using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>C6：一个日志来源（界面上一个卡片 + 一个筛选按钮）。</summary>
    public sealed class DiagSource
    {
        public string id { get; set; }
        public string name { get; set; }
        /// <summary>相对 runtime 的目录。</summary>
        public string directory { get; set; }
        public string pattern { get; set; }
        /// <summary>排除的文件名通配（逗号分隔）。</summary>
        public string exclude { get; set; }
        public string note { get; set; }
    }

    /// <summary>
    /// C6 日志查看与导出。
    ///
    /// 现场排障时最烦的是"日志散在好几个目录、还要按时间挑"。这里把四类东西统一起来：
    ///   * host      采集宿主日志      runtime\logs\host-*.log
    ///   * platform  平台/运维脚本日志 runtime\logs\*.log（排除 host-*）
    ///   * sdk       大华 SDK 日志     runtime\Log\*.log（Alg/camera/volume/weight/MVP_* …）
    ///   * spool     事件缓冲          runtime\spool\events-*.jsonl（A7）
    ///   * camera    相机状态事件      runtime\data\camera-events-*.jsonl（B8）
    ///   * auth      登录/鉴权审计     runtime\data\auth-events-*.jsonl（B9）
    ///
    /// 时间过滤按"文件名里的日期"优先（按天滚动的日志都带日期），没有日期就按文件修改时间。
    /// 打包用 BCL 的 ZipArchive 直接流式写响应，不落中间文件（离线机器上没有 7zip 也能解开）。
    /// </summary>
    public sealed class LogStore
    {
        private readonly ILogger<LogStore> _logger;
        private readonly string _runtimeRoot;
        private readonly List<DiagSource> _sources = new List<DiagSource>();

        public LogStore(IConfiguration config, ILogger<LogStore> logger)
        {
            _logger = logger;
            _runtimeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, config["Runtime:Root"] ?? ".."));

            _sources.Add(NewSource("host", "采集宿主日志", @"logs", "host-*.log", null,
                "采集宿主控制台日志（上电、触发、出码、切图、错误）"));
            _sources.Add(NewSource("platform", "平台日志", @"logs", "*.log", "host-*",
                "业务平台与运维脚本的日志（合并/去重/下游/监控）"));
            _sources.Add(NewSource("sdk", "SDK 日志", @"Log", "*.log", null,
                "大华 SDK 自己的日志（算法/相机/体积/重量），排查相机侧问题用"));
            _sources.Add(NewSource("spool", "事件缓冲（spool）", @"spool", "events-*.jsonl", null,
                "采集宿主写给平台的规范事件（A7：先落盘再推送）"));
            _sources.Add(NewSource("camera", "相机状态事件", @"data", "camera-events-*.jsonl", null,
                "掉线/上线/恢复/告警事件（B8）"));
            _sources.Add(NewSource("auth", "登录与鉴权审计", @"data", "auth-events-*.jsonl", null,
                "登录成功/失败/锁定/改密/账号变更（B9）"));
        }

        private static DiagSource NewSource(string id, string name, string dir, string pattern, string exclude, string note)
        {
            return new DiagSource
            {
                id = id,
                name = name,
                directory = dir,
                pattern = pattern,
                exclude = exclude,
                note = note
            };
        }

        public List<DiagSource> Sources
        {
            get { return _sources; }
        }

        private DiagSource FindSource(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            for (int i = 0; i < _sources.Count; i++)
            {
                if (string.Equals(_sources[i].id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return _sources[i];
                }
            }
            return null;
        }

        /// <summary>GET /api/diag/sources：每个来源有多少文件、多大、最新的一个是什么。</summary>
        public object Overview()
        {
            List<object> list = new List<object>();
            long allBytes = 0;
            int allFiles = 0;

            for (int i = 0; i < _sources.Count; i++)
            {
                List<FileInfo> files = ListFiles(_sources[i]);
                long bytes = 0;
                DateTime newest = DateTime.MinValue;
                string newestName = null;
                for (int k = 0; k < files.Count; k++)
                {
                    bytes += files[k].Length;
                    if (files[k].LastWriteTime > newest)
                    {
                        newest = files[k].LastWriteTime;
                        newestName = files[k].Name;
                    }
                }
                allBytes += bytes;
                allFiles += files.Count;

                list.Add(new
                {
                    id = _sources[i].id,
                    name = _sources[i].name,
                    directory = Path.Combine(_runtimeRoot, _sources[i].directory),
                    note = _sources[i].note,
                    fileCount = files.Count,
                    totalBytes = bytes,
                    sizeText = SizeText(bytes),
                    newestFile = newestName,
                    newestTime = newest == DateTime.MinValue ? null : newest.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }

            return new
            {
                runtimeRoot = _runtimeRoot,
                totalFiles = allFiles,
                totalBytes = allBytes,
                sizeText = SizeText(allBytes),
                sources = list,
                note = "时间范围按文件名里的日期过滤（按天滚动的日志都带日期），没日期的按文件修改时间"
            };
        }

        private static string SizeText(long bytes)
        {
            if (bytes < 1024) { return bytes + " B"; }
            if (bytes < 1024 * 1024) { return (bytes / 1024.0).ToString("0.0") + " KB"; }
            if (bytes < 1024L * 1024 * 1024) { return (bytes / 1024.0 / 1024).ToString("0.0") + " MB"; }
            return (bytes / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
        }

        private string DirectoryOf(DiagSource source)
        {
            return Path.GetFullPath(Path.Combine(_runtimeRoot, source.directory));
        }

        private List<FileInfo> ListFiles(DiagSource source)
        {
            List<FileInfo> result = new List<FileInfo>();
            string dir = DirectoryOf(source);
            if (!Directory.Exists(dir))
            {
                return result;
            }

            string[] excludes = string.IsNullOrEmpty(source.exclude)
                ? new string[0]
                : source.exclude.Split(',');

            string[] found = Directory.GetFiles(dir, source.pattern);
            for (int i = 0; i < found.Length; i++)
            {
                string name = Path.GetFileName(found[i]);
                bool skip = false;
                for (int k = 0; k < excludes.Length; k++)
                {
                    string pattern = excludes[k].Trim();
                    if (pattern.Length == 0) { continue; }
                    if (MatchesWildcard(name, pattern))
                    {
                        skip = true;
                        break;
                    }
                }
                if (skip)
                {
                    continue;
                }

                try
                {
                    result.Add(new FileInfo(found[i]));
                }
                catch (Exception)
                {
                }
            }

            result.Sort(delegate(FileInfo a, FileInfo b)
            {
                return string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        private static bool MatchesWildcard(string name, string pattern)
        {
            // 只支持前后缀两种用法（host-* / *.tmp），够用且不引正则回溯风险
            if (pattern.StartsWith("*", StringComparison.Ordinal))
            {
                return name.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase);
            }
            if (pattern.EndsWith("*", StringComparison.Ordinal))
            {
                return name.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>从文件名里认日期（yyyyMMdd 或 yyyy-MM-dd）；认不出返回 null。</summary>
        private static DateTime? DayFromName(string name)
        {
            for (int i = 0; i + 8 <= name.Length; i++)
            {
                bool digits = true;
                for (int k = 0; k < 8; k++)
                {
                    if (!char.IsDigit(name[i + k])) { digits = false; break; }
                }
                if (!digits) { continue; }
                if (i > 0 && char.IsDigit(name[i - 1])) { continue; }
                if (i + 8 < name.Length && char.IsDigit(name[i + 8])) { continue; }

                int year = int.Parse(name.Substring(i, 4));
                int month = int.Parse(name.Substring(i + 4, 2));
                int day = int.Parse(name.Substring(i + 6, 2));
                if (year < 2000 || year > 2100 || month < 1 || month > 12 || day < 1 || day > 31)
                {
                    continue;
                }
                try
                {
                    return new DateTime(year, month, day);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
            return null;
        }

        private static DateTime StampOf(FileInfo info)
        {
            DateTime? fromName = DayFromName(info.Name);
            return fromName ?? info.LastWriteTime.Date;
        }

        private static bool InRange(FileInfo info, DateTime from, DateTime to)
        {
            DateTime stamp = StampOf(info);
            return stamp >= from.Date && stamp <= to.Date;
        }

        /// <summary>GET /api/diag/files?source=&from=&to=：某个来源在时间范围内的文件。</summary>
        public object Files(string sourceId, DateTime from, DateTime to)
        {
            DiagSource source = FindSource(sourceId);
            if (source == null)
            {
                throw new InvalidOperationException("未知来源：" + sourceId);
            }

            List<object> list = new List<object>();
            long bytes = 0;
            List<FileInfo> files = ListFiles(source);
            for (int i = 0; i < files.Count; i++)
            {
                if (!InRange(files[i], from, to))
                {
                    continue;
                }
                bytes += files[i].Length;
                list.Add(new
                {
                    name = files[i].Name,
                    size = files[i].Length,
                    sizeText = SizeText(files[i].Length),
                    modified = files[i].LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    day = StampOf(files[i]).ToString("yyyy-MM-dd")
                });
            }

            return new
            {
                source = source.id,
                name = source.name,
                from = from.ToString("yyyy-MM-dd"),
                to = to.ToString("yyyy-MM-dd"),
                fileCount = list.Count,
                totalBytes = bytes,
                sizeText = SizeText(bytes),
                files = list
            };
        }

        private string ResolveFile(DiagSource source, string fileName, bool mustExist = true)
        {
            if (string.IsNullOrWhiteSpace(fileName)
                || fileName.IndexOf('\\') >= 0 || fileName.IndexOf('/') >= 0
                || fileName.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException("只接受文件名，不接受路径：" + fileName);
            }

            string dir = DirectoryOf(source);
            string full = Path.GetFullPath(Path.Combine(dir, fileName));
            if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("越权路径");
            }
            if (mustExist && !File.Exists(full))
            {
                throw new InvalidOperationException("文件不存在：" + fileName);
            }
            return full;
        }

        /// <summary>GET /api/diag/tail?source=&file=&lines=：看文件尾部（界面"查看"用）。</summary>
        public object Tail(string sourceId, string fileName, int lines)
        {
            DiagSource source = FindSource(sourceId);
            if (source == null)
            {
                throw new InvalidOperationException("未知来源：" + sourceId);
            }
            string path = ResolveFile(source, fileName);
            int take = Math.Clamp(lines <= 0 ? 200 : lines, 1, 2000);

            List<string> buffer = new List<string>();
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    buffer.Add(line);
                    if (buffer.Count > take)
                    {
                        buffer.RemoveAt(0);
                    }
                }
            }

            FileInfo info = new FileInfo(path);
            return new
            {
                source = source.id,
                file = fileName,
                lines = buffer.Count,
                sizeText = SizeText(info.Length),
                modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                content = buffer,
                truncated = buffer.Count >= take
            };
        }

        /// <summary>单文件下载（文件名必须落在该来源目录里）。</summary>
        public IResult Download(string sourceId, string fileName)
        {
            DiagSource source = FindSource(sourceId);
            if (source == null)
            {
                throw new InvalidOperationException("未知来源：" + sourceId);
            }
            string path = ResolveFile(source, fileName);
            return Results.File(path, "application/octet-stream", fileName);
        }

        /// <summary>
        /// GET /api/diag/bundle?from=&to=&sources=：一键打包 zip（流式写响应，不落中间文件）。
        /// zip 里按来源分目录，并附一份 README.txt 说明"这是谁、什么范围、机器上的时间"。
        /// </summary>
        public async System.Threading.Tasks.Task BundleAsync(HttpContext context, DateTime from, DateTime to, string sources)
        {
            List<DiagSource> selected = new List<DiagSource>();
            if (string.IsNullOrWhiteSpace(sources))
            {
                selected.AddRange(_sources);
            }
            else
            {
                string[] ids = sources.Split(',');
                for (int i = 0; i < ids.Length; i++)
                {
                    string id = ids[i].Trim();
                    if (id.Length == 0) { continue; }
                    DiagSource source = FindSource(id);
                    if (source == null)
                    {
                        throw new InvalidOperationException("未知来源：" + id);
                    }
                    selected.Add(source);
                }
            }
            if (selected.Count == 0)
            {
                throw new InvalidOperationException("至少要选一个来源");
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string fileName = "dws-diag-" + from.ToString("yyyyMMdd") + "-" + to.ToString("yyyyMMdd") + "-" + stamp + ".zip";

            context.Response.ContentType = "application/zip";
            context.Response.Headers["Content-Disposition"] = "attachment; filename=\"" + fileName + "\"";
            context.Response.Headers["Cache-Control"] = "no-cache";

            // ZipArchive 是同步 API，而 Kestrel 默认禁止同步写响应体 —— 只在这个请求上放开
            IHttpBodyControlFeature bodyFeature = context.Features.Get<IHttpBodyControlFeature>();
            if (bodyFeature != null)
            {
                bodyFeature.AllowSynchronousIO = true;
            }

            int fileCount = 0;
            long bytes = 0;
            using (ZipArchive zip = new ZipArchive(context.Response.Body, ZipArchiveMode.Create, true))
            {
                for (int i = 0; i < selected.Count; i++)
                {
                    DiagSource source = selected[i];
                    string folder = source.id;
                    List<FileInfo> files = ListFiles(source);
                    for (int k = 0; k < files.Count; k++)
                    {
                        if (!InRange(files[k], from, to))
                        {
                            continue;
                        }
                        try
                        {
                            zip.CreateEntryFromFile(files[k].FullName, folder + "/" + files[k].Name, CompressionLevel.Fastest);
                            fileCount++;
                            bytes += files[k].Length;
                        }
                        catch (IOException ex)
                        {
                            // 文件正被写（比如宿主正在写今天的日志）时跳过，不让整包失败
                            _logger.LogWarning("打包跳过 {0}：{1}", files[k].Name, ex.Message);
                        }
                    }
                }

                ZipArchiveEntry readme = zip.CreateEntry("README.txt", CompressionLevel.Fastest);
                using (StreamWriter writer = new StreamWriter(readme.Open(), new UTF8Encoding(true)))
                {
                    writer.WriteLine("DWS 物流解码平台 —— 诊断日志包");
                    writer.WriteLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    writer.WriteLine("时间范围：" + from.ToString("yyyy-MM-dd") + " ~ " + to.ToString("yyyy-MM-dd"));
                    writer.WriteLine("文件数：" + fileCount + "（原始大小 " + SizeText(bytes) + "）");
                    writer.WriteLine();
                    writer.WriteLine("目录对应关系：");
                    for (int i = 0; i < selected.Count; i++)
                    {
                        writer.WriteLine("  " + selected[i].id + "/  ->  " + selected[i].name +
                            "（runtime\\" + selected[i].directory + "\\" + selected[i].pattern + "）");
                        writer.WriteLine("      " + selected[i].note);
                    }
                    writer.WriteLine();
                    writer.WriteLine("说明：");
                    writer.WriteLine("  * 脱机分析时先看 host/ 里的采集日志，再看 sdk/ 里的相机侧日志；");
                    writer.WriteLine("  * spool/ 是平台消费的事件流，能和历史库里的包裹对上；");
                    writer.WriteLine("  * 时间范围按文件名里的日期过滤（按天滚动的日志都带日期）。");
                    writer.WriteLine("  * 解压用系统自带解压即可（标准 zip，无加密）。");
                }
            }
            await context.Response.Body.FlushAsync();
            _logger.LogInformation("诊断包已导出：{0}（{1} 个文件，{2}）", fileName, fileCount, SizeText(bytes));
        }
    }
}
