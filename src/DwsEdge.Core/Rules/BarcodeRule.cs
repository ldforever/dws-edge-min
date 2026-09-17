using System;
using System.Collections.Generic;

namespace DwsEdge.Core.Rules
{
    /// <summary>
    /// 一条条码过滤规则。
    ///
    /// 匹配语义：一条规则里填了的条件必须**全部满足**（AND）；
    /// 规则之间按 priority 从小到大依次判断，**第一条命中的规则决定结果**（先到先得）。
    /// 没有任何规则命中时，按规则集的 defaultAction 处理。
    ///
    /// 条件字段（都可留空，留空表示该条件不参与判断）：
    ///   minLength / maxLength  长度范围（含边界）
    ///   prefix / suffix        前/后缀
    ///   regex                  正则（带超时保护，写错了只会判不中，不会把平台卡死）
    ///   whitelist / blacklist  名单，支持 * 通配（见 BarcodeFilter.MatchesList）
    ///
    /// 注意：这里刻意用**小写属性名**，因为规则要经过 JSON 在
    /// 「规则文件 → 平台 → 前端界面」之间来回传，名字必须和线上格式一致；
    /// 而且 Core 同时面向 net48，不能依赖 System.Text.Json 的 [JsonPropertyName]。
    /// 顺带一个坑：属性（property）才会被 System.Text.Json 读写，字段（field）默认被忽略 ——
    /// 这也是这个类用属性而不是字段的原因。
    /// </summary>
    public sealed class BarcodeRule
    {
        /// <summary>动作：保留。</summary>
        public const string ActionKeep = "keep";

        /// <summary>动作：丢弃。</summary>
        public const string ActionDrop = "drop";

        /// <summary>规则名，现场排查时会打进日志和界面。</summary>
        public string name { get; set; }

        /// <summary>优先级，越小越先判断（建议留间隔，方便插入：10、20、30…）。</summary>
        public int priority { get; set; } = 100;

        /// <summary>false 的规则直接跳过。</summary>
        public bool enabled { get; set; } = true;

        /// <summary>命中后的动作：keep / drop。</summary>
        public string action { get; set; } = ActionKeep;

        public string remark { get; set; }

        public int? minLength { get; set; }
        public int? maxLength { get; set; }
        public string prefix { get; set; }
        public string suffix { get; set; }
        public string regex { get; set; }
        public List<string> whitelist { get; set; }
        public List<string> blacklist { get; set; }

        public bool IsDrop
        {
            get { return string.Equals(action, ActionDrop, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>条件摘要，给界面和日志用（例如 "长度 12-14 且 前缀=SF"）。</summary>
        public string Describe()
        {
            List<string> parts = new List<string>();
            if (minLength.HasValue || maxLength.HasValue)
            {
                string from = minLength.HasValue ? minLength.Value.ToString() : "*";
                string to = maxLength.HasValue ? maxLength.Value.ToString() : "*";
                parts.Add("长度 " + from + "-" + to);
            }
            if (!string.IsNullOrEmpty(prefix))
            {
                parts.Add("前缀=" + prefix);
            }
            if (!string.IsNullOrEmpty(suffix))
            {
                parts.Add("后缀=" + suffix);
            }
            if (!string.IsNullOrEmpty(regex))
            {
                parts.Add("正则=" + regex);
            }
            if (whitelist != null && whitelist.Count > 0)
            {
                parts.Add("白名单(" + whitelist.Count + ")");
            }
            if (blacklist != null && blacklist.Count > 0)
            {
                parts.Add("黑名单(" + blacklist.Count + ")");
            }

            if (parts.Count == 0)
            {
                parts.Add("无条件（命中所有条码）");
            }

            return string.Join(" 且 ", parts.ToArray());
        }
    }

    /// <summary>规则集：默认动作 + 规则列表。</summary>
    public sealed class BarcodeRuleSet
    {
        /// <summary>没有规则命中时的默认动作：keep（默认）或 drop。</summary>
        public string defaultAction { get; set; } = BarcodeRule.ActionKeep;

        /// <summary>条码比较是否忽略大小写（默认忽略）。</summary>
        public bool ignoreCase { get; set; } = true;

        public List<BarcodeRule> rules { get; set; } = new List<BarcodeRule>();

        public bool DefaultDrop
        {
            get { return string.Equals(defaultAction, BarcodeRule.ActionDrop, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>按优先级排好序的启用规则（不改动原列表）。</summary>
        public List<BarcodeRule> EnabledByPriority()
        {
            List<BarcodeRule> list = new List<BarcodeRule>();
            if (rules != null)
            {
                for (int i = 0; i < rules.Count; i++)
                {
                    if (rules[i] != null && rules[i].enabled)
                    {
                        list.Add(rules[i]);
                    }
                }
            }

            list.Sort(delegate(BarcodeRule a, BarcodeRule b)
            {
                int byPriority = a.priority.CompareTo(b.priority);
                if (byPriority != 0)
                {
                    return byPriority;
                }
                return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }
    }
}
