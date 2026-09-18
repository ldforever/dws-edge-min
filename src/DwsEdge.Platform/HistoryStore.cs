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
    /// <summary>B3 历史查询条件。</summary>
    public sealed class HistoryQuery
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        /// <summary>条码包含匹配（忽略大小写）。</summary>
        public string Code { get; set; }

        /// <summary>相机标识包含匹配。</summary>
        public string DeviceId { get; set; }

        /// <summary>true=只看无码，false=只看有码，null=全部。</summary>
        public bool? NoRead { get; set; }

        /// <summary>下发状态：pending / sent / failed；空=全部。没写过状态的记录按 pending 处理。</summary>
        public string DispatchState { get; set; }

        /// <summary>true=只看有图的，false=只看没图的，null=全部。</summary>
        public bool? HasImage { get; set; }

        public int Limit { get; set; }
        public int Offset { get; set; }

        public HistoryQuery()
        {
            From = DateTime.Today.AddDays(-1);
            To = DateTime.Today;
            Limit = 200;
        }
    }

    /// <summary>B3 查询结果（带总数与耗时，好用来自证"10 万条 2 秒内"）。</summary>
    public sealed class HistoryQueryResult
    {
        /// <summary>命中总数（过滤后、分页前）。</summary>
        public int total { get; set; }

        public int returned { get; set; }
        public long elapsedMs { get; set; }

        /// <summary>true=读了"按 traceId 收敛后的索引文件"，false=读了逐条快照。</summary>
        public bool fromIndex { get; set; }

        public List<ParcelRecord> items { get; set; } = new List<ParcelRecord>();
    }

    /// <summary>
    /// 历史持久化（V1 用按天 JSONL 文件；以后换 SQLite 只需替换本类，对外方法不变）：
    ///
    ///     data/parcels-yyyyMMdd.jsonl        包裹快照：每次事件更新追加一行完整记录（崩溃安全）
    ///     data/parcels-yyyyMMdd.index.jsonl  索引：一个 traceId 一行 = 该包裹的最终状态
    ///     data/offsets.json                  spool 消费位点
    ///
    /// 为什么要索引：一个包裹会写 2 行快照（先条码后重量体积），一天 5 万件就是 10 万行。
    /// 查询时逐行解析全部快照既慢又占内存（10 万条要好几秒）。索引文件把它收敛成"一个包裹一行"，
    /// 并且**可以在快照增长后重建**（索引坏了/删了都不影响数据，只是慢一点）。
    /// 今天正在写的那个文件直接读快照（还在追加，索引会一直过期）；过去的日期读索引。
    ///
    /// B1 的去重指纹不在这里，见 DedupStore（data\dedup）。
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

        /// <summary>每个索引文件最近一次尝试重建的时间，避免每次查询都对同一个大文件重复收敛。</summary>
        private readonly Dictionary<string, DateTime> _lastIndexBuildUtc =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public HistoryStore(IConfiguration config, ILogger<HistoryStore> logger)
        {
            _logger = logger;
            string dir = config["History:Directory"] ?? "../data";
            DirectoryPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dir));
            System.IO.Directory.CreateDirectory(DirectoryPath);

            int limit = 5000000;
            int.TryParse(config["History:ExportMaxRows"], out limit);
            ExportMaxRows = limit > 0 ? limit : 5000000;
        }

        public string DirectoryPath { get; private set; }

        /// <summary>单次导出最多写多少行（防止误操作把内存/磁盘打满）。</summary>
        public int ExportMaxRows { get; private set; }

        public string OffsetsPath
        {
            get { return Path.Combine(DirectoryPath, "offsets.json"); }
        }

        #region 写入

        /// <summary>把当前包裹记录写一行快照（每次更新都写，读的时候按 traceId 取最后一条）。</summary>
        public void Append(ParcelRecord record)
        {
            if (record == null)
            {
                return;
            }

            // 按"事件时间"分文件，而不是按下笔的时刻：跨零点、平台停机后补投历史数据时，
            // 用写入时刻会把昨天的事件写进今天的文件，按日期查询/统计（C4 看板）就全错了。
            DateTime eventDay = DayOf(record);
            string day = eventDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            lock (_sync)
            {
                try
                {
                    if (_writer == null || _writerDay != day)
                    {
                        CloseWriter();
                        string path = SnapshotPath(eventDay);
                        // bufferSize=1 + AutoFlush：逐行落盘，突然断电也不会丢最近的包裹
                        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 1), new UTF8Encoding(false));
                        _writer.AutoFlush = true;
                        _writerDay = day;
                    }

                    HistoryLine line = new HistoryLine();
                    line.type = "parcel";
                    // 快照行的时间也记事件时间：排查时"这行是什么时候的包裹"才是关键
                    line.time = string.IsNullOrEmpty(record.time)
                        ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        : record.time;
                    line.data = record;
                    _writer.WriteLine(JsonSerializer.Serialize(line, _json));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "写历史失败");
                }
            }
        }

        /// <summary>
        /// 这条记录属于哪一天：优先看事件时间戳（capturedAtMs），退回 record.time，都没有才用今天。
        /// 分文件与索引都按它来，保证"按日期查"和"写进去的日期"一致。
        /// </summary>
        private static DateTime DayOf(ParcelRecord record)
        {
            if (record.capturedAtMs > 0)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(record.capturedAtMs).ToLocalTime().Date;
            }
            DateTime parsed;
            if (!string.IsNullOrEmpty(record.time)
                && DateTime.TryParse(record.time, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                return parsed.Date;
            }
            return DateTime.Today;
        }

        /// <summary>
        /// 把某一天的快照收敛成索引（一个 traceId 一行，取最后一条）。
        /// 先写 .tmp 再原子替换，中途失败不会破坏已有索引。
        /// </summary>
        public void CompactDay(DateTime day)
        {
            string snapshot = SnapshotPath(day);
            if (!File.Exists(snapshot))
            {
                return;
            }

            string index = IndexPath(snapshot);
            try
            {
                Dictionary<string, ParcelRecord> byTrace = new Dictionary<string, ParcelRecord>(StringComparer.Ordinal);
                int lines = 0;

                foreach (string line in ReadLinesShared(snapshot))
                {
                    lines++;
                    ParcelRecord record = Parse(line);
                    if (record == null || string.IsNullOrEmpty(record.traceId))
                    {
                        continue;
                    }
                    byTrace[record.traceId] = record;   // 后者覆盖前者 = 最新状态
                }

                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, ParcelRecord> pair in byTrace)
                {
                    sb.AppendLine(JsonSerializer.Serialize(pair.Value, _json));
                }

                string tmp = index + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(index))
                {
                    File.Replace(tmp, index, null);
                }
                else
                {
                    File.Move(tmp, index);
                }

                lock (_sync)
                {
                    _lastIndexBuildUtc[index] = DateTime.UtcNow;
                }
                _logger.LogInformation("历史索引已重建：{0} 行快照 → {1} 个包裹（{2}）",
                    lines, byTrace.Count, Path.GetFileName(index));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("重建历史索引失败（查询会自动回退读快照）：{0}", ex.Message);
            }
        }

        /// <summary>平台启动时把"已经封盘的过去几天"整理一遍，之后查询就走索引了。</summary>
        public void CompactClosedDays(int days)
        {
            if (days < 1)
            {
                days = 1;
            }

            for (int i = 1; i <= days; i++)
            {
                DateTime day = DateTime.Today.AddDays(-i);
                string snapshot = SnapshotPath(day);
                string index = IndexPath(snapshot);
                if (File.Exists(snapshot) && !IndexIsFresh(snapshot, index))
                {
                    CompactDay(day);
                }
            }
        }

        #endregion

        #region 查询

        /// <summary>加载最近 N 天的包裹（平台重启后恢复界面用）。</summary>
        public List<ParcelRecord> LoadRecent(int days, int maxRecords)
        {
            if (days < 1)
            {
                days = 1;
            }

            HistoryQuery query = new HistoryQuery();
            query.From = DateTime.Today.AddDays(-(days - 1));
            query.To = DateTime.Today;
            query.Limit = maxRecords;
            query.Offset = 0;

            HistoryQueryResult result = Query(query);
            return result.items;
        }

        /// <summary>按条件查询（走索引，10 万条级别也能秒回）。</summary>
        public HistoryQueryResult Query(HistoryQuery query)
        {
            DateTime start = DateTime.UtcNow;
            HistoryQueryResult result = new HistoryQueryResult();
            if (query == null)
            {
                return result;
            }

            List<ParcelRecord> hits = new List<ParcelRecord>();
            bool usedIndex = true;

            for (DateTime day = query.From.Date; day <= query.To.Date; day = day.AddDays(1))
            {
                bool dayUsedIndex;
                hits.AddRange(LoadDay(day, query, out dayUsedIndex));
                if (!dayUsedIndex)
                {
                    usedIndex = false;
                }
            }

            // 最近的在最前面
            hits.Sort(delegate(ParcelRecord a, ParcelRecord b)
            {
                int byTime = b.capturedAtMs.CompareTo(a.capturedAtMs);
                if (byTime != 0)
                {
                    return byTime;
                }
                return string.CompareOrdinal(b.traceId, a.traceId);
            });

            result.total = hits.Count;
            result.fromIndex = usedIndex;

            int offset = Math.Max(0, query.Offset);
            int limit = query.Limit > 0 ? query.Limit : 200;
            for (int i = offset; i < hits.Count && result.items.Count < limit; i++)
            {
                result.items.Add(hits[i]);
            }
            result.returned = result.items.Count;
            result.elapsedMs = (long)(DateTime.UtcNow - start).TotalMilliseconds;
            return result;
        }

        // ================================================================
        // C4 统计看板（简版）：总包数 / 读码率 / 无码率，按相机与班次维度查看
        // ================================================================

        /// <summary>一个统计分组（相机/班次/小时/日期）。</summary>
        private sealed class StatsBucket
        {
            public string key;
            public string label;
            public long total;
            public long read;
            public long noread;
            public long images;
            public long dispatchSent;
            public long dispatchFailed;
            public long weightMissing;
            public long firstMs;
            public long lastMs;

            public StatsBucket(string key, string label)
            {
                this.key = key;
                this.label = label;
            }

            public void Add(ParcelRecord record)
            {
                total++;
                if (record.codeCount > 0)
                {
                    read++;
                }
                else
                {
                    noread++;
                }
                if (record.imageCount > 0)
                {
                    images++;
                }
                if (record.weightGrams <= 0)
                {
                    weightMissing++;
                }
                string state = record.dispatchState ?? "pending";
                if (string.Equals(state, "sent", StringComparison.OrdinalIgnoreCase))
                {
                    dispatchSent++;
                }
                else if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    dispatchFailed++;
                }
                long at = AtMsOf(record);
                if (at > 0)
                {
                    if (firstMs == 0 || at < firstMs) { firstMs = at; }
                    if (at > lastMs) { lastMs = at; }
                }
            }

            public object ToResult()
            {
                return new
                {
                    key,
                    label,
                    total,
                    read,
                    noread,
                    readRatePercent = Rate(read, total),
                    noreadRatePercent = Rate(noread, total),
                    images,
                    weightMissing,
                    dispatchSent,
                    dispatchFailed,
                    firstTime = FormatMs(firstMs),
                    lastTime = FormatMs(lastMs)
                };
            }
        }

        private static double Rate(long part, long all)
        {
            if (all <= 0)
            {
                return 0;
            }
            return Math.Round(100.0 * part / all, 1);
        }

        /// <summary>时间戳（毫秒）→ 显示用文本；0 表示没数据。</summary>
        private static string FormatMs(long ms)
        {
            if (ms <= 0)
            {
                return null;
            }
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        private static long AtMsOf(ParcelRecord record)
        {
            if (record.capturedAtMs > 0)
            {
                return record.capturedAtMs;
            }
            DateTime parsed;
            if (!string.IsNullOrEmpty(record.time)
                && DateTime.TryParse(record.time, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out parsed))
            {
                return new DateTimeOffset(parsed).ToUnixTimeMilliseconds();
            }
            return 0;
        }

        /// <summary>
        /// 统计看板：把查询范围内的包裹按维度分组统计。
        /// dimension = camera（按相机）/ shift（按班次）/ hour（按小时）/ day（按日期）。
        /// 统计口径与历史查询完全一致（同一个 LoadDay + 同一套过滤），所以"看板的数字"就是"库里查出来的数字"。
        /// </summary>
        public object BuildBoard(HistoryQuery query, string dimension, ShiftPlan shifts)
        {
            DateTime start = DateTime.UtcNow;
            string dim = string.IsNullOrEmpty(dimension) ? "camera" : dimension.Trim().ToLowerInvariant();
            Dictionary<string, StatsBucket> groups = new Dictionary<string, StatsBucket>(StringComparer.Ordinal);
            StatsBucket all = new StatsBucket("all", "合计");
            long unmatched = 0;

            for (DateTime day = query.From.Date; day <= query.To.Date; day = day.AddDays(1))
            {
                bool dayUsedIndex;
                List<ParcelRecord> records = LoadDay(day, query, out dayUsedIndex);
                for (int i = 0; i < records.Count; i++)
                {
                    ParcelRecord record = records[i];
                    all.Add(record);

                    string key;
                    string label;
                    if (!ResolveKey(dim, record, shifts, out key, out label))
                    {
                        unmatched++;
                        continue;
                    }

                    StatsBucket bucket;
                    if (!groups.TryGetValue(key, out bucket))
                    {
                        bucket = new StatsBucket(key, label);
                        groups.Add(key, bucket);
                    }
                    bucket.Add(record);
                }
            }

            List<StatsBucket> list = new List<StatsBucket>(groups.Values);
            if (dim == "camera")
            {
                // 相机维度按包数从多到少（现场先看谁在干活、谁没出码）
                list.Sort(delegate(StatsBucket a, StatsBucket b)
                {
                    int byTotal = b.total.CompareTo(a.total);
                    return byTotal != 0 ? byTotal : string.Compare(a.key, b.key, StringComparison.OrdinalIgnoreCase);
                });
            }
            else
            {
                list.Sort(delegate(StatsBucket a, StatsBucket b)
                {
                    return string.Compare(a.key, b.key, StringComparison.Ordinal);
                });
            }

            List<object> rows = new List<object>();
            for (int i = 0; i < list.Count; i++)
            {
                rows.Add(list[i].ToResult());
            }

            return new
            {
                from = query.From.ToString("yyyy-MM-dd"),
                to = query.To.ToString("yyyy-MM-dd"),
                dimension = dim,
                dimensions = new[] { "camera", "shift", "hour", "day" },
                totals = all.ToResult(),
                groups = rows,
                groupCount = rows.Count,
                unmatchedShifts = unmatched,
                elapsedMs = (long)(DateTime.UtcNow - start).TotalMilliseconds,
                note = dim == "shift"
                    ? "班次按 config\\shifts.json 划分；跨天班次的凌晨时段算前一天"
                    : "统计口径与历史查询一致（同一个索引、同一套过滤）"
            };
        }

        private static bool ResolveKey(string dimension, ParcelRecord record, ShiftPlan shifts, out string key, out string label)
        {
            key = null;
            label = null;
            long at = AtMsOf(record);

            if (dimension == "camera")
            {
                string camera = string.IsNullOrEmpty(record.deviceId) ? "未知相机" : record.deviceId;
                key = camera;
                label = camera;
                return true;
            }

            if (at <= 0)
            {
                return false;
            }
            DateTime local = DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().DateTime;

            if (dimension == "hour")
            {
                key = local.ToString("yyyy-MM-dd HH");
                label = local.ToString("MM-dd HH:00");
                return true;
            }

            if (dimension == "day")
            {
                key = local.ToString("yyyy-MM-dd");
                label = key;
                return true;
            }

            if (dimension == "shift")
            {
                string shiftKey;
                string shiftLabel;
                if (shifts != null && shifts.TryClassify(local, out shiftKey, out shiftLabel))
                {
                    key = shiftKey;
                    label = shiftLabel;
                    return true;
                }
                key = "未匹配班次";
                label = "未匹配班次（检查 shifts.json 的覆盖时段）";
                return true;
            }

            key = "未知维度";
            label = dimension;
            return true;
        }

        /// <summary>
        /// 导出 CSV（流式写出，不把结果全放进内存）。
        /// 字段覆盖 B3 要求：条码、时间、相机、图片路径、无码标记、下发状态。
        ///
        /// 注意这里特意做成异步写：Kestrel 默认禁止同步写响应体
        /// （AllowSynchronousIO=false，同步写会直接 500），所以用 WriteLineAsync。
        /// </summary>
        public async System.Threading.Tasks.Task<int> ExportCsvAsync(HistoryQuery query, TextWriter writer)
        {
            if (query == null || writer == null)
            {
                return 0;
            }

            await writer.WriteLineAsync(string.Join(",",
                "时间", "追踪号", "条码", "条码数", "无码", "相机", "重量(g)",
                "长(mm)", "宽(mm)", "高(mm)", "体积(mm3)", "图片数", "图片路径",
                "下发状态", "下发时间", "下发尝试", "下发错误", "采集时间戳"));

            int written = 0;
            for (DateTime day = query.From.Date; day <= query.To.Date; day = day.AddDays(1))
            {
                bool usedIndex;
                List<ParcelRecord> records = LoadDay(day, query, out usedIndex);
                for (int i = 0; i < records.Count; i++)
                {
                    await writer.WriteLineAsync(ToCsvRow(records[i]));
                    written++;
                    if (written >= ExportMaxRows)
                    {
                        return written;
                    }
                }
            }
            return written;
        }

        private static string ToCsvRow(ParcelRecord r)
        {
            string codes = r.codes == null ? string.Empty : string.Join(" ", r.codes.ToArray());
            return string.Join(",",
                Csv(r.time),
                Csv(r.traceId),
                Csv(codes),
                r.codeCount.ToString(CultureInfo.InvariantCulture),
                Csv(r.codeCount == 0 ? "是" : "否"),
                Csv(r.deviceId),
                r.weightGrams.ToString(CultureInfo.InvariantCulture),
                Num(r.lengthMm),
                Num(r.widthMm),
                Num(r.heightMm),
                Num(r.volumeMm3),
                r.imageCount.ToString(CultureInfo.InvariantCulture),
                Csv(r.firstImagePath),
                Csv(DispatchStateOf(r)),
                Csv(r.dispatchedAt),
                r.dispatchAttempts.ToString(CultureInfo.InvariantCulture),
                Csv(r.dispatchError),
                r.capturedAtMs.ToString(CultureInfo.InvariantCulture));
        }

        private static string Num(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>CSV 转义：含逗号/引号/换行的字段加引号，内部引号翻倍。</summary>
        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            bool needQuote = value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0
                || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0;
            if (!needQuote)
            {
                return value;
            }
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// 读某一天里满足条件的包裹（同一天内按 traceId 取最后一条）。
        /// usedIndex 表示这一天是用"收敛后的索引"还是"逐条快照"读的。
        /// </summary>
        private List<ParcelRecord> LoadDay(DateTime day, HistoryQuery query, out bool usedIndex)
        {
            List<ParcelRecord> result = new List<ParcelRecord>();
            string snapshot = SnapshotPath(day);
            string index = IndexPath(snapshot);
            bool isToday = day.Date == DateTime.Today;

            // 今天还在追加，索引会一直过期 → 直接读快照；过去的日期读索引（必要时先重建）
            bool useIndex = !isToday && File.Exists(index) && IndexIsFresh(snapshot, index);
            if (!isToday && File.Exists(snapshot) && !useIndex)
            {
                // 索引缺失或过期：重建一次。但如果刚重建过（10 秒内）就别反复折腾，直接读快照。
                bool recentlyBuilt;
                lock (_sync)
                {
                    DateTime last;
                    recentlyBuilt = _lastIndexBuildUtc.TryGetValue(index, out last)
                        && (DateTime.UtcNow - last).TotalSeconds < 10;
                }
                if (!recentlyBuilt)
                {
                    CompactDay(day);
                    useIndex = File.Exists(index) && IndexIsFresh(snapshot, index);
                }
            }

            usedIndex = useIndex;
            string path = useIndex ? index : snapshot;
            if (!File.Exists(path))
            {
                return result;
            }

            if (useIndex)
            {
                foreach (string line in ReadLinesShared(path))
                {
                    ParcelRecord record = Parse(line);
                    if (record != null && Matches(record, query))
                    {
                        result.Add(record);
                    }
                }
            }
            else
            {
                // 快照文件里一个包裹可能有多行 → 取最后一条，再过滤
                Dictionary<string, ParcelRecord> byTrace = new Dictionary<string, ParcelRecord>(StringComparer.Ordinal);
                foreach (string line in ReadLinesShared(path))
                {
                    ParcelRecord record = Parse(line);
                    if (record == null || string.IsNullOrEmpty(record.traceId))
                    {
                        continue;
                    }
                    byTrace[record.traceId] = record;
                }

                foreach (KeyValuePair<string, ParcelRecord> pair in byTrace)
                {
                    if (Matches(pair.Value, query))
                    {
                        result.Add(pair.Value);
                    }
                }
            }

            return result;
        }

        /// <summary>条件过滤（B3：条码、相机、无码、下发状态、图片）。</summary>
        private static bool Matches(ParcelRecord record, HistoryQuery query)
        {
            if (record == null)
            {
                return false;
            }

            if (query.NoRead.HasValue && (record.codeCount == 0) != query.NoRead.Value)
            {
                return false;
            }

            if (query.HasImage.HasValue)
            {
                bool hasImage = record.imageCount > 0 || !string.IsNullOrEmpty(record.firstImagePath);
                if (hasImage != query.HasImage.Value)
                {
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(query.DeviceId)
                && (record.deviceId == null
                    || record.deviceId.IndexOf(query.DeviceId, StringComparison.OrdinalIgnoreCase) < 0))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(query.DispatchState))
            {
                string want = NormalizeDispatch(query.DispatchState);
                if (!string.Equals(DispatchStateOf(record), want, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(query.Code))
            {
                bool hit = false;
                if (record.codes != null)
                {
                    for (int i = 0; i < record.codes.Count; i++)
                    {
                        if (record.codes[i] != null
                            && record.codes[i].IndexOf(query.Code, StringComparison.OrdinalIgnoreCase) >= 0)
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
        }

        private static string NormalizeDispatch(string state)
        {
            return string.IsNullOrEmpty(state) ? "pending" : state.Trim().ToLowerInvariant();
        }

        /// <summary>没写过下发状态的记录按 pending 处理（老数据也能被筛出来）。</summary>
        private static string DispatchStateOf(ParcelRecord record)
        {
            return NormalizeDispatch(record == null ? null : record.dispatchState);
        }

        private static bool IndexIsFresh(string snapshot, string index)
        {
            try
            {
                if (!File.Exists(index))
                {
                    return false;
                }
                if (!File.Exists(snapshot))
                {
                    return true;
                }
                return File.GetLastWriteTimeUtc(index) >= File.GetLastWriteTimeUtc(snapshot);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private string SnapshotPath(DateTime day)
        {
            return Path.Combine(DirectoryPath, "parcels-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
        }

        private static string IndexPath(string snapshotPath)
        {
            return snapshotPath.Substring(0, snapshotPath.Length - ".jsonl".Length) + ".index.jsonl";
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

        /// <summary>按行读一个可能正被追加写的文件（共享读）。</summary>
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
