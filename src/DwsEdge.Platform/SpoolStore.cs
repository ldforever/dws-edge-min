using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DwsEdge.Core.Config;
using DwsEdge.Core.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>
    /// 包裹仓库（V1 内存态）。
    ///
    /// 职责：把采集宿主写进 spool 的事件，按 traceId 合并成"一个包裹一条记录"，
    /// 对外提供查询、统计、相机状态和实时推送；图片只暴露路径与按需读取接口。
    /// 后续版本把内存换成 SQLite，接口保持不变。
    /// </summary>
    public sealed class SpoolStore
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, ParcelRecord> _byTrace =
            new Dictionary<string, ParcelRecord>(StringComparer.Ordinal);
        private readonly LinkedList<string> _order = new LinkedList<string>();
        private readonly Dictionary<string, CameraRecord> _cameras =
            new Dictionary<string, CameraRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<Channel<string>, byte> _subscribers =
            new ConcurrentDictionary<Channel<string>, byte>();
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions();
        private readonly ILogger<SpoolStore> _logger;
        private readonly HistoryStore _history;
        private readonly string _imagesRoot;
        private readonly int _maxRecords;

        private long _eventCount;
        private long _parcelCount;
        private long _noreadCount;
        private long _imageCount;
        private long _parseErrors;
        private long _missingTraceId;
        private long _traceIdConflicts;
        private int _pendingParcels;
        private long _cameraSessionId;
        private DateTime _lastMissingTraceLog = DateTime.MinValue;
        private long _imageFileCount;
        private long _imageDiskBytes;
        private long _diskTotalBytes;
        private long _diskFreeBytes;
        private int _diskUsedPercent;

        public SpoolStore(IConfiguration config, HistoryStore history, ILogger<SpoolStore> logger)
        {
            _logger = logger;
            _history = history;
            string root = config["Images:Root"] ?? "../images";
            _imagesRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, root));
            int max = 5000;
            int.TryParse(config["Images:MaxRecords"], out max);
            _maxRecords = Math.Max(100, max);

            // 平台重启后先从历史恢复最近数据，界面立刻有内容；spool 只读新增事件
            int loadDays = 2;
            int.TryParse(config["History:LoadDays"], out loadDays);
            LoadRecentHistory(Math.Max(1, loadDays));
        }

        public string ImagesRoot
        {
            get { return _imagesRoot; }
        }

        #region 写入：消费 spool 事件

        /// <summary>处理一条 spool 事件（只给 SpoolTailer 调用）。</summary>
        internal void Apply(SpoolEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            if (string.Equals(evt.type, "camera-status", StringComparison.OrdinalIgnoreCase))
            {
                ApplyCameraStatus(evt);
                return;
            }

            if (!string.Equals(evt.type, "parcel", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ParcelRecord record = null;
            bool isNew = false;
            bool becameReadable = false;

            lock (_sync)
            {
                _eventCount++;

                string key = evt.traceId;
                if (string.IsNullOrEmpty(key))
                {
                    // 事件没带追踪号：用 Core 的兜底规则生成（可追溯性较弱，计数并告警）
                    key = ParcelTrace.BuildFallback(evt.deviceId, evt.capturedAtMs);
                    _missingTraceId++;
                    if ((DateTime.UtcNow - _lastMissingTraceLog).TotalSeconds > 60)
                    {
                        _lastMissingTraceLog = DateTime.UtcNow;
                        _logger.LogWarning("收到 {0} 条缺少 traceId 的包裹事件，已用兜底键（相机+时间戳）；请检查采集宿主的追踪号生成", _missingTraceId);
                    }
                }

                if (!_byTrace.TryGetValue(key, out record))
                {
                    record = new ParcelRecord();
                    record.traceId = key;
                    record.deviceId = evt.deviceId;
                    record.capturedAtMs = evt.capturedAtMs;
                    record.time = FormatTime(evt.capturedAtMs > 0 ? evt.capturedAtMs : evt.receivedAtMs);
                    record.codes = new List<string>();
                    record.codeDetails = new List<CodeDetail>();
                    record.weightGrams = -1;
                    _byTrace[key] = record;
                    _order.AddLast(key);
                    isNew = true;
                }
                else
                {
                    bool hadCodes = record.codeCount > 0;
                    if (!hadCodes && evt.codes != null && evt.codes.Count > 0)
                    {
                        becameReadable = true;
                    }

                    // 冲突探测：同一追踪号下，新事件的条码和已有条码完全不相交 → 很可能是两个包裹被合并了
                    if (hadCodes && evt.codes != null && evt.codes.Count > 0 && !HasCommonCode(record, evt.codes))
                    {
                        _traceIdConflicts++;
                        _logger.LogWarning("追踪号冲突：{0} 上的条码 {1} 与已有 {2} 完全不相交",
                            key, CodesText(evt.codes), string.Join(",", record.codes.ToArray()));
                    }
                }

                if (evt.codes != null && evt.codes.Count > 0)
                {
                    for (int i = 0; i < evt.codes.Count; i++)
                    {
                        SpoolCode code = evt.codes[i];
                        if (code == null || string.IsNullOrEmpty(code.value))
                        {
                            continue;
                        }

                        CodeDetail existing = FindCodeDetail(record, code.value);
                        if (existing != null)
                        {
                            // 同一个码再次上报：把之前缺失的方位/类型补上
                            if (string.IsNullOrEmpty(existing.position) && !string.IsNullOrEmpty(code.position))
                            {
                                existing.position = code.position;
                            }
                            if (string.IsNullOrEmpty(existing.kind) && !string.IsNullOrEmpty(code.kind))
                            {
                                existing.kind = code.kind;
                            }
                            continue;
                        }

                        record.codes.Add(code.value);
                        CodeDetail detail = new CodeDetail();
                        detail.value = code.value;
                        detail.kind = code.kind;
                        detail.position = code.position;
                        record.codeDetails.Add(detail);
                    }
                }

                record.codeCount = record.codes.Count;
                if (evt.weightGrams > 0)
                {
                    record.weightGrams = evt.weightGrams;
                }
                if (evt.volumeMm3 > 0)
                {
                    record.volumeMm3 = evt.volumeMm3;
                }
                if (evt.lengthMm > 0) { record.lengthMm = evt.lengthMm; }
                if (evt.widthMm > 0) { record.widthMm = evt.widthMm; }
                if (evt.heightMm > 0) { record.heightMm = evt.heightMm; }
                if (!string.IsNullOrEmpty(evt.stage))
                {
                    record.stage = evt.stage;
                }

                // 分阶段 provider：看到 enriched 才算完整
                bool wasComplete = record.complete;
                if (evt.stagedResult.HasValue)
                {
                    record.staged = evt.stagedResult.Value;
                }
                record.complete = !record.staged
                    || string.Equals(record.stage, "enriched", StringComparison.OrdinalIgnoreCase);
                if (isNew && !record.complete)
                {
                    _pendingParcels++;
                }
                else if (!isNew && record.complete && !wasComplete)
                {
                    _pendingParcels = Math.Max(0, _pendingParcels - 1);
                }
                if (evt.images != null && evt.images.Count > 0)
                {
                    record.imageCount += evt.images.Count;
                    _imageCount += evt.images.Count;
                    for (int i = 0; i < evt.images.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(evt.images[i].path) && string.IsNullOrEmpty(record.firstImagePath))
                        {
                            record.firstImagePath = evt.images[i].path;
                        }
                    }
                }
                record.updates++;

                // 维持顺序：更新过的包裹挪到队尾，列表永远按"最近一次更新"倒序展示
                if (!isNew)
                {
                    _order.Remove(key);
                    _order.AddLast(key);
                }

                if (isNew)
                {
                    _parcelCount++;
                    if (record.codeCount == 0)
                    {
                        _noreadCount++;
                    }
                    else
                    {
                        BumpCameraCodeCount(evt.deviceId, record.time);
                    }
                }
                else if (becameReadable)
                {
                    _noreadCount = Math.Max(0, _noreadCount - 1);
                    BumpCameraCodeCount(evt.deviceId, record.time);
                }

                // 写一行历史快照（持久化），重启后不必再全量重放 spool
                _history.Append(record);

                Trim();
            }

            Publish(new { type = "parcel", data = record });
        }

        private void ApplyCameraStatus(SpoolEvent evt)
        {
            ApplyCameraStatus(evt, false);
        }

        /// <summary>按行读一个正在被别的进程追加写的文件（共享读）。</summary>
        private static IEnumerable<string> ReadLinesShared(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                while (!reader.EndOfStream)
                {
                    string line = reader.ReadLine();
                    if (line != null)
                    {
                        yield return line;
                    }
                }
            }
        }

        /// <summary>
        /// 平台启动时从 spool 回放相机状态（replay=true：不改"状态变化次数"，也不推 SSE）。
        ///
        /// 为什么要回放：相机状态在 spool 里是"当前值"语义，而消费位点保证的是"事件不重复处理"。
        /// 如果只按位点续读，平台单独重启后相机清单会一直空着，直到下一次上下线。
        /// 只扫最近两个事件文件，按出现顺序应用，结果就是每台相机的最终状态。
        /// </summary>
        public void RebuildCameraState(string spoolDirectory)
        {
            try
            {
                if (string.IsNullOrEmpty(spoolDirectory) || !Directory.Exists(spoolDirectory))
                {
                    return;
                }

                string[] files = Directory.GetFiles(spoolDirectory, "events-*.jsonl");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                int from = Math.Max(0, files.Length - 2);

                int applied = 0;
                for (int i = from; i < files.Length; i++)
                {
                    // 必须用 FileShare.ReadWrite 打开：spool 文件此刻正被采集宿主追加写
                    // （宿主用 FileShare.ReadWrite 打开，这里再用默认的 FileShare.Read 会直接 IOException）。
                    foreach (string line in ReadLinesShared(files[i]))
                    {
                        if (line.Length == 0 || line.IndexOf("camera-status", StringComparison.Ordinal) < 0)
                        {
                            continue;
                        }

                        SpoolEvent evt;
                        try
                        {
                            evt = JsonSerializer.Deserialize<SpoolEvent>(line, _json);
                        }
                        catch (JsonException)
                        {
                            continue;
                        }

                        if (evt != null && string.Equals(evt.type, "camera-status", StringComparison.OrdinalIgnoreCase))
                        {
                            ApplyCameraStatus(evt, true);
                            applied++;
                        }
                    }
                }

                if (applied > 0)
                {
                    _logger.LogInformation("已从 spool 回放相机状态 {0} 条（相机 {1} 台）", applied, _cameras.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("回放相机状态失败：{0}", ex.Message);
            }
        }

        private void ApplyCameraStatus(SpoolEvent evt, bool replay)
        {
            CameraRecord camera;
            lock (_sync)
            {
                string key = evt.deviceId ?? "unknown";
                bool isSnapshot = evt.isSnapshot.GetValueOrDefault(false);

                // 换了一轮采集宿主（会话号变了）就把上一轮的相机清掉：
                // 改了相机清单重启后，设备列表应该只有新清单里的相机，而不是新旧混在一起。
                if (evt.sessionId > 0 && evt.sessionId != _cameraSessionId)
                {
                    List<string> stale = new List<string>();
                    foreach (KeyValuePair<string, CameraRecord> pair in _cameras)
                    {
                        if (pair.Value.sessionId != evt.sessionId)
                        {
                            stale.Add(pair.Key);
                        }
                    }

                    for (int i = 0; i < stale.Count; i++)
                    {
                        _cameras.Remove(stale[i]);
                    }

                    if (stale.Count > 0)
                    {
                        _logger.LogInformation("采集宿主会话变更（{0} → {1}），已移除上一轮的相机 {2} 台",
                            _cameraSessionId, evt.sessionId, stale.Count);
                    }
                    _cameraSessionId = evt.sessionId;
                }

                if (!_cameras.TryGetValue(key, out camera))
                {
                    camera = new CameraRecord();
                    camera.deviceId = key;
                    _cameras[key] = camera;
                }

                camera.sessionId = evt.sessionId > 0 ? evt.sessionId : _cameraSessionId;

                if (evt.online.HasValue)
                {
                    camera.online = evt.online.Value;
                }
                camera.atMs = evt.atMs > 0 ? evt.atMs : evt.receivedAtMs;
                camera.lastChangeTime = FormatTime(camera.atMs);
                camera.userId = evt.userId;
                camera.fromSnapshot = isSnapshot;

                if (!string.IsNullOrEmpty(evt.model)) { camera.model = evt.model; }
                if (!string.IsNullOrEmpty(evt.serialNumber)) { camera.serialNumber = evt.serialNumber; }
                if (!string.IsNullOrEmpty(evt.vendor)) { camera.vendor = evt.vendor; }
                if (!string.IsNullOrEmpty(evt.firmware)) { camera.firmware = evt.firmware; }

                // A9：清单声明（ip/key/id）与安装方位。快照与增量事件都会带，
                // 空值不覆盖已知值——这样平台先收到增量、后收到快照也不会把身份弄丢。
                if (!string.IsNullOrEmpty(evt.declaredKind)) { camera.declaredKind = evt.declaredKind; }
                if (!string.IsNullOrEmpty(evt.declaredValue)) { camera.declaredValue = evt.declaredValue; }
                if (!string.IsNullOrEmpty(camera.declaredKind) && !string.IsNullOrEmpty(camera.declaredValue))
                {
                    camera.declaredLabel = camera.declaredKind + "=" + camera.declaredValue;
                }
                if (!string.IsNullOrEmpty(evt.position))
                {
                    camera.position = evt.position;
                    camera.positionLabel = CameraPositions.Label(evt.position);
                    camera.positionOrder = CameraPositions.Order(evt.position);
                }
                if (evt.discovered.HasValue)
                {
                    camera.discovered = evt.discovered.Value;
                }

                // 掉线/恢复计数取最大值：采集宿主重启后计数会从 0 开始，平台保留历史峰值
                if (evt.offlineCount > camera.offlineCount) { camera.offlineCount = evt.offlineCount; }
                if (evt.reconnectCount > camera.reconnectCount) { camera.reconnectCount = evt.reconnectCount; }
                if (evt.lastOfflineAtMs > camera.lastOfflineAtMs)
                {
                    camera.lastOfflineAtMs = evt.lastOfflineAtMs;
                    camera.lastOfflineTime = FormatTime(evt.lastOfflineAtMs);
                }
                if (evt.lastOfflineDurationMs > 0)
                {
                    camera.lastOfflineDurationMs = evt.lastOfflineDurationMs;
                    camera.lastOfflineDurationText =
                        (evt.lastOfflineDurationMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " 秒";
                }

                // 启动快照是基线，不计入"状态变化次数"
                if (!isSnapshot && !replay)
                {
                    camera.statusChanges++;
                }
            }

            if (!replay)
            {
                Publish(new { type = "camera", data = camera });
            }
        }

        /// <summary>按相机累计"出码包裹数"（调用方需持有 _sync 锁）。</summary>
        private void BumpCameraCodeCount(string deviceId, string time)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return;
            }

            CameraRecord camera;
            if (_cameras.TryGetValue(deviceId, out camera))
            {
                camera.codeCount++;
                camera.lastCodeTime = time;
            }
        }

        /// <summary>按条码值查找已记录的完整条码信息。</summary>
        private static CodeDetail FindCodeDetail(ParcelRecord record, string value)
        {
            if (record == null || record.codeDetails == null || string.IsNullOrEmpty(value))
            {
                return null;
            }
            for (int i = 0; i < record.codeDetails.Count; i++)
            {
                CodeDetail detail = record.codeDetails[i];
                if (detail != null && string.Equals(detail.value, value, StringComparison.Ordinal))
                {
                    return detail;
                }
            }
            return null;
        }

        /// <summary>新事件的条码是否与已记录的条码有交集（用于识别追踪号冲突）。</summary>
        private static bool HasCommonCode(ParcelRecord record, List<SpoolCode> codes)
        {
            if (record == null || record.codes == null || codes == null)
            {
                return false;
            }
            for (int i = 0; i < codes.Count; i++)
            {
                SpoolCode code = codes[i];
                if (code == null || string.IsNullOrEmpty(code.value))
                {
                    continue;
                }
                if (record.codes.Contains(code.value))
                {
                    return true;
                }
            }
            return false;
        }

        private static string CodesText(List<SpoolCode> codes)
        {
            List<string> values = new List<string>();
            if (codes != null)
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i] != null && !string.IsNullOrEmpty(codes[i].value))
                    {
                        values.Add(codes[i].value);
                    }
                }
            }
            return string.Join(",", values.ToArray());
        }

        internal void CountParseError()
        {
            lock (_sync)
            {
                _parseErrors++;
            }
        }

        /// <summary>由 StorageProbe 定期刷新图片目录与磁盘信息。</summary>
        public void UpdateStorage(long imageFileCount, long imageDiskBytes, long diskTotalBytes, long diskFreeBytes, int diskUsedPercent)
        {
            lock (_sync)
            {
                _imageFileCount = imageFileCount;
                _imageDiskBytes = imageDiskBytes;
                _diskTotalBytes = diskTotalBytes;
                _diskFreeBytes = diskFreeBytes;
                _diskUsedPercent = diskUsedPercent;
            }
        }

        private void Trim()
        {
            while (_order.Count > _maxRecords)
            {
                LinkedListNode<string> first = _order.First;
                if (first == null)
                {
                    break;
                }
                _order.RemoveFirst();
                _byTrace.Remove(first.Value);
            }
        }

        #endregion

        #region 历史加载与查询

        /// <summary>平台启动时从历史文件恢复最近 N 天的包裹，避免"重启后界面空白"。</summary>
        private void LoadRecentHistory(int days)
        {
            try
            {
                List<ParcelRecord> recent = _history.LoadRecent(days, _maxRecords);
                if (recent == null || recent.Count == 0)
                {
                    return;
                }

                int loaded = 0;
                lock (_sync)
                {
                    // LoadRecent 返回按时间倒序，这里反过来放，保证 LatestParcels 的先后顺序正确
                    for (int i = recent.Count - 1; i >= 0; i--)
                    {
                        ParcelRecord record = recent[i];
                        if (record == null || string.IsNullOrEmpty(record.traceId) || _byTrace.ContainsKey(record.traceId))
                        {
                            continue;
                        }

                        _byTrace[record.traceId] = record;
                        _order.AddLast(record.traceId);
                        _parcelCount++;
                        if (record.codeCount == 0)
                        {
                            _noreadCount++;
                        }
                        if (!record.complete)
                        {
                            _pendingParcels++;
                        }
                        _imageCount += record.imageCount;
                        loaded++;
                    }
                }

                _logger.LogInformation("已从历史恢复 {0} 个包裹（最近 {1} 天）", loaded, days);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "从历史恢复失败，本次按空白启动");
            }
        }

        /// <summary>历史查询：简单查询读内存，带条件或跨天的查询读历史文件。</summary>
        public List<ParcelRecord> QueryHistory(DateTime from, DateTime to, string code, string deviceId, bool? noread, int limit)
        {
            bool simpleQuery = string.IsNullOrEmpty(code) && string.IsNullOrEmpty(deviceId) && !noread.HasValue
                && (DateTime.Today - from.Date).TotalDays <= 1;
            if (simpleQuery)
            {
                return LatestParcels(limit);
            }
            return _history.Query(from, to, code, deviceId, noread, limit);
        }

        #endregion

        #region 读取：给 API 用

        public object Health()
        {
            return new
            {
                status = "ok",
                imagesRoot = _imagesRoot,
                serverTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }

        public PlatformStats Stats()
        {
            lock (_sync)
            {
                PlatformStats stats = new PlatformStats();
                stats.events = _eventCount;
                stats.parcels = _parcelCount;
                stats.noread = _noreadCount;
                stats.images = _imageCount;
                stats.parseErrors = _parseErrors;
                stats.pendingParcels = _pendingParcels;
                stats.missingTraceId = _missingTraceId;
                stats.traceIdConflicts = _traceIdConflicts;
                long readable = Math.Max(0, _parcelCount - _noreadCount);
                stats.readRate = _parcelCount == 0 ? 0 : Math.Round((double)readable / _parcelCount, 4);
                stats.camerasTotal = _cameras.Count;
                int online = 0;
                foreach (CameraRecord camera in _cameras.Values)
                {
                    if (camera.online)
                    {
                        online++;
                    }
                }
                stats.camerasOnline = online;
                stats.imageFileCount = _imageFileCount;
                stats.imageDiskBytes = _imageDiskBytes;
                stats.diskTotalBytes = _diskTotalBytes;
                stats.diskFreeBytes = _diskFreeBytes;
                stats.diskUsedPercent = _diskUsedPercent;
                stats.serverTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                return stats;
            }
        }

        public List<ParcelRecord> LatestParcels(int limit)
        {
            List<ParcelRecord> result = new List<ParcelRecord>();
            lock (_sync)
            {
                LinkedListNode<string> node = _order.Last;
                while (node != null && result.Count < limit)
                {
                    ParcelRecord record;
                    if (_byTrace.TryGetValue(node.Value, out record))
                    {
                        result.Add(record);
                    }
                    node = node.Previous;
                }
            }
            return result;
        }

        public List<CameraRecord> Cameras()
        {
            lock (_sync)
            {
                List<CameraRecord> list = new List<CameraRecord>(_cameras.Values);
                list.Sort(delegate(CameraRecord a, CameraRecord b)
                {
                    return string.Compare(a.deviceId, b.deviceId, StringComparison.OrdinalIgnoreCase);
                });
                return list;
            }
        }

        /// <summary>
        /// A9 设备信息页：相机清单 + 汇总 + 按方位聚合。
        ///
        /// 方位以磁盘上的 camera-positions.ini 为准（界面刚改过、采集宿主还没重启时，
        /// 这里显示的就是新方位；采集宿主重启后事件里也会带同样的值）。
        /// </summary>
        public CameraDeviceView DeviceList(string positionsPath)
        {
            CameraPositions fromFile = CameraPositions.Load(positionsPath);
            CameraDeviceView view = new CameraDeviceView();
            view.positionsFile = positionsPath;
            view.positionsFileExists = !string.IsNullOrEmpty(positionsPath) && File.Exists(positionsPath);
            view.positionOptions = new List<string>(CameraPositions.Options());
            view.cameras = new List<CameraRecord>();
            view.faces = new List<CameraFaceSummary>();

            List<CameraRecord> rows;
            lock (_sync)
            {
                rows = new List<CameraRecord>(_cameras.Values);
            }

            for (int i = 0; i < rows.Count; i++)
            {
                CameraRecord camera = rows[i];

                if (string.IsNullOrEmpty(camera.position))
                {
                    string fromMap = fromFile.ResolveAny(new string[]
                    {
                        camera.declaredValue, camera.deviceId, camera.serialNumber, camera.userId
                    });
                    if (!string.IsNullOrEmpty(fromMap))
                    {
                        camera.position = fromMap;
                        camera.positionLabel = CameraPositions.Label(fromMap);
                        camera.positionOrder = CameraPositions.Order(fromMap);
                    }
                }
                else
                {
                    // 界面上刚改过方位、采集宿主还没重启：以文件为准显示，并标记"待重启生效"
                    string fromMap = fromFile.ResolveAny(new string[]
                    {
                        camera.declaredValue, camera.deviceId, camera.serialNumber, camera.userId
                    });
                    if (!string.IsNullOrEmpty(fromMap)
                        && !string.Equals(fromMap, camera.position, StringComparison.OrdinalIgnoreCase))
                    {
                        camera.position = fromMap;
                        camera.positionLabel = CameraPositions.Label(fromMap);
                        camera.positionOrder = CameraPositions.Order(fromMap);
                        camera.positionPending = true;
                    }
                }
                if (string.IsNullOrEmpty(camera.positionLabel))
                {
                    camera.positionLabel = CameraPositions.Label(camera.position);
                }
                if (camera.positionOrder <= 0)
                {
                    camera.positionOrder = CameraPositions.Order(camera.position);
                }
                if (string.IsNullOrEmpty(camera.declaredLabel) && !string.IsNullOrEmpty(camera.declaredValue))
                {
                    camera.declaredLabel = camera.declaredKind + "=" + camera.declaredValue;
                }

                view.total++;
                if (camera.online)
                {
                    view.online++;
                }
                else
                {
                    view.offline++;
                }
                if (camera.discovered)
                {
                    view.discovered++;
                }
                else
                {
                    view.declaredMissing++;
                }
                if (string.IsNullOrEmpty(camera.position))
                {
                    view.positionMissing++;
                }
            }

            view.cameras.AddRange(rows);

            // 排序：先按方位（顶→底→左→右→前→后→线体→备用→未设置），同方位再按标识
            view.cameras.Sort(delegate(CameraRecord a, CameraRecord b)
            {
                int byPosition = a.positionOrder.CompareTo(b.positionOrder);
                if (byPosition != 0)
                {
                    return byPosition;
                }
                return string.Compare(a.deviceId, b.deviceId, StringComparison.OrdinalIgnoreCase);
            });

            // 按方位聚合
            Dictionary<string, CameraFaceSummary> faces =
                new Dictionary<string, CameraFaceSummary>(StringComparer.OrdinalIgnoreCase);
            string[] options = CameraPositions.Options();
            for (int i = 0; i < options.Length; i++)
            {
                CameraFaceSummary face = new CameraFaceSummary();
                face.position = options[i];
                face.label = CameraPositions.Label(options[i]);
                face.cameras = new List<string>();
                faces[options[i]] = face;
            }

            for (int i = 0; i < view.cameras.Count; i++)
            {
                CameraRecord camera = view.cameras[i];
                string key = CameraPositions.Normalize(camera.position);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                CameraFaceSummary face;
                if (!faces.TryGetValue(key, out face))
                {
                    continue; // 非标准方位（厂商自定义值）不参与六面概览，但仍在清单里显示
                }

                face.total++;
                if (camera.online)
                {
                    face.online++;
                }
                else
                {
                    face.offline++;
                }
                face.cameras.Add(string.IsNullOrEmpty(camera.declaredValue) ? camera.deviceId : camera.declaredValue);
            }

            for (int i = 0; i < options.Length; i++)
            {
                CameraFaceSummary face = faces[options[i]];
                if (face.total > 0)
                {
                    view.faces.Add(face);
                }
            }

            return view;
        }

        /// <summary>按需读取本地图片；只允许读取配置的图片根目录下的文件。</summary>
        public IResult OpenImage(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return Results.BadRequest(new { error = "path 不能为空" });
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return Results.BadRequest(new { error = "path 非法" });
            }

            string root = _imagesRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "只允许访问图片目录内的文件" });
            }
            if (!File.Exists(full))
            {
                return Results.NotFound(new { error = "图片不存在" });
            }

            string ext = Path.GetExtension(full).ToLowerInvariant();
            string contentType = "image/jpeg";
            if (ext == ".png")
            {
                contentType = "image/png";
            }
            else if (ext == ".bmp")
            {
                contentType = "image/bmp";
            }
            return Results.File(full, contentType, enableRangeProcessing: true);
        }

        #endregion

        #region 实时推送（SSE）

        private void Publish(object payload)
        {
            string json = JsonSerializer.Serialize(payload, _json);
            foreach (KeyValuePair<Channel<string>, byte> item in _subscribers)
            {
                item.Key.Writer.TryWrite(json);
            }
        }

        public async Task StreamAsync(HttpContext context, CancellationToken token)
        {
            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await context.Response.Body.FlushAsync(token);

            Channel<string> channel = Channel.CreateUnbounded<string>();
            _subscribers.TryAdd(channel, 0);
            _logger.LogInformation("SSE 客户端接入，当前订阅数 {0}", _subscribers.Count);

            try
            {
                // 先补发最近 20 条，避免新打开的页面空白
                List<ParcelRecord> recent = LatestParcels(20);
                for (int i = recent.Count - 1; i >= 0; i--)
                {
                    string json = JsonSerializer.Serialize(new { type = "parcel", data = recent[i] }, _json);
                    await context.Response.WriteAsync("data: " + json + "\n\n", token);
                }
                string stats = JsonSerializer.Serialize(new { type = "stats", data = Stats() }, _json);
                await context.Response.WriteAsync("data: " + stats + "\n\n", token);

                // 相机状态也补发一次：前端可能是"页面刚打开、相机状态早就上报过"的情况，
                // 只靠增量事件会一直空着（尤其是平台刚重启、宿主没重启的时候）。
                List<CameraRecord> cameras = Cameras();
                for (int i = 0; i < cameras.Count; i++)
                {
                    string camera = JsonSerializer.Serialize(new { type = "camera", data = cameras[i] }, _json);
                    await context.Response.WriteAsync("data: " + camera + "\n\n", token);
                }
                await context.Response.Body.FlushAsync(token);

                while (!token.IsCancellationRequested)
                {
                    string message;
                    try
                    {
                        message = await channel.Reader.ReadAsync(token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    await context.Response.WriteAsync("data: " + message + "\n\n", token);
                    await context.Response.Body.FlushAsync(token);
                }
            }
            catch (Exception)
            {
                // 客户端断开属于正常情况
            }
            finally
            {
                _subscribers.TryRemove(channel, out _);
            }
        }

        #endregion

        private static string FormatTime(long unixMs)
        {
            if (unixMs <= 0)
            {
                return string.Empty;
            }
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
        }
    }
}
