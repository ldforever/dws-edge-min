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
using DwsEdge.Core.Rules;
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
        /// <summary>
        /// traceId → 已经处理过的事件指纹（B1 去重）。
        /// 指纹两种形态：<c>id:&lt;eventId&gt;</c>（宿主的事件号）和
        /// <c>fp:&lt;阶段|条码|重量|体积|图片&gt;</c>（内容指纹，宿主没给事件号时靠它）。
        /// </summary>
        private readonly Dictionary<string, HashSet<string>> _appliedByTrace =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<Channel<string>, byte> _subscribers =
            new ConcurrentDictionary<Channel<string>, byte>();
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions();
        private readonly ILogger<SpoolStore> _logger;
        private readonly HistoryStore _history;
        private readonly BarcodeRuleStore _rules;
        private readonly DedupStore _dedup;
        /// <summary>B8 相机状态监控（在线率 / 掉线记录 / 心跳 / 告警）。</summary>
        private readonly CameraMonitor _monitor;
        private readonly string _imagesRoot;
        private readonly string _runtimeRoot;
        private ThumbnailService _thumbs;
        private readonly int _maxRecords;

        /// <summary>最近被规则丢弃的条码（诊断用，环形缓冲，最新在前）。</summary>
        private readonly LinkedList<FilteredCodeRecord> _filteredRecent = new LinkedList<FilteredCodeRecord>();
        private const int FilteredRecentCapacity = 200;

        private long _eventCount;
        private long _parcelCount;
        private long _noreadCount;
        private long _imageCount;
        private long _parseErrors;
        private long _missingTraceId;
        private long _traceIdConflicts;
        private long _duplicateEvents;
        private long _mergedParcels;
        private long _publishedParcels;
        private long _filteredCodes;
        private long _filteredToNoread;
        private int _pendingParcels;
        private int _dispatchPending;
        private int _dispatchSent;
        private int _dispatchFailed;
        private long _cameraSessionId;
        private DateTime _lastMissingTraceLog = DateTime.MinValue;
        private DateTime _lastDuplicateLog = DateTime.MinValue;
        private long _imageFileCount;
        private long _imageDiskBytes;
        private long _diskTotalBytes;
        private long _diskFreeBytes;
        private int _diskUsedPercent;
        /// <summary>C2：上一次推"相机计数"的时间（节流用）。</summary>
        private long _lastCounterPublishMs;

        public SpoolStore(IConfiguration config, HistoryStore history, BarcodeRuleStore rules,
            DedupStore dedup, CameraMonitor monitor, ILogger<SpoolStore> logger)
        {
            _logger = logger;
            _history = history;
            _rules = rules;
            _dedup = dedup;
            _monitor = monitor;
            // C2：把"出码计数"的权威值借给监控模块 —— 相机状态墙要把在线率和出码数放一起看
            if (_monitor != null)
            {
                _monitor.CounterProvider = CameraCounterOf;
            }
            string root = config["Images:Root"] ?? "../images";
            _imagesRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, root));
            _runtimeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, config["Runtime:Root"] ?? ".."));
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

            // 去重指纹用"过滤前"的内容：这样以后改了规则，也不会把已经处理过的事件重新当成新事件。
            string fingerprint = Fingerprint(evt);

            // ---- B2：条码过滤（命中"丢弃"规则的码在这里被摘掉，后面按"没有这个码"处理）----
            List<FilteredCodeRecord> filtered = FilterCodes(evt);

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

                // ---- B1 去重：同一个包裹、同一个阶段、同样内容的事件又来一遍 ----
                // 触发场景：provider 重发、spool 文件被重读（例如平台非正常退出后回退到上一个位点）、
                // 人工补投同一条事件。判定重复后：不改任何计数、不写历史、不推送。
                // 事件号带上阶段一起做键：宿主重启后事件号会从小数字重来，
                // 只按数字比较有可能把"同一包裹的另一次回调"误判成重复。
                string idKey = evt.eventId > 0
                    ? "id:" + evt.eventId.ToString(CultureInfo.InvariantCulture) + "|" + (evt.stage ?? "")
                    : null;
                HashSet<string> applied;
                if (!_appliedByTrace.TryGetValue(key, out applied))
                {
                    applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _appliedByTrace[key] = applied;
                }

                bool isDuplicate = applied.Contains(fingerprint)
                    || (idKey != null && applied.Contains(idKey));
                if (isDuplicate)
                {
                    _duplicateEvents++;
                    if ((DateTime.UtcNow - _lastDuplicateLog).TotalSeconds > 30)
                    {
                        _lastDuplicateLog = DateTime.UtcNow;
                        _logger.LogWarning("重复事件已丢弃（累计 {0} 条）：traceId={1} 阶段={2} 事件号={3}；同一包裹只会保留一条记录、只下发一次",
                            _duplicateEvents, key, evt.stage, evt.eventId);
                    }
                    return;
                }

                applied.Add(fingerprint);
                if (idKey != null)
                {
                    applied.Add(idKey);
                }
                // 归档内容指纹（WAL 追加写）：平台重启后重读 spool 时，靠它把同一条回调继续判成重复
                _dedup.Append(key, fingerprint);

                // B2：把这次被规则丢掉的条码记下来（累计 + 最近列表 + 挂到包裹上，方便解释 NOREAD）
                if (filtered.Count > 0)
                {
                    _filteredCodes += filtered.Count;
                    for (int i = 0; i < filtered.Count; i++)
                    {
                        FilteredCodeRecord item = filtered[i];
                        item.traceId = key;
                        item.deviceId = evt.deviceId;
                        item.time = FormatTime(evt.capturedAtMs > 0 ? evt.capturedAtMs : evt.receivedAtMs);
                        _filteredRecent.AddFirst(item);
                    }
                    while (_filteredRecent.Count > FilteredRecentCapacity)
                    {
                        _filteredRecent.RemoveLast();
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
                    record.filteredCodes = new List<FilteredCodeRecord>();
                    record.weightGrams = -1;

                    // 新建记录也要把这次被规则丢掉的码挂上去（否则界面解释不了"NOREAD 为什么没码"）
                    if (filtered.Count > 0)
                    {
                        record.filteredCodes.AddRange(filtered);
                    }

                    // B1：下发状态只在这里置一次，后面无论合并多少次回调都不会再动它，
                    // 所以同一个包裹在下游那边天然只会"入队一次"。
                    record.dispatchState = DispatchPending;
                    _byTrace[key] = record;
                    _order.AddLast(key);
                    _dispatchPending++;
                    isNew = true;

                    // 条码全被规则丢掉 → 这个包裹就是"无码包裹"，单独记一笔（现场最常见的问题）
                    if (filtered.Count > 0 && (evt.codes == null || evt.codes.Count == 0))
                    {
                        _filteredToNoread++;
                    }
                }
                else
                {
                    bool hadCodes = record.codeCount > 0;
                    if (filtered.Count > 0 && record.filteredCodes != null)
                    {
                        record.filteredCodes.AddRange(filtered);
                    }
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

                // B1 观测：一个包裹收到第 2 次有效回调 = "先条码后重量体积"合并成功
                if (record.updates == 2)
                {
                    _mergedParcels++;
                }

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

            // B8 心跳：这个相机"刚刚交出过数据"。出码是最强的心跳信号（比状态事件更能说明采集链路是通的），
            // 放在锁外面调用，避免在持有 _sync 的时候再进监控模块的锁。
            if (_monitor != null)
            {
                _monitor.ApplyCode(evt.deviceId, evt.capturedAtMs > 0 ? evt.capturedAtMs : evt.receivedAtMs);
                // C2：出码计数变了要马上让界面看到（相机状态墙的"这台相机在不在干活"就靠它）。
                // 内部有节流：密集过包时最多每 0.7 秒推一次，剩下的由 B8 的 5 秒快照兜底。
                PublishCameraCounters();
            }

            _publishedParcels++;
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
            string key = evt.deviceId ?? "unknown";
            bool isSnapshot = evt.isSnapshot.GetValueOrDefault(false);
            CameraRecord camera;
            lock (_sync)
            {
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

            // B8 把同一条状态喂给监控模块：在线率、掉线记录、心跳、告警都以它为准。
            // 放在锁外面调用（监控模块自己加锁），避免两把锁互相等。
            if (_monitor != null)
            {
                _monitor.ApplyStatus(key, camera.online, camera.atMs, isSnapshot, replay,
                    camera.declaredLabel, camera.position, camera.discovered);
            }

            if (!replay)
            {
                Publish(new { type = "camera", data = camera });
            }
        }

        /// <summary>
        /// B2：按当前规则过滤事件里的条码，命中"丢弃"的直接从事件上摘掉。
        /// 返回被摘掉的条码明细（traceId/时间由调用方补）。
        ///
        /// 没有配置任何规则时直接返回 —— 现场不用规则就一点额外开销都没有。
        /// </summary>
        private List<FilteredCodeRecord> FilterCodes(SpoolEvent evt)
        {
            List<FilteredCodeRecord> dropped = new List<FilteredCodeRecord>();
            if (evt.codes == null || evt.codes.Count == 0 || _rules == null)
            {
                return dropped;
            }

            BarcodeFilter filter = _rules.Filter;
            BarcodeRuleSet set = filter != null ? filter.RuleSet : null;
            if (filter == null || set == null || set.rules == null || set.rules.Count == 0)
            {
                return dropped;
            }

            for (int i = evt.codes.Count - 1; i >= 0; i--)
            {
                SpoolCode code = evt.codes[i];
                if (code == null || string.IsNullOrEmpty(code.value))
                {
                    continue;
                }

                BarcodeDecision decision = filter.Decide(code.value);
                if (decision.kept)
                {
                    continue;
                }

                FilteredCodeRecord item = new FilteredCodeRecord();
                item.code = code.value;
                item.rule = decision.matchedRule;
                item.reason = decision.reason;
                dropped.Add(item);
                evt.codes.RemoveAt(i);
            }

            if (dropped.Count > 0)
            {
                dropped.Reverse();   // 还原成原来的顺序，日志更好看
            }
            return dropped;
        }

        /// <summary>B2：最近被规则丢弃的条码（诊断用）。</summary>
        public List<FilteredCodeRecord> RecentFilteredCodes(int limit)
        {
            List<FilteredCodeRecord> result = new List<FilteredCodeRecord>();
            lock (_sync)
            {
                LinkedListNode<FilteredCodeRecord> node = _filteredRecent.First;
                while (node != null && result.Count < limit)
                {
                    result.Add(node.Value);
                    node = node.Next;
                }
            }
            return result;
        }

        private const string DispatchPending = "pending";
        private const string DispatchSent = "sent";
        private const string DispatchFailed = "failed";

        /// <summary>
        /// 事件内容指纹（B1 去重的依据）。
        /// 组成：阶段 + 条码集合 + 重量 + 体积/尺寸 + 图片数量与首图路径。
        /// 刻意不含 receivedAtMs / eventId 这类"每次送达都会变"的字段 ——
        /// 同一个包裹的第二次回调（enriched 带重量体积）阶段不同，指纹自然不同，不会被误判成重复。
        /// </summary>
        private static string Fingerprint(SpoolEvent evt)
        {
            StringBuilder sb = new StringBuilder("fp:");
            sb.Append(evt.stage ?? string.Empty).Append('|');

            if (evt.codes != null && evt.codes.Count > 0)
            {
                List<string> values = new List<string>();
                for (int i = 0; i < evt.codes.Count; i++)
                {
                    if (evt.codes[i] != null && !string.IsNullOrEmpty(evt.codes[i].value))
                    {
                        values.Add(evt.codes[i].value);
                    }
                }
                values.Sort(StringComparer.OrdinalIgnoreCase);
                sb.Append(string.Join(",", values.ToArray()));
            }

            sb.Append('|').Append(evt.weightGrams)
              .Append('|').Append(evt.lengthMm.ToString(CultureInfo.InvariantCulture))
              .Append('x').Append(evt.widthMm.ToString(CultureInfo.InvariantCulture))
              .Append('x').Append(evt.heightMm.ToString(CultureInfo.InvariantCulture))
              .Append('|');

            if (evt.images != null && evt.images.Count > 0)
            {
                List<string> paths = new List<string>();
                for (int i = 0; i < evt.images.Count; i++)
                {
                    if (evt.images[i] != null && !string.IsNullOrEmpty(evt.images[i].path))
                    {
                        paths.Add(evt.images[i].path);
                    }
                }
                paths.Sort(StringComparer.OrdinalIgnoreCase);
                sb.Append(evt.images.Count).Append(':').Append(string.Join(",", paths.ToArray()));
            }
            else
            {
                sb.Append('0');
            }

            return sb.ToString();
        }

        /// <summary>
        /// 从历史快照反推指纹（B1：重启后继续去重用）。
        ///
        /// asDetected = false：按记录当前状态还原（一般是第二次回调，带重量体积）；
        /// asDetected = true ：按"第一次回调"的形态还原（只有条码 + 原图，没有重量体积）——
        /// 这样平台重启后重读 spool，两条回调都能被判成重复。
        ///
        /// 注意：这是"能还原多少就还原多少"的近似，不是全量持久化指纹表。
        /// 覆盖的是现场最常见的"一次条码 + 一次重量体积"两段式回调。
        /// </summary>
        private static string HistoryFingerprint(ParcelRecord record, bool asDetected)
        {
            SpoolEvent stub = new SpoolEvent();
            stub.stage = asDetected ? "detected" : record.stage;
            stub.weightGrams = asDetected ? -1 : record.weightGrams;
            stub.lengthMm = asDetected ? 0 : record.lengthMm;
            stub.widthMm = asDetected ? 0 : record.widthMm;
            stub.heightMm = asDetected ? 0 : record.heightMm;
            stub.codes = new List<SpoolCode>();
            if (record.codes != null)
            {
                for (int i = 0; i < record.codes.Count; i++)
                {
                    SpoolCode code = new SpoolCode();
                    code.value = record.codes[i];
                    stub.codes.Add(code);
                }
            }
            stub.images = new List<SpoolImage>();
            if (!string.IsNullOrEmpty(record.firstImagePath))
            {
                SpoolImage image = new SpoolImage();
                image.path = record.firstImagePath;
                stub.images.Add(image);
            }
            return Fingerprint(stub);
        }

        /// <summary>
        /// B1 幂等下发：取出"待下发"的包裹（下游 TCP/HTTP 模块从这里取）。
        /// 一个 traceId 只会出现一次 —— 记录创建时就定了 dispatchState=pending，
        /// 后面合并多少次回调都不会再改变它的入队资格。
        /// </summary>
        public List<ParcelRecord> PendingDispatch(int limit)
        {
            List<ParcelRecord> result = new List<ParcelRecord>();
            lock (_sync)
            {
                LinkedListNode<string> node = _order.First;
                while (node != null && result.Count < limit)
                {
                    ParcelRecord record;
                    if (_byTrace.TryGetValue(node.Value, out record)
                        && IsDispatchable(record))
                    {
                        result.Add(record);
                    }
                    node = node.Next;
                }
            }
            return result;
        }

        /// <summary>待下发的包裹：状态是 pending/failed，并且已经"完整"（分阶段 provider 要等重量体积）。</summary>
        public int PendingDispatchCount()
        {
            lock (_sync)
            {
                int count = 0;
                foreach (ParcelRecord record in _byTrace.Values)
                {
                    if (IsDispatchable(record))
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        /// <summary>待下发的包裹：状态是 pending/failed，并且已经"完整"（分阶段 provider 要等重量体积）。</summary>
        private static bool IsDispatchable(ParcelRecord record)
        {
            if (record == null || record.complete != true)
            {
                return false;
            }
            return string.IsNullOrEmpty(record.dispatchState)
                || string.Equals(record.dispatchState, DispatchPending, StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.dispatchState, DispatchFailed, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 下游回报下发结果（B4/B6 调用）。幂等：
        /// 同一个 traceId 重复 ack 不会重复计数，已经 sent 的不会被改回 pending。
        /// </summary>
        public object AckDispatch(string traceId, bool success, string error)
        {
            if (string.IsNullOrEmpty(traceId))
            {
                return new { ok = false, error = "traceId 不能为空" };
            }

            lock (_sync)
            {
                ParcelRecord record;
                if (!_byTrace.TryGetValue(traceId, out record))
                {
                    return new { ok = false, error = "没有这个包裹：" + traceId };
                }

                if (string.Equals(record.dispatchState, DispatchSent, StringComparison.OrdinalIgnoreCase))
                {
                    // 已经下发成功过：直接返回 ok，不重复计数
                    return new { ok = true, alreadySent = true, state = record.dispatchState };
                }

                // 先按"旧状态"把对应的计数减掉，再按"新状态"加回去 ——
                // 状态之间的迁移（pending→failed、failed→sent…）才不会把计数弄乱。
                DecrementDispatchCounter(record.dispatchState);

                if (success)
                {
                    record.dispatchState = DispatchSent;
                    record.dispatchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    record.dispatchError = null;
                    _dispatchSent++;
                }
                else
                {
                    record.dispatchState = DispatchFailed;
                    record.dispatchError = error;
                    _dispatchFailed++;
                }

                record.dispatchAttempts++;
                _history.Append(record);
                return new { ok = true, state = record.dispatchState, attempts = record.dispatchAttempts };
            }
        }

        /// <summary>把某个下发状态对应的计数减 1（状态迁移时用）。</summary>
        private void DecrementDispatchCounter(string state)
        {
            if (string.IsNullOrEmpty(state)
                || string.Equals(state, DispatchPending, StringComparison.OrdinalIgnoreCase))
            {
                _dispatchPending = Math.Max(0, _dispatchPending - 1);
            }
            else if (string.Equals(state, DispatchSent, StringComparison.OrdinalIgnoreCase))
            {
                _dispatchSent = Math.Max(0, _dispatchSent - 1);
            }
            else if (string.Equals(state, DispatchFailed, StringComparison.OrdinalIgnoreCase))
            {
                _dispatchFailed = Math.Max(0, _dispatchFailed - 1);
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
                _appliedByTrace.Remove(first.Value);
            }
        }

        #endregion

        #region 历史加载与查询

        /// <summary>平台启动时从历史文件恢复最近 N 天的包裹，避免"重启后界面空白"。</summary>
        private void LoadRecentHistory(int days)
        {
            try
            {
                // B1：先把"已处理事件指纹"从归档里读回来 —— 这是重启后还能识别重复上报的关键。
                // 归档按 traceId 一条，启动时自动整理（合并 WAL、清理过期），不会随运行时长膨胀。
                Dictionary<string, HashSet<string>> appliedHistory = _dedup.Load();
                if (appliedHistory.Count > 0)
                {
                    foreach (KeyValuePair<string, HashSet<string>> pair in appliedHistory)
                    {
                        _appliedByTrace[pair.Key] = pair.Value;
                    }
                    _logger.LogInformation("已恢复 {0} 个包裹的去重指纹（重复上报不会被重复计数）", appliedHistory.Count);
                }

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

                        // B1：把恢复出来的包裹登记成"已处理指纹"，这样平台重启后如果 spool 又被重读
                        // （位点回退、offsets.json 丢失），已经处理过的回调不会被再算一次。
                        // 注意是"合并"而不是覆盖 —— 上面已经从 applied-*.jsonl 读回了精确指纹，
                        // 这里再补两种形态（当前状态 / 第一次回调）作为兜底。
                        HashSet<string> applied;
                        if (!_appliedByTrace.TryGetValue(record.traceId, out applied))
                        {
                            applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _appliedByTrace[record.traceId] = applied;
                        }
                        applied.Add(HistoryFingerprint(record, false));
                        applied.Add(HistoryFingerprint(record, true));

                        _parcelCount++;
                        if (record.codeCount == 0)
                        {
                            _noreadCount++;
                        }
                        if (!record.complete)
                        {
                            _pendingParcels++;
                        }
                        if (record.updates >= 2)
                        {
                            _mergedParcels++;
                        }
                        if (string.IsNullOrEmpty(record.dispatchState)
                            || string.Equals(record.dispatchState, DispatchPending, StringComparison.OrdinalIgnoreCase))
                        {
                            _dispatchPending++;
                        }
                        else if (string.Equals(record.dispatchState, DispatchSent, StringComparison.OrdinalIgnoreCase))
                        {
                            _dispatchSent++;
                        }
                        else
                        {
                            _dispatchFailed++;
                        }
                        _imageCount += record.imageCount;
                        loaded++;
                    }
                }

                _logger.LogInformation("已从历史恢复 {0} 个包裹（最近 {1} 天）", loaded, days);

                // B3：把"已经封盘的过去几天"整理成索引，之后历史查询就走索引（10 万条级别也能秒回）
                _history.CompactClosedDays(Math.Max(1, days));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "从历史恢复失败，本次按空白启动");
            }
        }

        /// <summary>
        /// 历史查询：一律走历史库（带索引）。
        /// 早先这里对"简单查询"做了内存快捷路径，但那会按内存里的"最近 N 条"返回，
        /// 跨天时数据不对、总数也是假的 —— 索引已经让查询足够快，不值得为省这点时间牺牲正确性。
        /// </summary>
        public HistoryQueryResult QueryHistory(HistoryQuery query)
        {
            return _history.Query(query);
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
                stats.duplicateEvents = _duplicateEvents;
                stats.mergedParcels = _mergedParcels;
                stats.publishedParcels = _publishedParcels;
                stats.dispatchPending = _dispatchPending;
                stats.dispatchSent = _dispatchSent;
                stats.dispatchFailed = _dispatchFailed;
                stats.filteredCodes = _filteredCodes;
                stats.filteredToNoread = _filteredToNoread;
                BarcodeRuleSet set = _rules != null ? _rules.Current : null;
                stats.ruleCount = set != null ? set.EnabledByPriority().Count : 0;
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
        public string ResolveImagePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return null;
            }

            string root = _imagesRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return File.Exists(full) ? full : null;
        }

        /// <summary>
        /// B7：按需取缩略图。BMP 原图会真的缩小（纯 C# 降采样，结果缓存到 cache\thumbs），
        /// JPEG 原图因为离线环境没有解码库，回退成直接返回原图（响应头里会标注）。
        /// 整个过程都在平台进程里做，采集进程一点不受影响。
        /// </summary>
        public IResult OpenThumbnail(string path, int width)
        {
            string full = ResolveImagePath(path);
            if (full == null)
            {
                return Results.BadRequest(new { error = "只允许访问图片目录内已存在的文件" });
            }

            if (_thumbs == null)
            {
                _thumbs = new ThumbnailService(Path.Combine(_runtimeRoot, "cache", "thumbs"),
                    delegate(string message, bool warning) { _logger.LogWarning(message); });
            }

            ThumbnailService.ThumbResult result = _thumbs.Get(full, width);
            return Results.File(result.bytes, result.format == "bmp" ? "image/bmp" : "image/jpeg",
                lastModified: File.GetLastWriteTimeUtc(full), enableRangeProcessing: false);
        }

        /// <summary>B7：图片元信息（尺寸/大小/后缀），前端用来决定怎么显示。</summary>
        public object ImageInfo(string path)
        {
            string full = ResolveImagePath(path);
            if (full == null)
            {
                return new { ok = false, error = "只允许访问图片目录内已存在的文件" };
            }

            FileInfo info = new FileInfo(full);
            return new
            {
                ok = true,
                path = full,
                name = info.Name,
                bytes = info.Length,
                modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                extension = info.Extension.TrimStart('.').ToLowerInvariant(),
                thumbSupported = info.Extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            };
        }

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

        /// <summary>B8：告警产生/恢复时推给界面（界面顶部告警条用它实时更新）。</summary>
        public void PublishAlert(CameraAlert alert)
        {
            if (alert == null)
            {
                return;
            }
            Publish(new { type = "alert", data = alert });
        }

        /// <summary>B8：把监控快照（汇总 + 每台指标 + 活动告警）推给界面。</summary>
        public void PublishMonitor(CameraMonitor monitor)
        {
            if (monitor == null)
            {
                return;
            }
            Publish(new { type = "monitor", data = monitor.Snapshot(50) });
        }

        /// <summary>
        /// C2：某台相机的"计数类"权威值（出码数等），供监控模块带进相机状态墙。
        /// 由 CameraMonitor.CounterProvider 回调进来，所以调用方可能正持有 monitor 的锁 ——
        /// 这里只拿 _sync，不会反向再进 monitor，加锁顺序是单向的。
        /// </summary>
        private CameraCounter CameraCounterOf(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return null;
            }

            lock (_sync)
            {
                CameraRecord camera;
                if (!_cameras.TryGetValue(deviceId, out camera))
                {
                    return null;
                }

                CameraCounter counter = new CameraCounter();
                counter.camera = camera.deviceId;
                counter.codeCount = camera.codeCount;
                counter.lastCodeTime = camera.lastCodeTime;
                counter.offlineCount = camera.offlineCount;
                counter.online = camera.online;
                counter.discovered = camera.discovered;
                counter.position = camera.position;
                counter.positionLabel = camera.positionLabel;
                counter.model = camera.model;
                counter.serialNumber = camera.serialNumber;
                return counter;
            }
        }

        /// <summary>所有相机的计数（接口用；顺序与界面的相机墙一致：按方位、再按相机名）。</summary>
        public List<CameraCounter> CameraCounters()
        {
            List<CameraCounter> list = new List<CameraCounter>();
            List<string> keys = new List<string>();
            lock (_sync)
            {
                foreach (KeyValuePair<string, CameraRecord> pair in _cameras)
                {
                    keys.Add(pair.Key);
                }
            }
            for (int i = 0; i < keys.Count; i++)
            {
                CameraCounter counter = CameraCounterOf(keys[i]);
                if (counter != null)
                {
                    list.Add(counter);
                }
            }
            list.Sort(delegate(CameraCounter a, CameraCounter b)
            {
                int orderA = PositionOrderOf(a.position);
                int orderB = PositionOrderOf(b.position);
                if (orderA != orderB) { return orderA.CompareTo(orderB); }
                return string.Compare(a.camera, b.camera, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        private static int PositionOrderOf(string position)
        {
            return string.IsNullOrEmpty(position) ? 99 : CameraPositions.Order(position);
        }

        /// <summary>
        /// C2：把"出码计数"这类会随手一包就变的轻量数据单独推一条（`type=camera-count`），
        /// 比每次重推整份监控快照省带宽。节流 300ms：密集过包时中间几包会被合并掉，
        /// 但"最后的值"一定会通过 B8 的周期快照（默认 5 秒）补上，所以验收要求的 5 秒内刷新有两条路保底。
        /// </summary>
        public void PublishCameraCounters()
        {
            if (_subscribers.IsEmpty)
            {
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastCounterPublishMs < 300)
            {
                return;
            }
            _lastCounterPublishMs = now;

            Publish(new { type = "camera-count", data = CameraCounters() });
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

                // B8：监控快照（在线率/心跳/活动告警）也补一份，页面一打开就有数
                if (_monitor != null)
                {
                    string snapshot = JsonSerializer.Serialize(
                        new { type = "monitor", data = _monitor.Snapshot(50) }, _json);
                    await context.Response.WriteAsync("data: " + snapshot + "\n\n", token);
                }

                // C2：相机计数（出码数）补一份 —— 状态墙上的"出码"不能等下一次过包才出现
                string counters = JsonSerializer.Serialize(
                    new { type = "camera-count", data = CameraCounters() }, _json);
                await context.Response.WriteAsync("data: " + counters + "\n\n", token);
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
