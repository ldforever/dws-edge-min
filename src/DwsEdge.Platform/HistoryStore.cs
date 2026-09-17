using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>
    /// 历史持久化（V1 用按天 JSONL 文件；以后换 SQLite 时只需替换本类，对外方法不变）：
    ///
    ///     data/parcels-yyyyMMdd.jsonl   包裹快照：每次事件更新写一行完整记录
    ///     data/applied-yyyyMMdd.jsonl   B1 去重指纹：平台重启后继续识别"重复上报"
    ///     data/offsets.json             spool 消费位点：重启后只读新增事件，不再全量重放
    ///
    /// 说明：当前开发环境无法离线获取 Microsoft.Data.Sqlite 包，因此先用文件实现；
    /// 对外只暴露 Append / LoadRecent / Query / LoadOffsets / SaveOffsets 这几个方法，
    /// 换成 SQLite 时只需替换本类内部实现。
    /// </summary>
    public sealed class HistoryStore : IDisposable
    {
        internal sealed class HistoryLine
        {
            public string type { get; set; }
            public string time { get; set; }
            public ParcelRecord data { get; set; }
        }

        private readonly object _sync = new object();
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions();
        private readonly ILogger<HistoryStore> _logger;
        private StreamWriter _writer;
        private string _writerDay;

        public HistoryStore(IConfiguration config, ILogger<HistoryStore> logger)
        {
            _logger = logger;
            string dir = config["History:Directory"] ?? "../data";
            DirectoryPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dir));
            System.IO.Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; private set; }

        public string OffsetsPath
        {
            get { return Path.Combine(DirectoryPath, "offsets.json"); }
        }

        #region 包裹快照写入 / 读取

        /// <summary>把当前包裹记录写一行快照（每次更新都写，读的时候按 traceId 取最后一条）。</summary>
        public void Append(ParcelRecord record)
        {
            if (record == null)
            {
                return;
            }

            string day = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            lock (_sync)
            {
                try
                {
                    if (_writer == null || _writerDay != day)
                    {
                        CloseWriter();
                        string path = Path.Combine(DirectoryPath, "parcels-" + day + ".jsonl");
                        // bufferSize=1：历史记录逐行落盘，进程崩溃也不会丢最近的包裹
                        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 1), new UTF8Encoding(false));
                        _writer.AutoFlush = true;
                        _writerDay = day;
                    }

                    HistoryLine line = new HistoryLine();
                    line.type = "parcel";
                    line.time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    line.data = record;
                    _writer.WriteLine(JsonSerializer.Serialize(line, _json));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "写历史失败");
                }
            }
        }

        /// <summary>加载最近 N 天的包裹快照，用于平台重启后立即恢复界面数据。</summary>
        public List<ParcelRecord> LoadRecent(int days, int maxRecords)
        {
            if (days < 1)
            {
                days = 1;
            }
            DateTime from = DateTime.Today.AddDays(-(days - 1));
            return ReadRange(from, DateTime.Today, null, maxRecords);
        }

        /// <summary>按时间范围/条件查询历史包裹（读取范围覆盖的日文件）。</summary>
        public List<ParcelRecord> Query(DateTime from, DateTime to, string code, string deviceId, bool? noread, int limit)
        {
            return ReadRange(from, to,
                delegate(ParcelRecord record)
                {
                    if (!string.IsNullOrEmpty(deviceId)
                        && (record.deviceId == null || record.deviceId.IndexOf(deviceId, StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        return false;
                    }
                    if (noread.HasValue)
                    {
                        bool isNoread = record.codeCount == 0;
                        if (isNoread != noread.Value)
                        {
                            return false;
                        }
                    }
                    if (!string.IsNullOrEmpty(code))
                    {
                        bool hit = false;
                        if (record.codes != null)
                        {
                            for (int i = 0; i < record.codes.Count; i++)
                            {
                                if (record.codes[i] != null && record.codes[i].IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    hit = true;
                                    break;
                                }
                            }
                        }
                        if (!hit)
                        {
                            return false;
                        }
                    }
                    return true;
                },
                limit);
        }

        private List<ParcelRecord> ReadRange(DateTime from, DateTime to, Func<ParcelRecord, bool> filter, int limit)
        {
            Dictionary<string, ParcelRecord> byTrace = new Dictionary<string, ParcelRecord>(StringComparer.Ordinal);

            for (DateTime day = from.Date; day <= to.Date; day = day.AddDays(1))
            {
                string path = Path.Combine(DirectoryPath, "parcels-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    foreach (string line in File.ReadLines(path, Encoding.UTF8))
                    {
                        ParcelRecord record = Parse(line);
                        if (record == null)
                        {
                            continue;
                        }
                        if (filter != null && !filter(record))
                        {
                            continue;
                        }
                        if (!string.IsNullOrEmpty(record.traceId))
                        {
                            byTrace[record.traceId] = record;   // 后者覆盖前者 = 取最新快照
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "读取历史失败：{0}", path);
                }
            }

            List<ParcelRecord> list = new List<ParcelRecord>(byTrace.Values);
            list.Sort(delegate(ParcelRecord a, ParcelRecord b) { return b.capturedAtMs.CompareTo(a.capturedAtMs); });

            int max = limit > 0 ? limit : list.Count;
            if (list.Count > max)
            {
                list.RemoveRange(max, list.Count - max);
            }
            return list;
        }

        private ParcelRecord Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            try
            {
                HistoryLine wrapper = JsonSerializer.Deserialize<HistoryLine>(line, _json);
                if (wrapper != null && wrapper.data != null)
                {
                    return wrapper.data;
                }
            }
            catch (JsonException)
            {
            }

            try
            {
                return JsonSerializer.Deserialize<ParcelRecord>(line, _json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        #endregion

        #region B1 去重指纹（重启后继续识别重复上报）

        /// <summary>
        /// 记录一条"已处理事件的内容指纹"。
        /// 只写 fp: 开头的内容指纹（阶段+条码+重量体积+图片），不写宿主的事件号 ——
        /// 事件号是每次运行自增的，跨重启没有意义。
        /// </summary>
        public void AppendApplied(string traceId, string fingerprint)
        {
            if (string.IsNullOrEmpty(traceId) || string.IsNullOrEmpty(fingerprint))
            {
                return;
            }

            string day = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string path = Path.Combine(DirectoryPath, "applied-" + day + ".jsonl");
            try
            {
                // 逐行落盘（bufferSize=1）：崩溃时最多丢最后一条指纹，且重复指纹被再次判重也不影响正确性
                using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 1))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.WriteLine(JsonSerializer.Serialize(new AppliedLine { t = traceId, k = fingerprint }, _json));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "写去重指纹失败");
            }
        }

        private sealed class AppliedLine
        {
            public string t { get; set; }
            public string k { get; set; }
        }

        /// <summary>加载最近 N 天的去重指纹（traceId → 指纹集合）。</summary>
        public Dictionary<string, HashSet<string>> LoadApplied(int days)
        {
            Dictionary<string, HashSet<string>> result =
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            if (days < 1)
            {
                days = 1;
            }

            DateTime from = DateTime.Today.AddDays(-(days - 1));
            for (DateTime day = from.Date; day <= DateTime.Today; day = day.AddDays(1))
            {
                string path = Path.Combine(DirectoryPath, "applied-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    foreach (string line in File.ReadLines(path, Encoding.UTF8))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        AppliedLine item;
                        try
                        {
                            item = JsonSerializer.Deserialize<AppliedLine>(line, _json);
                        }
                        catch (JsonException)
                        {
                            continue;
                        }

                        if (item == null || string.IsNullOrEmpty(item.t) || string.IsNullOrEmpty(item.k))
                        {
                            continue;
                        }

                        HashSet<string> keys;
                        if (!result.TryGetValue(item.t, out keys))
                        {
                            keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            result[item.t] = keys;
                        }
                        keys.Add(item.k);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "读取去重指纹失败：{0}", path);
                }
            }

            return result;
        }

        /// <summary>清理过期的去重指纹文件（和包裹历史一样按天滚动）。</summary>
        public void CleanupApplied(int keepDays)
        {
            try
            {
                if (keepDays < 1)
                {
                    keepDays = 1;
                }
                DateTime cutoff = DateTime.Today.AddDays(-keepDays);
                foreach (string file in Directory.GetFiles(DirectoryPath, "applied-*.jsonl"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    DateTime day;
                    if (DateTime.TryParseExact(name.Substring("applied-".Length), "yyyyMMdd",
                        CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out day)
                        && day < cutoff)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理去重指纹失败");
            }
        }

        #endregion

        #region spool 消费位点

        public Dictionary<string, long> LoadOffsets()
        {
            try
            {
                if (File.Exists(OffsetsPath))
                {
                    string text = File.ReadAllText(OffsetsPath, Encoding.UTF8);
                    Dictionary<string, long> offsets = JsonSerializer.Deserialize<Dictionary<string, long>>(text, _json);
                    if (offsets != null)
                    {
                        return new Dictionary<string, long>(offsets, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取消费位点失败，本次按从头读取处理");
            }
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        public void SaveOffsets(Dictionary<string, long> offsets)
        {
            if (offsets == null)
            {
                return;
            }
            try
            {
                string text = JsonSerializer.Serialize(offsets, _json);
                File.WriteAllText(OffsetsPath, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "保存消费位点失败");
            }
        }

        #endregion

        private void CloseWriter()
        {
            if (_writer != null)
            {
                try
                {
                    _writer.Flush();
                    _writer.Dispose();
                }
                catch (Exception)
                {
                }
                _writer = null;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                CloseWriter();
            }
        }
    }
}
