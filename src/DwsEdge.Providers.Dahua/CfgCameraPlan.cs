using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DwsEdge.Providers.Dahua
{
    /// <summary>cfg 里声明的一台相机。</summary>
    internal sealed class CfgCameraEntry
    {
        public string Kind;      // ip / key / id
        public string Value;
        public bool Enabled;

        public override string ToString()
        {
            string kind = string.IsNullOrEmpty(Kind) ? "unknown" : Kind;
            string value = string.IsNullOrEmpty(Value) ? "(空)" : Value;
            return kind + "=" + value + (Enabled ? " [启用]" : " [禁用]");
        }
    }

    /// <summary>
    /// 从 LogisticsBase.cfg 读出"相机计划"（ImageAcq 的 mode/num/randWorkMode + Camera 声明），
    /// 在启动 SDK 之前先做自检，避免现场只能等 SDK 报 3000 才知道配置写错了。
    /// </summary>
    internal sealed class CfgCameraPlan
    {
        public string Mode = "";
        public string Num = "";
        public string RandWorkMode = "";
        public readonly List<CfgCameraEntry> Cameras = new List<CfgCameraEntry>();

        public int NumValue
        {
            get
            {
                int n;
                return int.TryParse(Num, out n) ? n : -1;
            }
        }

        public int EnabledCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < Cameras.Count; i++)
                {
                    if (Cameras[i].Enabled)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        public static CfgCameraPlan Read(string cfgPath)
        {
            CfgCameraPlan plan = new CfgCameraPlan();
            if (!File.Exists(cfgPath))
            {
                return plan;
            }

            string text;
            try
            {
                text = File.ReadAllText(cfgPath, Encoding.GetEncoding("GB2312"));
            }
            catch (Exception)
            {
                text = File.ReadAllText(cfgPath, Encoding.UTF8);
            }

            Match imageAcq = Regex.Match(text, "<ImageAcq\\b([^>]*)/?>", RegexOptions.IgnoreCase);
            if (imageAcq.Success)
            {
                Dictionary<string, string> attrs = ParseAttributes(imageAcq.Groups[1].Value);
                plan.Mode = Get(attrs, "mode");
                plan.Num = Get(attrs, "num");
                plan.RandWorkMode = Get(attrs, "randWorkMode");
            }

            foreach (Match match in Regex.Matches(text, "<Camera\\b([^>]*)/?>", RegexOptions.IgnoreCase))
            {
                Dictionary<string, string> attrs = ParseAttributes(match.Groups[1].Value);
                CfgCameraEntry entry = new CfgCameraEntry();

                if (!string.IsNullOrEmpty(Get(attrs, "ip")))
                {
                    entry.Kind = "ip";
                    entry.Value = Get(attrs, "ip");
                }
                else if (!string.IsNullOrEmpty(Get(attrs, "key")))
                {
                    entry.Kind = "key";
                    entry.Value = Get(attrs, "key");
                }
                else if (!string.IsNullOrEmpty(Get(attrs, "id")))
                {
                    entry.Kind = "id";
                    entry.Value = Get(attrs, "id");
                }
                else
                {
                    entry.Kind = "unknown";
                    entry.Value = "";
                }

                entry.Enabled = Get(attrs, "enable") == "1";
                plan.Cameras.Add(entry);
            }

            return plan;
        }

        /// <summary>硬性错误：不修就不该启动。</summary>
        public List<string> Errors()
        {
            List<string> problems = new List<string>();
            int num = NumValue;

            if (num < 0)
            {
                problems.Add("ImageAcq 的 num 缺失或不是数字（当前值：" + Num + "）");
            }
            else if (num < 1)
            {
                problems.Add("num=" + num + " 无效：至少要有 1 台相机");
            }

            if (Mode == "2")
            {
                if (EnabledCount == 0)
                {
                    problems.Add("mode=2 是按 IP/Key/Id 指定相机，但没有任何一台 <Camera ... enable=\"1\">");
                }
                if (num >= 0 && num != EnabledCount)
                {
                    problems.Add("num=" + num + " 与 enable=\"1\" 的相机数量 " + EnabledCount + " 不一致（mode=2 时必须相等）");
                }
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Cameras.Count; i++)
            {
                CfgCameraEntry entry = Cameras[i];
                if (!entry.Enabled)
                {
                    continue;
                }
                if (entry.Kind == "unknown" || string.IsNullOrEmpty(entry.Value))
                {
                    problems.Add("第 " + (i + 1) + " 条相机声明启用了，但 ip/key/id 是空的");
                    continue;
                }
                if (!seen.Add(entry.Kind + ":" + entry.Value))
                {
                    problems.Add("相机声明重复：" + entry);
                }
            }

            return problems;
        }

        /// <summary>提醒项：不阻塞启动，但要提示现场确认。</summary>
        public List<string> Warnings()
        {
            List<string> warnings = new List<string>();

            // 不写死相机数量：这里只按 SDK 文档给提醒，不阻断启动
            if (NumValue > 20)
            {
                warnings.Add("num=" + NumValue + " 超过 SDK 文档标注的上限 20 台，请确认当前 SDK 版本确实支持这么多相机");
            }

            if (Mode == "1" && RandWorkMode == "0" && NumValue > 0)
            {
                warnings.Add("mode=1 且 randWorkMode=0 时，现场实际发现的相机数量必须等于 num=" + NumValue
                    + "，否则 SDK 会启动失败；建议改成 randWorkMode=1（任一台工作即可启动）");
            }
            if (Mode != "1" && Mode != "2" && Mode != "3" && Mode != "4")
            {
                warnings.Add("ImageAcq 的 mode=" + Mode + " 不是常见取值（1=自动发现 2=按IP/Key 3=全部智能机 4=全部工业机）");
            }
            return warnings;
        }

        public string Describe()
        {
            return "mode=" + Mode + " num=" + Num + " randWorkMode=" + RandWorkMode
                 + " 启用相机=" + EnabledCount + "/" + Cameras.Count;
        }

        private static Dictionary<string, string> ParseAttributes(string text)
        {
            Dictionary<string, string> attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 标准写法：key="value"
            foreach (Match match in Regex.Matches(text, "([A-Za-z_][\\w\\-\\.]*)\\s*=\\s*\"([^\"]*)\""))
            {
                attrs[match.Groups[1].Value] = match.Groups[2].Value;
            }

            // 兼容手改时漏掉引号的写法：key=value
            foreach (Match match in Regex.Matches(text, "([A-Za-z_][\\w\\-\\.]*)\\s*=\\s*([^\\s\"'>]+)"))
            {
                string name = match.Groups[1].Value;
                if (!attrs.ContainsKey(name))
                {
                    attrs[name] = match.Groups[2].Value;
                }
            }

            return attrs;
        }

        private static string Get(Dictionary<string, string> attrs, string key)
        {
            string value;
            return attrs.TryGetValue(key, out value) ? value : null;
        }
    }
}
