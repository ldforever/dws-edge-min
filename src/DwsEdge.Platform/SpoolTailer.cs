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
        private readonly ILogger<SpoolTailer> _logger;
        private readonly string _spoolDirectory;
        private readonly int _intervalMs;
        private readonly Dictionary<string, long> _offsets =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly JsonSerializerOptions _json =
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public SpoolTailer(IConfiguration config, SpoolStore store, ILogger<SpoolTailer> logger)
        {
            _store = store;
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

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    ProcessOnce();
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
    }
}
