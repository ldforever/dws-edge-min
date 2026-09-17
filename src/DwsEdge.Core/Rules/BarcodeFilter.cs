using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DwsEdge.Core.Rules
{
    /// <summary>一条条码的判定结果（规则测试接口和运行期都用它）。</summary>
    public sealed class BarcodeDecision
    {
        // 同样用小写属性名：这几个字段会直接返给前端显示
        public string code { get; set; }

        /// <summary>最终是否保留。</summary>
        public bool kept { get; set; }

        /// <summary>命中的规则名；没有命中任何规则时为空。</summary>
        public string matchedRule { get; set; }

        /// <summary>人话解释，直接显示给现场。</summary>
        public string reason { get; set; }

        /// <summary>命中的规则动作：keep / drop / default。</summary>
        public string action { get; set; }
    }

    /// <summary>整批条码的判定汇总。</summary>
    public sealed class BarcodeFilterReport
    {
        public List<BarcodeDecision> decisions { get; set; } = new List<BarcodeDecision>();
        public int kept { get; set; }
        public int dropped { get; set; }
    }

    /// <summary>
    /// 条码过滤引擎（纯逻辑，不依赖 IO，方便单测和以后下发到采集侧）。
    ///
    /// 判定顺序：
    ///   1. 按优先级取启用的规则；
    ///   2. 逐条判断条件（填了的条件必须全部满足），**第一条命中的规则决定结果**；
    ///   3. 一条都没命中 → 用 ruleSet.defaultAction。
    ///
    /// 现场安全：
    ///   * 正则带 50ms 超时，写错了只会"判不中"，不会把平台卡死；
    ///   * 名单支持 * 通配（SF* / *0001 / *JD*）。
    /// </summary>
    public sealed class BarcodeFilter
    {
        private readonly BarcodeRuleSet _ruleSet;
        private readonly StringComparison _comparison;

        /// <summary>正则超时回调：现场规则写坏时用来提示，最多报几次由调用方决定。</summary>
        public Action<string, string> OnRegexTimeout { get; set; }

        public BarcodeFilter(BarcodeRuleSet ruleSet)
        {
            _ruleSet = ruleSet ?? new BarcodeRuleSet();
            _comparison = _ruleSet.ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        }

        public BarcodeRuleSet RuleSet
        {
            get { return _ruleSet; }
        }

        /// <summary>判定单个条码。</summary>
        public BarcodeDecision Decide(string code)
        {
            BarcodeDecision decision = new BarcodeDecision();
            decision.code = code;

            if (string.IsNullOrEmpty(code))
            {
                decision.kept = false;
                decision.action = BarcodeRule.ActionDrop;
                decision.reason = "空条码";
                return decision;
            }

            List<BarcodeRule> ordered = _ruleSet.EnabledByPriority();
            for (int i = 0; i < ordered.Count; i++)
            {
                BarcodeRule rule = ordered[i];
                if (!Matches(rule, code))
                {
                    continue;
                }

                decision.matchedRule = rule.name;
                decision.action = rule.action;
                decision.kept = !rule.IsDrop;
                decision.reason = "命中规则「" + rule.name + "」（优先级 " + rule.priority + "，"
                    + rule.Describe() + "）→ " + (rule.IsDrop ? "丢弃" : "保留");
                return decision;
            }

            decision.kept = !_ruleSet.DefaultDrop;
            decision.action = _ruleSet.DefaultDrop ? BarcodeRule.ActionDrop : BarcodeRule.ActionKeep;
            decision.reason = _ruleSet.DefaultDrop
                ? "没有规则命中，按默认动作丢弃"
                : "没有规则命中，按默认动作保留";
            return decision;
        }

        /// <summary>批量判定（顺序与传入一致）。</summary>
        public BarcodeFilterReport Decide(IEnumerable<string> codes)
        {
            BarcodeFilterReport report = new BarcodeFilterReport();
            if (codes == null)
            {
                return report;
            }

            foreach (string code in codes)
            {
                BarcodeDecision decision = Decide(code);
                report.decisions.Add(decision);
                if (decision.kept)
                {
                    report.kept++;
                }
                else
                {
                    report.dropped++;
                }
            }
            return report;
        }

        /// <summary>一条规则是否命中（填了的条件必须全部满足）。</summary>
        public bool Matches(BarcodeRule rule, string code)
        {
            if (rule == null || string.IsNullOrEmpty(code))
            {
                return false;
            }

            if (rule.minLength.HasValue && code.Length < rule.minLength.Value)
            {
                return false;
            }
            if (rule.maxLength.HasValue && code.Length > rule.maxLength.Value)
            {
                return false;
            }
            if (!string.IsNullOrEmpty(rule.prefix)
                && !code.StartsWith(rule.prefix, _comparison))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(rule.suffix)
                && !code.EndsWith(rule.suffix, _comparison))
            {
                return false;
            }
            if (rule.whitelist != null && rule.whitelist.Count > 0 && !MatchesList(rule.whitelist, code))
            {
                return false;
            }
            if (rule.blacklist != null && rule.blacklist.Count > 0 && !MatchesList(rule.blacklist, code))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(rule.regex) && !MatchesRegex(rule, code))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 名单匹配：支持精确值，也支持 * 通配（SF* / *0001 / *JD*）。
        /// 空名单视为"不参与判断"（由调用方保证不会走到这里）。
        /// </summary>
        public bool MatchesList(List<string> list, string code)
        {
            if (list == null)
            {
                return false;
            }

            for (int i = 0; i < list.Count; i++)
            {
                string item = list[i];
                if (string.IsNullOrEmpty(item))
                {
                    continue;
                }

                if (item.IndexOf('*') < 0)
                {
                    if (string.Equals(item, code, _comparison))
                    {
                        return true;
                    }
                    continue;
                }

                // 通配：去掉两端的 *，中间的 * 当作"任意字符"简单地按分段匹配
                string pattern = item;
                bool startsAny = pattern.StartsWith("*", StringComparison.Ordinal);
                bool endsAny = pattern.EndsWith("*", StringComparison.Ordinal);
                string core = pattern.Trim('*');

                if (core.Length == 0)
                {
                    return true;   // 单个 * = 匹配所有
                }

                if (startsAny && endsAny)
                {
                    if (code.IndexOf(core, _comparison) >= 0)
                    {
                        return true;
                    }
                }
                else if (startsAny)
                {
                    if (code.EndsWith(core, _comparison))
                    {
                        return true;
                    }
                }
                else if (endsAny)
                {
                    if (code.StartsWith(core, _comparison))
                    {
                        return true;
                    }
                }
                else if (core.IndexOf('*') >= 0)
                {
                    // 形如 SF*001：按 * 切段，逐段顺序匹配
                    string[] segments = pattern.Split('*');
                    int position = 0;
                    bool ok = true;
                    for (int s = 0; s < segments.Length; s++)
                    {
                        string segment = segments[s];
                        if (segment.Length == 0)
                        {
                            continue;
                        }
                        int found = code.IndexOf(segment, position, _comparison);
                        if (found < 0)
                        {
                            ok = false;
                            break;
                        }
                        position = found + segment.Length;
                    }
                    if (ok)
                    {
                        return true;
                    }
                }
                else if (string.Equals(core, code, _comparison))
                {
                    return true;
                }
            }

            return false;
        }

        private bool MatchesRegex(BarcodeRule rule, string code)
        {
            try
            {
                RegexOptions options = RegexOptions.CultureInvariant;
                if (_ruleSet.ignoreCase)
                {
                    options |= RegexOptions.IgnoreCase;
                }

                return Regex.IsMatch(code, rule.regex, options, TimeSpan.FromMilliseconds(50));
            }
            catch (RegexMatchTimeoutException)
            {
                if (OnRegexTimeout != null)
                {
                    OnRegexTimeout(rule.name, rule.regex);
                }
                return false;
            }
            catch (ArgumentException)
            {
                // 正则语法错误：判不中（保存规则时会先做校验，正常不会走到这）
                return false;
            }
        }
    }
}
