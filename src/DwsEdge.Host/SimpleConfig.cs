using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DwsEdge.Host
{
    /// <summary>
    /// 极简 INI 解析（段 + key=value），不引入任何第三方库。
    ///
    ///   [runtime]
    ///   provider=dahua-dws
    ///   [dahua-dws]
    ///   cfgPath=Cfg\LogisticsBase.cfg
    /// </summary>
    internal sealed class SimpleConfig
    {
        private readonly Dictionary<string, Dictionary<string, string>> _sections;

        private SimpleConfig(Dictionary<string, Dictionary<string, string>> sections)
        {
            _sections = sections;
        }

        public static SimpleConfig Load(string path)
        {
            Dictionary<string, Dictionary<string, string>> sections =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            sections[string.Empty] = current;

            if (!File.Exists(path))
            {
                return new SimpleConfig(sections);
            }

            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                {
                    continue;
                }

                if (line[0] == '[')
                {
                    int end = line.IndexOf(']');
                    if (end > 0)
                    {
                        string name = line.Substring(1, end - 1).Trim();
                        if (!sections.TryGetValue(name, out current))
                        {
                            current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            sections[name] = current;
                        }
                    }
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key.Length > 0)
                {
                    current[key] = value;
                }
            }

            return new SimpleConfig(sections);
        }

        public string Get(string section, string key, string defaultValue)
        {
            Dictionary<string, string> s;
            if (_sections.TryGetValue(section ?? string.Empty, out s))
            {
                string v;
                if (s.TryGetValue(key, out v))
                {
                    return v;
                }
            }
            return defaultValue;
        }

        /// <summary>取出某个配置段，交给 provider 自己解释。</summary>
        public Dictionary<string, string> Section(string name)
        {
            Dictionary<string, string> s;
            if (name != null && _sections.TryGetValue(name, out s))
            {
                return new Dictionary<string, string>(s, StringComparer.OrdinalIgnoreCase);
            }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
