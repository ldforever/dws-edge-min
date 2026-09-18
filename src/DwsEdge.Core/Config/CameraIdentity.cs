using System;
using System.Collections.Generic;

namespace DwsEdge.Core.Config
{
    /// <summary>
    /// 把 cfg 里声明的相机（<c>&lt;Camera ip=... / key=... / id=... enable="1" /&gt;</c>）
    /// 和 SDK 实际上报的相机标识对上号。
    ///
    /// 为什么需要它：大华 SDK 不回报相机 IP（CameraInfo 只有 ID / 型号 / 序列号 / 厂商 / 固件 / ExtraInfo），
    /// 现场 cfg 里写的是 IP 时，只能拿这些标识去比对：
    ///   第一轮：精确匹配（忽略大小写）；
    ///   第二轮：包含匹配 —— 清单里写完整 id（厂商:序列号）、SDK 只给序列号这类情况。
    ///
    /// 注意：看起来像 IPv4 的声明值只允许精确匹配，避免 100.100.100.1 错配到 100.100.100.11。
    /// 宁可显示成"清单里声明了但没发现"，也不要显示错的对应关系。
    /// </summary>
    public static class CameraIdentity
    {
        /// <summary>在候选标识里找与声明匹配的那一条；找不到返回 null。</summary>
        public static CameraPlanEntry Match(CameraPlan plan, IEnumerable<string> candidates)
        {
            return Match(plan, candidates, null);
        }

        /// <summary>
        /// 在候选标识里找与声明匹配的那一条；找不到返回 null。
        ///
        /// aliases 用来把"声明值"扩展成一组等价标识，典型用法是 IP ↔ SDK Key 对照表：
        /// 清单写 ip=100.100.100.11，而 SDK 上报的是 Key=Huaray Technology:BK27440AAK00036，
        /// 有了对照表就能把这条声明挂到那台设备上，界面不会出现"一条在线一条幽灵离线"。
        /// 传 null 时行为与不带 aliases 的版本一致。
        /// </summary>
        public static CameraPlanEntry Match(CameraPlan plan, IEnumerable<string> candidates, Func<string, string[]> aliases)
        {
            if (plan == null || candidates == null)
            {
                return null;
            }

            List<string> list = new List<string>();
            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate))
                {
                    list.Add(candidate.Trim());
                }
            }
            if (list.Count == 0)
            {
                return null;
            }

            List<CameraPlanEntry> declared = EnabledEntries(plan);

            // 第一轮：精确匹配（声明值本身，加上它的等价标识）
            for (int d = 0; d < declared.Count; d++)
            {
                string[] values = ValuesOf(declared[d].Value, aliases);
                for (int v = 0; v < values.Length; v++)
                {
                    for (int c = 0; c < list.Count; c++)
                    {
                        if (string.Equals(values[v], list[c], StringComparison.OrdinalIgnoreCase))
                        {
                            return declared[d];
                        }
                    }
                }
            }

            // 第二轮：包含匹配 —— 清单里写完整 id（厂商:序列号）、SDK 只给序列号这类情况
            for (int d = 0; d < declared.Count; d++)
            {
                CameraPlanEntry entry = declared[d];
                string[] values = ValuesOf(entry.Value, aliases);
                for (int v = 0; v < values.Length; v++)
                {
                    string value = values[v];
                    if (value.Length < 4 || LooksLikeIp(value))
                    {
                        continue;
                    }

                    for (int c = 0; c < list.Count; c++)
                    {
                        string candidate = list[c];
                        if (candidate.Length < 4)
                        {
                            continue;
                        }
                        if (candidate.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0
                            || value.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return entry;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>声明值 + 等价标识（去重）。aliases 抛异常时退回"只用声明值"，不影响采集。</summary>
        private static string[] ValuesOf(string declaredValue, Func<string, string[]> aliases)
        {
            if (string.IsNullOrEmpty(declaredValue))
            {
                return new string[0];
            }
            if (aliases == null)
            {
                return new string[] { declaredValue };
            }

            string[] extra;
            try
            {
                extra = aliases(declaredValue);
            }
            catch (Exception)
            {
                return new string[] { declaredValue };
            }

            List<string> values = new List<string>();
            values.Add(declaredValue);
            if (extra != null)
            {
                for (int i = 0; i < extra.Length; i++)
                {
                    if (string.IsNullOrEmpty(extra[i]))
                    {
                        continue;
                    }

                    bool duplicated = false;
                    for (int j = 0; j < values.Count; j++)
                    {
                        if (string.Equals(values[j], extra[i], StringComparison.OrdinalIgnoreCase))
                        {
                            duplicated = true;
                            break;
                        }
                    }
                    if (!duplicated)
                    {
                        values.Add(extra[i]);
                    }
                }
            }
            return values.ToArray();
        }

        /// <summary>cfg 里所有 enable="1" 的相机声明。</summary>
        public static List<CameraPlanEntry> EnabledEntries(CameraPlan plan)
        {
            List<CameraPlanEntry> list = new List<CameraPlanEntry>();
            if (plan == null)
            {
                return list;
            }

            for (int i = 0; i < plan.Cameras.Count; i++)
            {
                CameraPlanEntry entry = plan.Cameras[i];
                if (entry.Enabled && !string.IsNullOrEmpty(entry.Value))
                {
                    list.Add(entry);
                }
            }
            return list;
        }

        /// <summary>声明值是否已被某台相机匹配（按 kind+value 比较）。</summary>
        public static bool Contains(List<CameraPlanEntry> matched, CameraPlanEntry entry)
        {
            if (matched == null || entry == null)
            {
                return false;
            }

            for (int i = 0; i < matched.Count; i++)
            {
                if (string.Equals(matched[i].Kind, entry.Kind, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(matched[i].Value, entry.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>把声明写成 "ip=172.20.10.11" 这种短标签。</summary>
        public static string Describe(CameraPlanEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Value))
            {
                return string.Empty;
            }
            return entry.Kind + "=" + entry.Value;
        }

        private static bool LooksLikeIp(string value)
        {
            if (value.Length < 7 || value.IndexOf('.') < 0)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '.' && (c < '0' || c > '9'))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
