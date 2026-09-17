using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>
    /// 定期统计图片目录（文件数 / 总大小）与所在磁盘的占用，结果缓存到 SpoolStore，
    /// 供 /api/stats 使用。避免每次请求都去扫描目录。
    /// </summary>
    public sealed class StorageProbe : BackgroundService
    {
        private readonly SpoolStore _store;
        private readonly ILogger<StorageProbe> _logger;
        private readonly string _root;
        private readonly int _intervalSeconds;

        public StorageProbe(IConfiguration config, SpoolStore store, ILogger<StorageProbe> logger)
        {
            _store = store;
            _logger = logger;
            _root = store.ImagesRoot;

            int interval = 300;
            int.TryParse(config["Storage:ProbeIntervalSeconds"], out interval);
            _intervalSeconds = Math.Max(30, interval);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("图片目录探测：{0}（每 {1} 秒）", _root, _intervalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Probe();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "图片目录探测失败");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void Probe()
        {
            long files = 0;
            long bytes = 0;

            if (Directory.Exists(_root))
            {
                foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        bytes += new FileInfo(file).Length;
                        files++;
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            long total = 0;
            long free = 0;
            int usedPercent = 0;
            try
            {
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(_root));
                total = drive.TotalSize;
                free = drive.TotalFreeSpace;
                if (total > 0)
                {
                    usedPercent = (int)Math.Round((total - free) * 100.0 / total);
                }
            }
            catch (Exception)
            {
            }

            _store.UpdateStorage(files, bytes, total, free, usedPercent);
        }
    }
}
