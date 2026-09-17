using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace DwsEdge.Host
{
    /// <summary>
    /// 存图保留策略：
    ///   1) 按保存天数清理：图片根目录下名为 yyyyMMdd 的日期目录，早于截止日期的整目录删除；
    ///   2) 磁盘水位保护：磁盘占用达到上限后，从最旧的日期目录开始删，直到降到阈值以下。
    ///
    /// 安全约束：只处理图片根目录下"名字正好是 8 位数字"的直接子目录，
    /// 删除前再次校验绝对路径仍在图片根目录内，绝不碰其他文件。
    /// </summary>
    internal sealed class ImageRetentionService : IDisposable
    {
        private sealed class DayFolder
        {
            public DateTime Date;
            public string Path;
            public bool Deleted;
        }

        private readonly string _imageRoot;
        private readonly int _retentionDays;
        private readonly int _maxDiskPercent;
        private readonly int _intervalMinutes;
        private readonly Action<string, bool> _log;

        private Thread _thread;
        private volatile bool _running;

        public ImageRetentionService(string imageRoot, int retentionDays, int maxDiskPercent, int intervalMinutes, Action<string, bool> log)
        {
            _imageRoot = Path.GetFullPath(imageRoot);
            _retentionDays = Math.Max(0, retentionDays);
            _maxDiskPercent = Math.Max(0, Math.Min(100, maxDiskPercent));
            _intervalMinutes = Math.Max(1, intervalMinutes);
            _log = log;
        }

        public string ImageRoot
        {
            get { return _imageRoot; }
        }

        public bool Enabled
        {
            get { return _retentionDays > 0 || _maxDiskPercent > 0; }
        }

        public void Start(bool cleanupOnStart)
        {
            if (!Enabled)
            {
                return;
            }

            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "image-retention";
            _thread.Start(cleanupOnStart);
        }

        private void Loop(object state)
        {
            bool first = state is bool && (bool)state;
            while (_running)
            {
                if (first)
                {
                    first = false;
                    TryCleanup();
                }

                // 分片 sleep，保证 Stop 能及时退出
                int slices = _intervalMinutes * 6;
                for (int i = 0; i < slices && _running; i++)
                {
                    Thread.Sleep(10000);
                }

                if (_running)
                {
                    TryCleanup();
                }
            }
        }

        private void TryCleanup()
        {
            try
            {
                RunOnce();
            }
            catch (Exception ex)
            {
                _log("存图清理异常：" + ex.Message, true);
            }
        }

        /// <summary>执行一次清理，返回删除的日期目录数。</summary>
        public int RunOnce()
        {
            if (!Directory.Exists(_imageRoot))
            {
                return 0;
            }

            List<DayFolder> folders = ListDayFolders();
            int deletedFolders = 0;
            int deletedFiles = 0;
            long freedBytes = 0;

            // 1) 按保存天数
            if (_retentionDays > 0)
            {
                DateTime cutoff = DateTime.Today.AddDays(-_retentionDays);
                for (int i = 0; i < folders.Count; i++)
                {
                    if (folders[i].Date >= cutoff)
                    {
                        continue;
                    }
                    if (DeleteFolder(folders[i], ref deletedFiles, ref freedBytes))
                    {
                        deletedFolders++;
                    }
                }
            }

            // 2) 磁盘水位
            if (_maxDiskPercent > 0)
            {
                int used = GetDiskUsedPercent();
                for (int i = 0; i < folders.Count && used >= _maxDiskPercent; i++)
                {
                    if (folders[i].Deleted)
                    {
                        continue;
                    }
                    if (DeleteFolder(folders[i], ref deletedFiles, ref freedBytes))
                    {
                        deletedFolders++;
                    }
                    used = GetDiskUsedPercent();
                }
            }

            int usedAfter = GetDiskUsedPercent();
            if (deletedFolders > 0)
            {
                string freedText = freedBytes >= 1024 * 1024
                    ? (freedBytes / 1024 / 1024) + " MB"
                    : (freedBytes / 1024) + " KB";
                _log(string.Format(CultureInfo.InvariantCulture,
                    "存图清理：删除 {0} 个日期目录、{1} 个文件，释放 {2}；当前磁盘占用 {3}%（保存天数：{4}，水位上限：{5}%）",
                    deletedFolders, deletedFiles, freedText,
                    usedAfter, _retentionDays == 0 ? "永久" : _retentionDays + " 天", _maxDiskPercent), false);
            }
            else if (usedAfter >= _maxDiskPercent && _maxDiskPercent > 0)
            {
                _log(string.Format(CultureInfo.InvariantCulture,
                    "存图清理：已无可删的日期目录，但磁盘占用仍为 {0}%（上限 {1}%），请人工处理",
                    usedAfter, _maxDiskPercent), true);
            }

            return deletedFolders;
        }

        private List<DayFolder> ListDayFolders()
        {
            List<DayFolder> list = new List<DayFolder>();

            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(_imageRoot);
            }
            catch (Exception)
            {
                return list;
            }

            for (int i = 0; i < dirs.Length; i++)
            {
                string name = Path.GetFileName(dirs[i]);
                if (name.Length != 8)
                {
                    continue;
                }

                DateTime date;
                if (!DateTime.TryParseExact(name, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                {
                    continue;
                }

                DayFolder folder = new DayFolder();
                folder.Date = date.Date;
                folder.Path = dirs[i];
                list.Add(folder);
            }

            list.Sort(delegate(DayFolder a, DayFolder b) { return a.Date.CompareTo(b.Date); });
            return list;
        }

        private bool DeleteFolder(DayFolder folder, ref int deletedFiles, ref long freedBytes)
        {
            string full = Path.GetFullPath(folder.Path);
            string root = _imageRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            // 安全检查：必须还在图片根目录内，且目录名是 8 位日期
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                _log("存图清理：跳过不在图片目录内的路径 " + full, true);
                return false;
            }
            if (Path.GetFileName(full).Length != 8)
            {
                return false;
            }

            long bytes = 0;
            int files = 0;
            try
            {
                foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
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
            catch (Exception)
            {
            }

            try
            {
                Directory.Delete(full, true);
            }
            catch (Exception ex)
            {
                _log("存图清理：删除目录失败 " + full + "：" + ex.Message, true);
                return false;
            }

            folder.Deleted = true;
            deletedFiles += files;
            freedBytes += bytes;
            return true;
        }

        private int GetDiskUsedPercent()
        {
            try
            {
                string root = Path.GetPathRoot(_imageRoot);
                DriveInfo drive = new DriveInfo(root);
                long total = drive.TotalSize;
                if (total <= 0)
                {
                    return 0;
                }
                long free = drive.TotalFreeSpace;
                return (int)Math.Round((total - free) * 100.0 / total);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public void Dispose()
        {
            _running = false;
            if (_thread != null && _thread.IsAlive)
            {
                _thread.Join(5000);
            }
        }
    }
}
