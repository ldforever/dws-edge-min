using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>
    /// 消费采集宿主写出的 JSONL 事件文件。
    ///
    /// V1 用文件 spool：简单、可离线、天然抗崩溃；
    /// V2 换成 gRPC 或命名管道时，这个类替换成客户端即可，SpoolStore 不用改。
    /// </summary>
    public sealed class SpoolTailer : BackgroundService
    {
        private readonly SpoolStore _store;
        private readonly HistoryStore _history;
        private readonly ILogger<SpoolTailer> _logger;
        private readonly string _spoolDirectory;
        private readonly int _intervalMs;
        private readonly Dictionary<string, long> _offsets =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly JsonSerializerOptions _json =
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        private DateTime _lastOffsetSave = DateTime.MinValue;
        private DateTime _lastMarkerWrite = DateTime.MinValue;
        private string _lastConsumedDay;

        public SpoolTailer(IConfiguration config, SpoolStore store, HistoryStore history, ILogger<SpoolTailer> logger)
        {
            _store = store;
            _history = history;
            _logger = logger;

            string directory = config["Spool:Directory"] ?? "../spool";
            _spoolDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, directory));

            int interval = 500;
            int.TryParse(config["Spool:PollIntervalMs"], out interval);
            _intervalMs = Math.Max(100, interval);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("采集事件目录：{0}（轮询 {1} ms）", _spoolDirectory, _intervalMs);

            // 恢复上次的消费位点：只读新增事件，不再全量重放
            Dictionary<string, long> saved = _history.LoadOffsets();
            foreach (KeyValuePair<string, long> pair in saved)
            {
                _offsets[pair.Key] = pair.Value;
            }
            if (saved.Count > 0)
            {
                _logger.LogInformation("已恢复 {0} 个文件的消费位点（继续读新增事件）", saved.Count);
            }

            // 相机状态是"当前值"语义：消费位点只保证包裹事件不重复处理，
            // 平台单独重启时相机清单必须重新建立（否则要等下一次状态变化才有数据）。
            _store.RebuildCameraState(_spoolDirectory);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    ProcessOnce();
                    PersistOffsets(false);
                    UpdateConsumedMarker();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "读取采集事件失败");
                }

                try
                {
                    await Task.Delay(_intervalMs, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void ProcessOnce()
        {
            if (!Directory.Exists(_spoolDirectory))
            {
                return;
            }

            string[] files = Directory.GetFiles(_spoolDirectory, "events-*.jsonl");
            Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length; i++)
            {
                ProcessFile(files[i]);
            }
        }

        /// <summary>从上次读到的位置继续读，只处理以换行结尾的完整行。</summary>
        private void ProcessFile(string path)
        {
            long offset;
            if (!_offsets.TryGetValue(path, out offset))
            {
                offset = 0;
            }

            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (offset > stream.Length)
                {
                    offset = 0;   // 文件被重建或截断
                }

                stream.Seek(offset, SeekOrigin.Begin);
                long remaining = stream.Length - offset;
                if (remaining <= 0)
                {
                    return;
                }

                byte[] buffer = new byte[remaining];
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return;
                }

                string text = Encoding.UTF8.GetString(buffer, 0, read);
                int lastNewline = text.LastIndexOf('\n');
                if (lastNewline < 0)
                {
                    return;   // 还没有完整的一行
                }

                string complete = text.Substring(0, lastNewline);
                string[] lines = complete.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }
                    HandleLine(line);
                }

                _offsets[path] = offset + Encoding.UTF8.GetByteCount(text.Substring(0, lastNewline + 1));
            }
        }

        private void HandleLine(string line)
        {
            try
            {
                SpoolEvent evt = JsonSerializer.Deserialize<SpoolEvent>(line, _json);
                _store.Apply(evt);
            }
            catch (JsonException)
            {
                _store.CountParseError();
            }
            catch (Exception ex)
            {
                _store.CountParseError();
                _logger.LogDebug(ex, "处理事件失败");
            }
        }

        /// <summary>把消费位点写盘（默认节流 5 秒；force=true 立即写）。</summary>
        private void PersistOffsets(bool force)
        {
            // 位点落盘间隔：1 秒。这个值越小，"平台异常退出后 spool 被重读"的窗口就越小
            // （重读本身是安全的 —— B1 的内容去重会把重复事件丢掉，这里是少做无用功）。
            if (!force && (DateTime.UtcNow - _lastOffsetSave).TotalSeconds < 1)
            {
                return;
            }
            _lastOffsetSave = DateTime.UtcNow;

            // 只保留仍然存在的文件，避免 offsets.json 无限增长
            Dictionary<string, long> snapshot = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, long> pair in _offsets)
            {
                if (File.Exists(pair.Key))
                {
                    snapshot[pair.Key] = pair.Value;
                }
            }
            _history.SaveOffsets(snapshot);
        }

        /// <summary>
        /// 写出"已完整消费到哪一天"的标记（spool\.consumed），供采集宿主的 spool 保留策略使用：
        /// 只有被标记覆盖的日期文件才允许删除。规则是从最旧文件开始、连续读完整的最大日期。
        /// </summary>
        private void UpdateConsumedMarker()
        {
            if (!Directory.Exists(_spoolDirectory))
            {
                return;
            }

            string[] files = Directory.GetFiles(_spoolDirectory, "events-*.jsonl");
            Array.Sort(files, StringComparer.Ordinal);

            string consumedDay = null;
            for (int i = 0; i < files.Length; i++)
            {
                long offset;
                if (!_offsets.TryGetValue(files[i], out offset))
                {
                    offset = 0;
                }

                long length;
                try
                {
                    length = new FileInfo(files[i]).Length;
                }
                catch (Exception)
                {
                    break;
                }

                if (offset < length)
                {
                    break;   // 这个文件还没读完，后面的更不能算已消费
                }

                string day = ExtractDay(files[i]);
                if (!string.IsNullOrEmpty(day))
                {
                    consumedDay = day;
                }
            }

            if (string.IsNullOrEmpty(consumedDay) || consumedDay == _lastConsumedDay)
            {
                return;
            }
            if ((DateTime.UtcNow - _lastMarkerWrite).TotalSeconds < 5)
            {
                return;
            }

            try
            {
                File.WriteAllText(Path.Combine(_spoolDirectory, ".consumed"), consumedDay, new UTF8Encoding(false));
                _lastConsumedDay = consumedDay;
                _lastMarkerWrite = DateTime.UtcNow;
                _logger.LogInformation("已更新 spool 消费标记：{0}（该日期之前的文件允许被采集宿主清理）", consumedDay);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "写 spool 消费标记失败");
            }
        }

        private static string ExtractDay(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int dash = name.LastIndexOf('-');
            if (dash < 0 || dash >= name.Length - 1)
            {
                return null;
            }
            string day = name.Substring(dash + 1);
            return day.Length == 8 ? day : null;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            PersistOffsets(true);
            await base.StopAsync(cancellationToken);
        }
    }
}
