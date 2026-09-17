using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using DwsEdge.Core.Rules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>
    /// 条码规则的文件存储（B2）。
    ///
    /// 文件：runtime\config\barcode-rules.json（UTF-8）
    ///   * 第一次运行时会写一份带示例的默认文件（示例规则都是 enabled=false，不影响行为）；
    ///   * 保存前自动备份成 barcode-rules.json.bak-&lt;时间戳&gt;；
    ///   * 现场直接改文件也能生效 —— 每秒检查一次修改时间，变了就重新加载（热加载，不用重启平台）。
    /// </summary>
    public sealed class BarcodeRuleStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly ILogger<BarcodeRuleStore> _logger;
        private readonly object _sync = new object();
        private BarcodeRuleSet _ruleSet = new BarcodeRuleSet();
        private BarcodeFilter _filter;
        private DateTime _loadedAtUtc = DateTime.MinValue;
        private DateTime _lastCheckUtc = DateTime.MinValue;
        private int _regexTimeoutReports;

        public BarcodeRuleStore(IConfiguration config, ILogger<BarcodeRuleStore> logger)
        {
            _logger = logger;
            string root = config["Runtime:Root"] ?? "..";
            string runtimeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, root));
            FilePath = Path.Combine(runtimeRoot, @"config\barcode-rules.json");

            EnsureDefaultFile();
            Reload();
        }

        public string FilePath { get; private set; }

        /// <summary>当前生效的规则集（会顺带做热加载检查）。</summary>
        public BarcodeRuleSet Current
        {
            get
            {
                EnsureFresh();
                lock (_sync)
                {
                    return _ruleSet;
                }
            }
        }

        /// <summary>当前生效的过滤器（平台按事件过滤条码时用它）。</summary>
        public BarcodeFilter Filter
        {
            get
            {
                EnsureFresh();
                lock (_sync)
                {
                    return _filter;
                }
            }
        }

        public bool FileExists
        {
            get { return File.Exists(FilePath); }
        }

        /// <summary>用给定的规则集做一次临时过滤（规则测试接口用，不落盘、不影响运行中的规则）。</summary>
        public BarcodeFilter CreateFilter(BarcodeRuleSet ruleSet)
        {
            BarcodeFilter filter = new BarcodeFilter(ruleSet);
            filter.OnRegexTimeout = ReportRegexTimeout;
            return filter;
        }

        /// <summary>保存规则集到文件（自动备份），随后立即生效。</summary>
        public string Save(BarcodeRuleSet ruleSet)
        {
            if (ruleSet == null)
            {
                throw new ArgumentNullException("ruleSet");
            }

            List<string> problems = Validate(ruleSet);
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", problems.ToArray()));
            }

            string backup = null;
            string dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            lock (_sync)
            {
                if (File.Exists(FilePath))
                {
                    backup = FilePath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(FilePath, backup, true);
                }

                File.WriteAllText(FilePath, JsonSerializer.Serialize(ruleSet, JsonOptions), new UTF8Encoding(false));
                Apply(ruleSet);
                _loadedAtUtc = File.GetLastWriteTimeUtc(FilePath);
            }

            _logger.LogInformation("条码规则已保存：{0} 条规则（默认动作 {1}）→ {2}",
                ruleSet.rules != null ? ruleSet.rules.Count : 0, ruleSet.defaultAction, FilePath);
            return backup;
        }

        /// <summary>校验规则集：优先级重复、正则语法、长度范围、动作取值。</summary>
        public List<string> Validate(BarcodeRuleSet ruleSet)
        {
            List<string> problems = new List<string>();
            if (ruleSet == null)
            {
                problems.Add("规则集为空");
                return problems;
            }

            if (!IsKnownAction(ruleSet.defaultAction))
            {
                problems.Add("默认动作只能是 keep 或 drop");
            }

            if (ruleSet.rules == null)
            {
                return problems;
            }

            HashSet<int> priorities = new HashSet<int>();
            for (int i = 0; i < ruleSet.rules.Count; i++)
            {
                BarcodeRule rule = ruleSet.rules[i];
                if (rule == null)
                {
                    continue;
                }

                string label = string.IsNullOrEmpty(rule.name) ? ("第 " + (i + 1) + " 条") : rule.name;
                if (string.IsNullOrEmpty(rule.name))
                {
                    problems.Add(label + "：规则名不能为空");
                }
                if (!IsKnownAction(rule.action))
                {
                    problems.Add(label + "：动作只能是 keep 或 drop");
                }
                if (rule.minLength.HasValue && rule.maxLength.HasValue
                    && rule.minLength.Value > rule.maxLength.Value)
                {
                    problems.Add(label + "：最小长度大于最大长度");
                }
                if (!priorities.Add(rule.priority))
                {
                    problems.Add(label + "：优先级 " + rule.priority + " 与其他规则重复（重复时按规则名排序，结果不好预期）");
                }
                if (!string.IsNullOrEmpty(rule.regex))
                {
                    try
                    {
                        System.Text.RegularExpressions.Regex.IsMatch("test", rule.regex,
                            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));
                    }
                    catch (ArgumentException ex)
                    {
                        problems.Add(label + "：正则语法错误 — " + ex.Message);
                    }
                    catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                    {
                        problems.Add(label + "：正则过于复杂（50ms 内跑不完）");
                    }
                }
            }

            return problems;
        }

        private static bool IsKnownAction(string action)
        {
            return string.Equals(action, BarcodeRule.ActionKeep, StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, BarcodeRule.ActionDrop, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>最多每秒检查一次文件修改时间，变了就重新加载（现场直接改文件即可生效）。</summary>
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
                if (!File.Exists(FilePath))
                {
                    return;
                }

                DateTime stamp = File.GetLastWriteTimeUtc(FilePath);
                if (stamp == _loadedAtUtc)
                {
                    return;
                }

                Reload();
                _logger.LogInformation("条码规则文件已变化，已重新加载：{0}", FilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("检查条码规则文件失败：{0}", ex.Message);
            }
        }

        private void Reload()
        {
            BarcodeRuleSet loaded = null;
            try
            {
                if (File.Exists(FilePath))
                {
                    string text = File.ReadAllText(FilePath, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        loaded = JsonSerializer.Deserialize<BarcodeRuleSet>(text, JsonOptions);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("条码规则文件解析失败，本次沿用上一份规则：{0}", ex.Message);
            }

            if (loaded == null)
            {
                loaded = new BarcodeRuleSet();
                _logger.LogInformation("未配置条码规则（{0}）：所有条码原样保留", FilePath);
            }
            else
            {
                List<string> problems = Validate(loaded);
                if (problems.Count > 0)
                {
                    _logger.LogWarning("条码规则有问题（仍会生效，请尽快修正）：{0}", string.Join("；", problems.ToArray()));
                }
                _logger.LogInformation("已加载条码规则：{0} 条（启用 {1} 条），默认动作 {2}",
                    loaded.rules != null ? loaded.rules.Count : 0,
                    loaded.EnabledByPriority().Count,
                    loaded.defaultAction);
            }

            lock (_sync)
            {
                Apply(loaded);
                _loadedAtUtc = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
            }
        }

        private void Apply(BarcodeRuleSet ruleSet)
        {
            _ruleSet = ruleSet;
            _filter = new BarcodeFilter(ruleSet);
            _filter.OnRegexTimeout = ReportRegexTimeout;
        }

        private void ReportRegexTimeout(string ruleName, string pattern)
        {
            // 同一类问题最多报 10 次，避免刷屏
            if (System.Threading.Interlocked.Increment(ref _regexTimeoutReports) <= 10)
            {
                _logger.LogWarning("条码规则「{0}」的正则超过 50ms 未匹配完（{1}）：该条码按「未命中」处理，请简化正则",
                    ruleName, pattern);
            }
        }

        /// <summary>第一次运行时生成一份带示例的默认文件（示例规则都不启用）。</summary>
        private void EnsureDefaultFile()
        {
            if (File.Exists(FilePath))
            {
                return;
            }

            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                BarcodeRuleSet sample = new BarcodeRuleSet();
                sample.defaultAction = BarcodeRule.ActionKeep;
                sample.rules = new List<BarcodeRule>();
                sample.rules.Add(new BarcodeRule
                {
                    name = "只保留 12-14 位、SF/JD 开头的运单号",
                    priority = 10,
                    enabled = false,
                    action = BarcodeRule.ActionKeep,
                    minLength = 12,
                    maxLength = 14,
                    prefix = "SF",
                    remark = "示例：多个条件同时满足才算命中；把 enabled 改成 true 即可生效"
                });
                sample.rules.Add(new BarcodeRule
                {
                    name = "丢弃测试码与设备码",
                    priority = 5,
                    enabled = false,
                    action = BarcodeRule.ActionDrop,
                    prefix = "TEST",
                    remark = "示例：优先级 5 比上面的 10 先判断，所以 TEST 开头的码会被先丢掉"
                });
                sample.rules.Add(new BarcodeRule
                {
                    name = "正则兜底：只认纯数字+字母的 15 位码",
                    priority = 90,
                    enabled = false,
                    action = BarcodeRule.ActionDrop,
                    regex = "^[A-Za-z0-9]{15}$",
                    remark = "示例：正则命中 → 丢弃"
                });

                File.WriteAllText(FilePath, JsonSerializer.Serialize(sample, JsonOptions), new UTF8Encoding(false));
                _logger.LogInformation("已生成条码规则示例文件（规则都是禁用状态，不影响现有行为）：{0}", FilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("生成条码规则示例文件失败：{0}", ex.Message);
            }
        }
    }
}
