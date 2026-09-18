using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DwsEdge.Core.Config
{
    /// <summary>
    /// 相机 IP ↔ SDK Key（厂商:序列号）对照表。
    ///
    /// 为什么需要它：
    ///   大华 SDK 的状态接口（GetWorkCameraInfo）只回报 Key（厂商:序列号）、型号、固件，
    ///   **不回报 IP**；而现场配相机清单时按 IP 写最省事（网口一插、IP 一看就知道是哪台）。
    ///   两边的"身份"对不上，平台就会把同一台相机显示成两条：
    ///     一条是清单里的 ip=100.100.100.11（永远离线、报 declared-missing）
    ///     一条是 SDK 发现的 Huaray Technology:BK27440AAK00036（在线但"未在清单中"）。
    ///
    /// 这张表把两者关联起来，于是：**清单继续写 IP，平台也能认出是哪台相机**。
    ///
    /// 数据从哪儿来（两处都用，互为补充）：
    ///   1) SDK 日志 Log\default.log / camera.log 里的 IP-Key 配对 —— 启动后立刻可用，不用等包裹；
    ///   2) 扫码回调 SingleCameraCodeInfo.CameraIP + Key —— SDK 官方字段，权威且实时。
    ///
    /// 并存一份到 config\camera-identity.ini（ip=key 一行一条），人可读、可手改、可跟交付包走。
    /// </summary>
    public sealed class CameraIdentityMap
    {
        /// <summary>默认相对路径（相对 runtime 目录）。</summary>
        public const string DefaultRelativePath = @"config\camera-identity.ini";

        private readonly object _sync = new object();
        private readonly Dictionary<string, string> _keyByIp =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _ipByKey =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _dirty;

        // ---- SDK 日志里的三种写法（按实测日志整理，任一命中即可）----
        //   Camera[IP-100.100.100.11][Key-Huaray Technology:BK27440AAK00036]
        //   IP-Key:[100.100.100.11-Huaray Technology:BK27440AAK00036]
        //   Camera[100.100.100.11|Huaray Technology:BK27440AAK00036]
        private static readonly Regex[] LogPatterns = new Regex[]
        {
            new Regex(@"IP-(\d{1,3}(?:\.\d{1,3}){3})\]\[Key-([^\]\r\n]+)", RegexOptions.Compiled),
            new Regex(@"IP-Key:\[(\d{1,3}(?:\.\d{1,3}){3})-([^\]\r\n]+)\]", RegexOptions.Compiled),
            new Regex(@"Camera\[(\d{1,3}(?:\.\d{1,3}){3})\|([^\]\r\n]+)\]", RegexOptions.Compiled)
        };

        public int Count
        {
            get { lock (_sync) { return _keyByIp.Count; } }
        }

        public bool IsDirty
        {
            get { lock (_sync) { return _dirty; } }
        }

        public IEnumerable<KeyValuePair<string, string>> Entries
        {
            get
            {
                List<KeyValuePair<string, string>> list;
                lock (_sync)
                {
                    list = new List<KeyValuePair<string, string>>(_keyByIp);
                }
                return list;
            }
        }

        /// <summary>
        /// 记一条 IP ↔ Key。任一侧为空、或 IP 不像 IPv4 时忽略。
        /// 返回 true 表示这条关联是新的或变了（调用方据此决定是否落盘）。
        /// </summary>
        public bool Record(string ip, string key)
        {
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(key))
            {
                return false;
            }

            ip = ip.Trim();
            key = key.Trim();
            if (ip.Length == 0 || key.Length == 0 || !LooksLikeIp(ip))
            {
                return false;
            }

            lock (_sync)
            {
                string oldKey;
                string oldIp;
                bool changed = false;

                if (!_keyByIp.TryGetValue(ip, out oldKey) || !string.Equals(oldKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    _keyByIp[ip] = key;
                    changed = true;
                }

                if (!_ipByKey.TryGetValue(key, out oldIp) || !string.Equals(oldIp, ip, StringComparison.OrdinalIgnoreCase))
                {
                    _ipByKey[key] = ip;
                    changed = true;
                }

                if (changed)
                {
                    _dirty = true;
                }
                return changed;
            }
        }

        /// <summary>IP → SDK Key；没有对应返回 null。</summary>
        public string KeyOf(string ip)
        {
            if (string.IsNullOrEmpty(ip))
            {
                return null;
            }

            lock (_sync)
            {
                string key;
                return _keyByIp.TryGetValue(ip.Trim(), out key) ? key : null;
            }
        }

        /// <summary>SDK Key → IP；没有对应返回 null。</summary>
        public string IpOf(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            lock (_sync)
            {
                string ip;
                return _ipByKey.TryGetValue(key.Trim(), out ip) ? ip : null;
            }
        }

        /// <summary>
        /// 一个声明值（可能是 IP、也可能是 Key/序列号）的全部等价标识，用于和各路候选标识比对。
        /// 例：声明 "100.100.100.11" → 返回 {"100.100.100.11", "Huaray Technology:BK27440AAK00036"}；
        /// 声明就是 Key 时 → 返回 {Key, 它对应的 IP}。
        /// </summary>
        public string[] AliasesOf(string declaredValue)
        {
            if (string.IsNullOrEmpty(declaredValue))
            {
                return new string[0];
            }

            declaredValue = declaredValue.Trim();
            List<string> list = new List<string>();
            list.Add(declaredValue);

            string key = KeyOf(declaredValue);
            if (!string.IsNullOrEmpty(key))
            {
                list.Add(key);
            }

            string ip = IpOf(declaredValue);
            if (!string.IsNullOrEmpty(ip))
            {
                list.Add(ip);
            }

            return list.ToArray();
        }

        /// <summary>读 config\camera-identity.ini（ip=key，一行一条；# 为注释）。文件不存在不算错。</summary>
        public int Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return 0;
            }

            int added = 0;
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string text = lines[i].Trim();
                    if (text.Length == 0 || text.StartsWith("#") || text.StartsWith(";"))
                    {
                        continue;
                    }

                    int eq = text.IndexOf('=');
                    if (eq <= 0 || eq >= text.Length - 1)
                    {
                        continue;
                    }

                    string ip = text.Substring(0, eq).Trim();
                    string key = text.Substring(eq + 1).Trim();
                    if (Record(ip, key))
                    {
                        added++;
                    }
                }
            }
            catch (Exception)
            {
                // 读对照表失败不影响采集：没有它只是退回"按 Key 显示"
                return added;
            }

            lock (_sync)
            {
                // 刚读进来的内容就是磁盘上的内容，不算脏
                _dirty = false;
            }
            return added;
        }

        /// <summary>内容有变化时写回 ip=key 对照表；返回 true 表示写了。</summary>
        public bool Save(string path)
        {
            if (string.IsNullOrEmpty(path) || !IsDirty)
            {
                return false;
            }

            List<KeyValuePair<string, string>> list;
            lock (_sync)
            {
                list = new List<KeyValuePair<string, string>>(_keyByIp);
            }
            list.Sort(delegate (KeyValuePair<string, string> a, KeyValuePair<string, string> b)
            {
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 相机 IP ↔ SDK Key（厂商:序列号）对照表");
                sb.AppendLine("# 由采集宿主自动维护：从 SDK 日志与扫码回调里采集，用来把\"清单里按 IP 写的相机\"");
                sb.AppendLine("# 和\"SDK 按 厂商:序列号 上报的设备\"关联起来，避免界面上出现一条幽灵离线记录。");
                sb.AppendLine("# 一行一条：ip=key。可以手改；删掉整份文件也没关系，下次启动会重建。");
                sb.AppendLine("# 生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                for (int i = 0; i < list.Count; i++)
                {
                    sb.Append(list[i].Key).Append('=').AppendLine(list[i].Value);
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

                lock (_sync)
                {
                    _dirty = false;
                }
                return true;
            }
            catch (Exception)
            {
                // 写不进去（磁盘只读/占用）也不影响采集，下次再写
                return false;
            }
        }

        /// <summary>
        /// 从 SDK 日志里提取 IP↔Key 配对，返回新增条数。
        /// 同一文件里后面的记录覆盖前面的（相机换 IP 后会以最新一次为准）。
        /// </summary>
        public int ParseLog(string logPath)
        {
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
            {
                return 0;
            }

            string text;
            try
            {
                // SDK 日志可能正被 SDK 自己占用，用共享读打开
                using (FileStream fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                {
                    text = sr.ReadToEnd();
                }
            }
            catch (Exception)
            {
                return 0;
            }

            int added = 0;
            for (int p = 0; p < LogPatterns.Length; p++)
            {
                MatchCollection matches;
                try
                {
                    matches = LogPatterns[p].Matches(text);
                }
                catch (Exception)
                {
                    continue;
                }

                for (int i = 0; i < matches.Count; i++)
                {
                    string ip = matches[i].Groups[1].Value;
                    string key = matches[i].Groups[2].Value;
                    if (Record(ip, key))
                    {
                        added++;
                    }
                }
            }
            return added;
        }

        /// <summary>看起来像 IPv4 吗（只做格式判断，不做范围校验）。</summary>
        public static bool LooksLikeIp(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 7 || value.IndexOf('.') < 0)
            {
                return false;
            }

            int dots = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '.')
                {
                    dots++;
                    continue;
                }
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }
            return dots == 3;
        }
    }
}
