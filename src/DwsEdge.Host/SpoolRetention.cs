using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace DwsEdge.Host
{
    /// <summary>
    /// spool 事件文件保留策略：删除 events-yyyyMMdd.jsonl 里超过保存天数的文件。
    ///
    /// 安全约束（重要）：只有当业务平台已经把某个日期之前的事件全部消费掉（写下了
    /// spool\.consumed 标记）之后，才允许删除那一天的文件；没有标记就一律不删，
    /// 避免"平台停着、事件先被清掉"导致丢数据。
    /// </summary>
    internal sealed class SpoolRetentionService : IDisposable
    {
        private const string ConsumedMarkerName = ".consumed";

        private readonly string _spoolDir;
        private readonly int _retentionDays;
        private readonly int _intervalMinutes;
        private readonly Action<string, bool> _log;

        private Thread _thread;
        private volatile bool _running;

        public SpoolRetentionService(string spoolDir, int retentionDays, int intervalMinutes, Action<string, bool> log)
        {
            _spoolDir = Path.GetFullPath(spoolDir);
            _retentionDays = Math.Max(0, retentionDays);
            _intervalMinutes = Math.Max(1, intervalMinutes);
            _log = log;
        }

        public string SpoolDirectory
        {
            get { return _spoolDir; }
        }

        public bool Enabled
        {
            get { return _retentionDays > 0; }
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
            _thread.Name = "spool-retention";
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
                _log("spool 清理异常：" + ex.Message, true);
            }
        }

        /// <summary>执行一次清理，返回删除的文件数。</summary>
        public int RunOnce()
        {
            if (!Directory.Exists(_spoolDir))
            {
                return 0;
            }

            DateTime consumedThrough;
            bool hasConsumedMarker = TryReadConsumedMarker(out consumedThrough);

            if (!hasConsumedMarker)
            {
                _log("spool 清理：业务平台还没有写过消费标记（" + Path.Combine(_spoolDir, ConsumedMarkerName)
                    + "），本次不删除任何事件文件，避免丢数据", false);
                return 0;
            }

            DateTime cutoff = DateTime.Today.AddDays(-_retentionDays);
            int deleted = 0;
            long freedBytes = 0;

            foreach (string file in Directory.GetFiles(_spoolDir, "events-*.jsonl"))
            {
                DateTime day;
                if (!TryParseSpoolFileDate(file, out day))
                {
                    continue;
                }

                // 两个条件都要满足：超过保存天数，且平台确认已经消费过这一天
                if (day >= cutoff || day > consumedThrough)
                {
                    continue;
                }

                try
                {
                    long size = new FileInfo(file).Length;
                    File.Delete(file);
                    deleted++;
                    freedBytes += size;
                }
                catch (Exception ex)
                {
                    _log("spool 清理：删除失败 " + file + "：" + ex.Message, true);
                }
            }

            if (deleted > 0)
            {
                string freed = freedBytes >= 1024 * 1024
                    ? (freedBytes / 1024 / 1024) + " MB"
                    : (freedBytes / 1024) + " KB";
                _log(string.Format(CultureInfo.InvariantCulture,
                    "spool 清理：删除 {0} 个事件文件，释放 {1}（保存天数：{2}，已消费到：{3}）",
                    deleted, freed, _retentionDays + " 天", consumedThrough.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), false);
            }

            return deleted;
        }

        private bool TryReadConsumedMarker(out DateTime consumedThrough)
        {
            consumedThrough = DateTime.MinValue;
            string path = Path.Combine(_spoolDir, ConsumedMarkerName);
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                string text = File.ReadAllText(path).Trim();
                return DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out consumedThrough);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryParseSpoolFileDate(string file, out DateTime day)
        {
            day = DateTime.MinValue;
            string name = Path.GetFileNameWithoutExtension(file);   // events-20260917
            int dash = name.LastIndexOf('-');
            if (dash < 0 || dash >= name.Length - 1)
            {
                return false;
            }

            string datePart = name.Substring(dash + 1);
            if (datePart.Length != 8)
            {
                return false;
            }

            return DateTime.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
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
