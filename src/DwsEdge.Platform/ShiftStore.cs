using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>一个班次：名称 + 起止时刻（HH:mm，允许跨天，比如 20:00-08:00）。</summary>
    public sealed class ShiftDef
    {
        public string name { get; set; }
        public string start { get; set; }
        public string end { get; set; }
    }

    /// <summary>班次表（runtime\config\shifts.json）。</summary>
    public sealed class ShiftPlan
    {
        public List<ShiftDef> shifts { get; set; } = new List<ShiftDef>();

        /// <summary>
        /// 把一个时间点归到某个班次。
        /// 跨天班（start &gt; end，例如 20:00-08:00）在凌晨那一段算"前一天"的班，
        /// 这样夜班的统计口径才符合现场习惯（凌晨 2 点的包裹属于昨晚那一班）。
        /// </summary>
        public bool TryClassify(DateTime time, out string key, out string label)
        {
            key = null;
            label = null;
            if (shifts == null)
            {
                return false;
            }

            for (int i = 0; i < shifts.Count; i++)
            {
                ShiftDef shift = shifts[i];
                TimeSpan start;
                TimeSpan end;
                if (shift == null || !TryParseTime(shift.start, out start) || !TryParseTime(shift.end, out end))
                {
                    continue;
                }

                DateTime shiftDate;
                if (start <= end)
                {
                    TimeSpan now = time.TimeOfDay;
                    if (now < start || now >= end)
                    {
                        continue;
                    }
                    shiftDate = time.Date;
                }
                else
                {
                    // 跨天：晚上这一段算当天，凌晨这一段算前一天
                    if (time.TimeOfDay >= start)
                    {
                        shiftDate = time.Date;
                    }
                    else if (time.TimeOfDay < end)
                    {
                        shiftDate = time.Date.AddDays(-1);
                    }
                    else
                    {
                        continue;
                    }
                }

                string day = shiftDate.ToString("yyyy-MM-dd");
                key = shift.name + "@" + day;
                label = shift.name + "（" + day + " " + shift.start + "-" + shift.end + "）";
                return true;
            }
            return false;
        }

        internal static bool TryParseTime(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            string[] parts = text.Trim().Split(':');
            int hour;
            int minute;
            if (parts.Length != 2 || !int.TryParse(parts[0], out hour) || !int.TryParse(parts[1], out minute))
            {
                return false;
            }
            if (hour < 0 || hour > 23 || minute < 0 || minute > 59)
            {
                return false;
            }
            value = new TimeSpan(hour, minute, 0);
            return true;
        }
    }

    /// <summary>
    /// C4：班次配置（runtime\config\shifts.json，热加载）。
    /// 统计看板按它把包裹分到"白班/夜班（或现场自己的班次）"。
    /// </summary>
    public sealed class ShiftStore
    {
        private readonly ILogger<ShiftStore> _logger;
        private readonly object _sync = new object();
        private ShiftPlan _plan = new ShiftPlan();
        private string _path;
        private DateTime _loadedAtUtc = DateTime.MinValue;
        private DateTime _lastCheckUtc = DateTime.MinValue;

        public ShiftStore(IConfiguration config, ILogger<ShiftStore> logger)
        {
            _logger = logger;
            string root = config["Runtime:Root"] ?? "..";
            string runtimeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, root));
            _path = Path.Combine(runtimeRoot, @"config\shifts.json");

            EnsureDefault();
            Reload();
        }

        public string FilePath
        {
            get { return _path; }
        }

        public ShiftPlan Plan
        {
            get { EnsureFresh(); lock (_sync) { return _plan; } }
        }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private void EnsureDefault()
        {
            try
            {
                if (File.Exists(_path))
                {
                    return;
                }
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                ShiftPlan plan = new ShiftPlan();
                plan.shifts.Add(new ShiftDef { name = "白班", start = "08:00", end = "20:00" });
                plan.shifts.Add(new ShiftDef { name = "夜班", start = "20:00", end = "08:00" });
                File.WriteAllText(_path, JsonSerializer.Serialize(plan, Json), new UTF8Encoding(false));
                _logger.LogInformation("已生成班次配置模板：{0}", _path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("生成班次配置失败：{0}", ex.Message);
            }
        }

        private void Reload()
        {
            ShiftPlan loaded = null;
            try
            {
                if (File.Exists(_path))
                {
                    string text = File.ReadAllText(_path, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        loaded = JsonSerializer.Deserialize<ShiftPlan>(text, Json);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("班次配置解析失败，沿用上一份：{0}", ex.Message);
            }

            if (loaded == null || loaded.shifts == null || loaded.shifts.Count == 0)
            {
                loaded = new ShiftPlan();
                loaded.shifts.Add(new ShiftDef { name = "白班", start = "08:00", end = "20:00" });
                loaded.shifts.Add(new ShiftDef { name = "夜班", start = "20:00", end = "08:00" });
            }

            lock (_sync)
            {
                _plan = loaded;
                _loadedAtUtc = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
            }
        }

        private void EnsureFresh()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastCheckUtc).TotalMilliseconds < 1000)
            {
                return;
            }
            _lastCheckUtc = now;
            try
            {
                if (File.Exists(_path) && File.GetLastWriteTimeUtc(_path) != _loadedAtUtc)
                {
                    Reload();
                    _logger.LogInformation("班次配置已变化，已重新加载：{0}", _path);
                }
            }
            catch (Exception)
            {
            }
        }

        public object Read()
        {
            ShiftPlan plan = Plan;
            List<object> items = new List<object>();
            for (int i = 0; i < plan.shifts.Count; i++)
            {
                ShiftDef shift = plan.shifts[i];
                items.Add(new
                {
                    index = i + 1,
                    name = shift.name,
                    start = shift.start,
                    end = shift.end,
                    span = SpanText(shift)
                });
            }

            return new
            {
                file = _path,
                shifts = items,
                note = "跨天班次（如 20:00-08:00）在凌晨那一段算前一天；改完立即生效（看板按新班次重新统计）"
            };
        }

        private static string SpanText(ShiftDef shift)
        {
            TimeSpan start;
            TimeSpan end;
            if (!ShiftPlan.TryParseTime(shift.start, out start) || !ShiftPlan.TryParseTime(shift.end, out end))
            {
                return "时间格式不对";
            }
            double hours = (end - start).TotalHours;
            if (hours <= 0)
            {
                hours += 24;   // 跨天
            }
            return hours.ToString("0.#") + " 小时" + (start > end ? "（跨天）" : "");
        }

        public List<string> Validate(ShiftPlan plan)
        {
            List<string> problems = new List<string>();
            if (plan == null || plan.shifts == null || plan.shifts.Count == 0)
            {
                problems.Add("至少要配置一个班次");
                return problems;
            }
            if (plan.shifts.Count > 8)
            {
                problems.Add("班次最多 8 个");
            }

            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < plan.shifts.Count; i++)
            {
                ShiftDef shift = plan.shifts[i];
                if (shift == null || string.IsNullOrWhiteSpace(shift.name))
                {
                    problems.Add("第 " + (i + 1) + " 个班次没有名称");
                    continue;
                }
                if (!names.Add(shift.name.Trim()))
                {
                    problems.Add("班次名称重复：" + shift.name);
                }
                TimeSpan start;
                TimeSpan end;
                if (!ShiftPlan.TryParseTime(shift.start, out start))
                {
                    problems.Add("班次「" + shift.name + "」的开始时间要写成 HH:mm（例如 08:00）");
                }
                if (!ShiftPlan.TryParseTime(shift.end, out end))
                {
                    problems.Add("班次「" + shift.name + "」的结束时间要写成 HH:mm（例如 20:00）");
                }
                if (ShiftPlan.TryParseTime(shift.start, out start) && ShiftPlan.TryParseTime(shift.end, out end) && start == end)
                {
                    problems.Add("班次「" + shift.name + "」开始与结束相同，会覆盖整天");
                }
            }
            return problems;
        }

        /// <summary>保存班次（自动备份）。</summary>
        public string Save(ShiftPlan plan)
        {
            List<string> problems = Validate(plan);
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", problems.ToArray()));
            }

            string backup = null;
            lock (_sync)
            {
                if (File.Exists(_path))
                {
                    backup = _path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(_path, backup, true);
                }
                File.WriteAllText(_path, JsonSerializer.Serialize(plan, Json), new UTF8Encoding(false));
                _plan = plan;
                _loadedAtUtc = File.GetLastWriteTimeUtc(_path);
            }
            _logger.LogInformation("班次配置已保存：{0}", _path);
            return backup;
        }
    }
}
