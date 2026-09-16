using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
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
        private readonly string _imagesRoot;
        private readonly int _maxRecords;

        private long _eventCount;
        private long _parcelCount;
        private long _noreadCount;
        private long _imageCount;
        private long _parseErrors;

        public SpoolStore(IConfiguration config, ILogger<SpoolStore> logger)
        {
            _logger = logger;
            string root = config["Images:Root"] ?? "../images";
            _imagesRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, root));
            int max = 5000;
            int.TryParse(config["Images:MaxRecords"], out max);
            _maxRecords = Math.Max(100, max);
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

                string key = string.IsNullOrEmpty(evt.traceId)
                    ? (evt.deviceId + "|" + evt.capturedAtMs)
                    : evt.traceId;

                if (!_byTrace.TryGetValue(key, out record))
                {
                    record = new ParcelRecord();
                    record.traceId = key;
                    record.deviceId = evt.deviceId;
                    record.capturedAtMs = evt.capturedAtMs;
                    record.time = FormatTime(evt.capturedAtMs > 0 ? evt.capturedAtMs : evt.receivedAtMs);
                    record.codes = new List<string>();
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
                }

                if (evt.codes != null && evt.codes.Count > 0)
                {
                    for (int i = 0; i < evt.codes.Count; i++)
                    {
                        string value = evt.codes[i] == null ? null : evt.codes[i].value;
                        if (!string.IsNullOrEmpty(value) && !record.codes.Contains(value))
                        {
                            record.codes.Add(value);
                        }
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
                if (!string.IsNullOrEmpty(evt.stage))
                {
                    record.stage = evt.stage;
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
                }
                else if (becameReadable)
                {
                    _noreadCount = Math.Max(0, _noreadCount - 1);
                }

                Trim();
            }

            Publish(new { type = "parcel", data = record });
        }

        private void ApplyCameraStatus(SpoolEvent evt)
        {
            CameraRecord camera;
            lock (_sync)
            {
                string key = evt.deviceId ?? "unknown";
                if (!_cameras.TryGetValue(key, out camera))
                {
                    camera = new CameraRecord();
                    camera.deviceId = key;
                    _cameras[key] = camera;
                }
                camera.online = evt.online.GetValueOrDefault(true);
                camera.atMs = evt.atMs > 0 ? evt.atMs : evt.receivedAtMs;
                camera.lastChangeTime = FormatTime(camera.atMs);
                camera.statusChanges++;
            }

            Publish(new { type = "camera", data = camera });
        }

        internal void CountParseError()
        {
            lock (_sync)
            {
                _parseErrors++;
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
                return new List<CameraRecord>(_cameras.Values);
            }
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
