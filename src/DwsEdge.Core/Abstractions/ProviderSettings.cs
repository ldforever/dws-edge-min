using System;
using System.Collections.Generic;
using System.IO;

namespace DwsEdge.Core.Abstractions
{
    /// <summary>
    /// provider 的私有配置（来自 gateway.ini 中该 provider 的配置段）。
    /// 核心层不认识"大华"，所有厂商参数都由 provider 自己解释。
    /// </summary>
    public sealed class ProviderSettings
    {
        private readonly Dictionary<string, string> _values;

        /// <summary>运行时目录（exe 所在目录，SDK 的 Cfg/、DLL 都在这里）。</summary>
        public string RuntimeDirectory { get; private set; }

        public ProviderSettings(string runtimeDirectory, Dictionary<string, string> values)
        {
            RuntimeDirectory = runtimeDirectory;
            _values = values != null
                ? new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string Get(string key, string defaultValue)
        {
            string v;
            if (_values.TryGetValue(key, out v) && !string.IsNullOrEmpty(v))
            {
                return v;
            }
            return defaultValue;
        }

        public bool GetBool(string key, bool defaultValue)
        {
            string v = Get(key, null);
            if (string.IsNullOrEmpty(v))
            {
                return defaultValue;
            }
            return v.Equals("1", StringComparison.OrdinalIgnoreCase)
                || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        public int GetInt(string key, int defaultValue)
        {
            int n;
            if (int.TryParse(Get(key, null), out n))
            {
                return n;
            }
            return defaultValue;
        }

        /// <summary>把相对路径解析成运行时目录下的绝对路径。</summary>
        public string ResolvePath(string relativeOrAbsolute)
        {
            if (string.IsNullOrEmpty(relativeOrAbsolute))
            {
                return RuntimeDirectory;
            }
            if (Path.IsPathRooted(relativeOrAbsolute))
            {
                return relativeOrAbsolute;
            }
            return Path.GetFullPath(Path.Combine(RuntimeDirectory, relativeOrAbsolute));
        }
    }
}
