using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>去重指纹归档的对外状态（给 /api/dedup 和界面用）。</summary>
    public sealed class DedupStats
    {
        public string directory { get; set; }

        /// <summary>索引里的 traceId 条数（= 重复上报会被丢弃的包裹数）。</summary>
        public int indexEntries { get; set; }

        /// <summary>索引里的指纹总数。</summary>
        public int indexKeys { get; set; }

        /// <summary>还没合并进索引的 WAL 行数。</summary>
        public int walLines { get; set; }

        public long walBytes { get; set; }
        public long indexBytes { get; set; }
        public int retentionDays { get; set; }
        public int compactWhenWalLines { get; set; }
        public int compactEveryMinutes { get; set; }
        public string lastCompactAt { get; set; }
        public long compactions { get; set; }

        /// <summary>累计因超过保留期被清掉的 traceId 数。</summary>
        public long droppedExpired { get; set; }

        /// <summary>从旧格式（data\applied-*.jsonl）导入的指纹条数。</summary>
        public long importedLegacy { get; set; }
    }

    /// <summary>
    /// B1 去重指纹的归档存储。
    ///
    /// 为什么单独做这个：最早是一天一个 data\applied-yyyyMMdd.jsonl、每处理一条事件追加一行 ——
    /// 跑上一个月文件一直在涨，而且同一个 traceId 会在多个文件里重复出现，启动要全量扫一遍。
    ///
    /// 现在的结构（runtime\data\dedup\）：
    ///     applied-index.jsonl  一个 traceId 一行：{"t":"P1","k":["fp:..."],"s":最后出现时间}
    ///     wal-时间戳.jsonl      追加写：一条 = 一次已处理事件（顺序写、便宜、崩溃安全）
    ///     applied-*.jsonl.imported  旧格式（自动导入后改名保留，不删原件）
    ///
    /// 行为：
    ///   * 运行期只追加 WAL；平台启动时和定时（默认 10 分钟，或 WAL 超过 5000 行）整理一次；
    ///   * 整理 = 把内存索引写成新的 applied-index.jsonl（先写 .tmp 再原子替换）→ 删掉已合并的 WAL
    ///     → 按保留期（默认 30 天）丢掉太久没出现过的 traceId；
    ///   * 所以磁盘占用只跟"最近 30 天处理过多少包裹"有关，不随运行时长无限增长；
    ///   * 以后换 SQLite：只要重写这个类的 Load/Append/Compact/Stats，调用方一行都不用改。
    /// </summary>
    public sealed class DedupStore : IDisposable
    {
        private sealed class IndexLine
        {
            public string t { get; set; }
            public List<string> k { get; set; }

            /// <summary>最后出现时间（Unix 毫秒），整理时用它判断是否过期。</summary>
            public long s { get; set; }
        }

        private sealed class WalLine
        {
            public string t { get; set; }
            public string k { get; set; }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ILogger<DedupStore> _logger;
        private readonly object _sync = new object();
        private readonly Dictionary<string, HashSet<string>> _index =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _lastSeen =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private StreamWriter _walWriter;
        private int _walLines;
        private Timer _timer;
        private bool _disposed;
        private long _compactions;
        private long _droppedExpired;
        private long _importedLegacy;

        public DedupStore(IConfiguration config, ILogger<DedupStore> logger)
        {
            _logger = logger;

            string root = config["Runtime:Root"] ?? "..";
            string runtimeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, root));
            string dir = config["Dedup:Directory"] ?? @"data\dedup";
            // 约定：Dedup:Directory 相对 runtime 根目录（例如 data\dedup）。
            // 但如果有人按 History/Spool 的写法填了 "../data/dedup"（相对 exe 目录），也照样能work ——
            // 否则归档会被悄悄写到 runtime 的上一层去，排查起来很费劲。
            if (Path.IsPathRooted(dir))
            {
                DirectoryPath = dir;
            }
            else if (dir.StartsWith("..", StringComparison.Ordinal))
            {
                DirectoryPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dir));
            }
            else
            {
                DirectoryPath = Path.GetFullPath(Path.Combine(runtimeRoot, dir));
            }
            LegacyDirectory = Path.Combine(runtimeRoot, "data");

            RetentionDays = ReadInt(config, "Dedup:RetentionDays", 30);
            CompactWhenWalLines = ReadInt(config, "Dedup:CompactWhenWalLines", 5000);
            CompactEveryMinutes = ReadInt(config, "Dedup:CompactEveryMinutes", 10);

            System.IO.Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; private set; }

        /// <summary>旧格式（data\applied-*.jsonl）所在目录，只在导入时用。</summary>
        public string LegacyDirectory { get; private set; }

        public int RetentionDays { get; private set; }
        public int CompactWhenWalLines { get; private set; }
        public int CompactEveryMinutes { get; private set; }
        public string LastCompactAt { get; private set; }

        private string IndexPath
        {
            get { return Path.Combine(DirectoryPath, "applied-index.jsonl"); }
        }

        private static int ReadInt(IConfiguration config, string key, int fallback)
        {
            int value;
            string text = config[key];
            if (!string.IsNullOrEmpty(text) && int.TryParse(text, out value))
            {
                return value;
            }
            return fallback;
        }

        /// <summary>
        /// 载入归档，返回 traceId → 指纹集合。
        /// 载入后会立刻整理一次（把 WAL 并进索引、导入旧格式、清掉过期项），
        /// 所以启动完成后磁盘上一定是一份紧凑的索引。
        /// </summary>
        public Dictionary<string, HashSet<string>> Load()
        {
            lock (_sync)
            {
                DateTime start = DateTime.UtcNow;
                ReadIndex();
                int walFiles = ReadWalFiles();
                int legacyFiles = ImportLegacy();
                Compact();

                foreach (KeyValuePair<string, HashSet<string>> pair in _index)
                {
                    if (!_lastSeen.ContainsKey(pair.Key))
                    {
                        _lastSeen[pair.Key] = NowMs();
                    }
                }

                _logger.LogInformation(
                    "去重指纹已载入：{0} 个 traceId / {1} 条指纹（WAL {2} 个文件，旧格式 {3} 个文件，耗时 {4} ms）",
                    _index.Count, CountKeys(), walFiles, legacyFiles,
                    (int)(DateTime.UtcNow - start).TotalMilliseconds);

                Dictionary<string, HashSet<string>> copy =
                    new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, HashSet<string>> pair in _index)
                {
                    copy[pair.Key] = new HashSet<string>(pair.Value, StringComparer.OrdinalIgnoreCase);
                }

                StartTimer();
                return copy;
            }
        }

        private void ReadIndex()
        {
            string path = IndexPath;
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                foreach (string line in ReadLinesShared(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    IndexLine item;
                    try
                    {
                        item = JsonSerializer.Deserialize<IndexLine>(line, JsonOptions);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (item == null || string.IsNullOrEmpty(item.t))
                    {
                        continue;
                    }

                    HashSet<string> keys = EnsureEntry(item.t);
                    if (item.k != null)
                    {
                        for (int i = 0; i < item.k.Count; i++)
                        {
                            if (!string.IsNullOrEmpty(item.k[i]))
                            {
                                keys.Add(item.k[i]);
                            }
                        }
                    }
                    if (item.s > 0)
                    {
                        _lastSeen[item.t] = item.s;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("读去重索引失败（按空索引继续）：{0}", ex.Message);
            }
        }

        private int ReadWalFiles()
        {
            string[] files;
            try
            {
                files = System.IO.Directory.GetFiles(DirectoryPath, "wal-*.jsonl");
            }
            catch (Exception)
            {
                return 0;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int f = 0; f < files.Length; f++)
            {
                try
                {
                    foreach (string line in ReadLinesShared(files[f]))
                    {
                        WalLine item = ParseWal(line);
                        if (item != null)
                        {
                            EnsureEntry(item.t).Add(item.k);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("读去重 WAL 失败：{0}（{1}）", files[f], ex.Message);
                }
            }
            return files.Length;
        }

        /// <summary>把旧格式（data\applied-yyyyMMdd.jsonl）导入进来，然后改名成 *.imported（不删原件）。</summary>
        private int ImportLegacy()
        {
            string[] files;
            try
            {
                if (!System.IO.Directory.Exists(LegacyDirectory))
                {
                    return 0;
                }
                files = System.IO.Directory.GetFiles(LegacyDirectory, "applied-*.jsonl");
            }
            catch (Exception)
            {
                return 0;
            }

            int imported = 0;
            for (int f = 0; f < files.Length; f++)
            {
                try
                {
                    foreach (string line in ReadLinesShared(files[f]))
                    {
                        WalLine item = ParseWal(line);
                        if (item != null)
                        {
                            EnsureEntry(item.t).Add(item.k);
                            _importedLegacy++;
                        }
                    }

                    File.Move(files[f], files[f] + ".imported");
                    imported++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("导入旧格式去重指纹失败：{0}（{1}）", files[f], ex.Message);
                }
            }

            if (imported > 0)
            {
                _logger.LogInformation("已导入 {0} 个旧格式去重文件（已改名为 *.imported，原件保留）", imported);
            }
            return imported;
        }

        private static WalLine ParseWal(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            try
            {
                WalLine item = JsonSerializer.Deserialize<WalLine>(line, JsonOptions);
                if (item != null && !string.IsNullOrEmpty(item.t) && !string.IsNullOrEmpty(item.k))
                {
                    return item;
                }
            }
            catch (JsonException)
            {
            }
            return null;
        }

        private HashSet<string> EnsureEntry(string traceId)
        {
            HashSet<string> keys;
            if (!_index.TryGetValue(traceId, out keys))
            {
                keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _index[traceId] = keys;
            }
            return keys;
        }

        /// <summary>
        /// 记录"某包裹的某条事件已经处理过"。
        /// 只做一次顺序追加，不改写索引 —— 每个包裹事件调用一次也扛得住。
        /// </summary>
        public void Append(string traceId, string key)
        {
            if (string.IsNullOrEmpty(traceId) || string.IsNullOrEmpty(key))
            {
                return;
            }

            lock (_sync)
            {
                EnsureEntry(traceId).Add(key);
                _lastSeen[traceId] = NowMs();

                try
                {
                    EnsureWalWriter();
                    _walWriter.WriteLine(JsonSerializer.Serialize(new WalLine { t = traceId, k = key }, JsonOptions));
                    _walLines++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("写去重 WAL 失败（内存中已生效，重启后这条指纹会丢）：{0}", ex.Message);
                }
            }
        }

        private void EnsureWalWriter()
        {
            if (_walWriter != null && _walLines < 20000)
            {
                return;
            }

            CloseWalWriter();
            string path = Path.Combine(DirectoryPath,
                "wal-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl");
            _walWriter = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 1),
                new UTF8Encoding(false));
            _walWriter.AutoFlush = true;
            _walLines = 0;
        }

        private void CloseWalWriter()
        {
            if (_walWriter == null)
            {
                return;
            }
            try
            {
                _walWriter.Flush();
                _walWriter.Dispose();
            }
            catch (Exception)
            {
            }
            _walWriter = null;
        }

        /// <summary>
        /// 整理：把内存索引写成新的索引文件（原子替换）→ 删掉已合并的 WAL → 丢掉过期项。
        /// 平台启动时自动跑一次；运行期由定时器触发。
        /// </summary>
        public void Compact()
        {
            lock (_sync)
            {
                try
                {
                    CloseWalWriter();

                    long cutoff = NowMs() - (long)Math.Max(1, RetentionDays) * 24 * 3600 * 1000;
                    List<string> expired = new List<string>();
                    foreach (KeyValuePair<string, long> pair in _lastSeen)
                    {
                        if (pair.Value < cutoff)
                        {
                            expired.Add(pair.Key);
                        }
                    }
                    for (int i = 0; i < expired.Count; i++)
                    {
                        _index.Remove(expired[i]);
                        _lastSeen.Remove(expired[i]);
                    }
                    _droppedExpired += expired.Count;

                    StringBuilder sb = new StringBuilder();
                    int entries = 0;
                    foreach (KeyValuePair<string, HashSet<string>> pair in _index)
                    {
                        if (pair.Value.Count == 0)
                        {
                            continue;
                        }

                        IndexLine line = new IndexLine();
                        line.t = pair.Key;
                        line.k = new List<string>(pair.Value);
                        line.s = _lastSeen.ContainsKey(pair.Key) ? _lastSeen[pair.Key] : NowMs();
                        sb.AppendLine(JsonSerializer.Serialize(line, JsonOptions));
                        entries++;
                    }

                    string tmp = IndexPath + ".tmp";
                    File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                    if (File.Exists(IndexPath))
                    {
                        File.Replace(tmp, IndexPath, null);
                    }
                    else
                    {
                        File.Move(tmp, IndexPath);
                    }

                    int removedWal = 0;
                    string[] walFiles = System.IO.Directory.GetFiles(DirectoryPath, "wal-*.jsonl");
                    for (int i = 0; i < walFiles.Length; i++)
                    {
                        try
                        {
                            File.Delete(walFiles[i]);
                            removedWal++;
                        }
                        catch (Exception)
                        {
                        }
                    }

                    _walLines = 0;
                    _compactions++;
                    LastCompactAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                    _logger.LogInformation("去重指纹整理完成：{0} 个 traceId（清理过期 {1}，合并并删除 WAL {2} 个）",
                        entries, expired.Count, removedWal);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("去重指纹整理失败（不影响去重：内存与 WAL 仍然有效）：{0}", ex.Message);
                }
            }
        }

        private void StartTimer()
        {
            int minutes = Math.Max(1, CompactEveryMinutes);
            _timer = new Timer(OnTimer, null, TimeSpan.FromMinutes(minutes), TimeSpan.FromMinutes(minutes));
        }

        private void OnTimer(object state)
        {
            try
            {
                bool need;
                lock (_sync)
                {
                    need = _walLines >= Math.Max(100, CompactWhenWalLines);
                }
                if (need)
                {
                    Compact();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("定时整理去重指纹失败：{0}", ex.Message);
            }
        }

        public DedupStats Stats()
        {
            DedupStats stats = new DedupStats();
            lock (_sync)
            {
                stats.directory = DirectoryPath;
                stats.indexEntries = _index.Count;
                stats.indexKeys = CountKeys();
                stats.walLines = _walLines;
                stats.retentionDays = RetentionDays;
                stats.compactWhenWalLines = CompactWhenWalLines;
                stats.compactEveryMinutes = CompactEveryMinutes;
                stats.lastCompactAt = LastCompactAt;
                stats.compactions = _compactions;
                stats.droppedExpired = _droppedExpired;
                stats.importedLegacy = _importedLegacy;

                try
                {
                    FileInfo index = new FileInfo(IndexPath);
                    stats.indexBytes = index.Exists ? index.Length : 0;

                    long walBytes = 0;
                    string[] walFiles = System.IO.Directory.GetFiles(DirectoryPath, "wal-*.jsonl");
                    for (int i = 0; i < walFiles.Length; i++)
                    {
                        walBytes += new FileInfo(walFiles[i]).Length;
                    }
                    stats.walBytes = walBytes;
                }
                catch (Exception)
                {
                }
            }
            return stats;
        }

        private int CountKeys()
        {
            int total = 0;
            foreach (HashSet<string> keys in _index.Values)
            {
                total += keys.Count;
            }
            return total;
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;

                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }
                CloseWalWriter();
            }
        }
    }
}
