using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DwsEdge.Core.Config
{
    /// <summary>
    /// 相机方位表：相机（IP / 序列号 / 完整 id）→ 安装方位（top/bottom/left/right/front/rear/line/spare）。
    ///
    /// 它有两个用处：
    ///   1. 采集宿主：大华回调里的 CodesInfo.Position 经常为空，用这张表给条码补方位；
    ///   2. 业务平台：设备信息页显示"哪台相机装在哪个面"，并且允许在界面上改。
    ///
    /// 文件是 UTF-8 的 key=value 文本（# 或 ; 开头是注释），默认位置 runtime\config\camera-positions.ini，
    /// 由 tools\make-camera-cfg.ps1 的 pos= 生成。放在 Core 是因为两边都要用同一份读写实现。
    /// </summary>
    public sealed class CameraPositions
    {
        /// <summary>默认相对路径（相对 runtime 目录）。</summary>
        public const string DefaultRelativePath = @"config\camera-positions.ini";

        private static readonly string[] Known = { "top", "bottom", "left", "right", "front", "rear", "line", "spare" };

        private readonly Dictionary<string, string> _map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int Count
        {
            get { return _map.Count; }
        }

        public IEnumerable<KeyValuePair<string, string>> Entries
        {
            get { return _map; }
        }

        public static CameraPositions Load(string path)
        {
            CameraPositions positions = new CameraPositions();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return positions;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch (Exception)
            {
                return positions;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                {
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, eq).Trim();
                string value = Normalize(line.Substring(eq + 1).Trim().Trim('"'));
                if (key.Length > 0 && value.Length > 0)
                {
                    positions._map[key] = value;
                }
            }

            return positions;
        }

        /// <summary>
        /// 按相机 id / 序列号 / IP 解析方位；解析不到返回 null。
        /// 顺序：精确 → 去掉"厂商:"前缀再试 → 包含匹配（清单写完整 id、回调只给序列号这类情况）。
        /// </summary>
        public string Resolve(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
            {
                return null;
            }

            string value;
            if (_map.TryGetValue(cameraId, out value))
            {
                return value;
            }

            int colon = cameraId.LastIndexOf(':');
            if (colon >= 0 && colon < cameraId.Length - 1)
            {
                string serial = cameraId.Substring(colon + 1).Trim();
                if (_map.TryGetValue(serial, out value))
                {
                    return value;
                }
            }

            foreach (KeyValuePair<string, string> pair in _map)
            {
                if (pair.Key.IndexOf(cameraId, StringComparison.OrdinalIgnoreCase) >= 0
                    || cameraId.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return pair.Value;
                }
            }

            return null;
        }

        /// <summary>一次性按多个候选标识（id / 序列号 / IP）找方位。</summary>
        public string ResolveAny(IEnumerable<string> candidates)
        {
            if (candidates == null)
            {
                return null;
            }

            foreach (string candidate in candidates)
            {
                string value = Resolve(candidate);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            return null;
        }

        public string Get(string key)
        {
            string value;
            return (!string.IsNullOrEmpty(key) && _map.TryGetValue(key, out value)) ? value : null;
        }

        public void Set(string key, string position)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            string value = Normalize(position);
            if (value.Length == 0)
            {
                _map.Remove(key);
                return;
            }
            _map[key] = value;
        }

        public bool Remove(string key)
        {
            return !string.IsNullOrEmpty(key) && _map.Remove(key);
        }

        /// <summary>
        /// 写回文件（UTF-8、无 BOM）。原文件存在时先备份成 .bak-&lt;时间戳&gt;，
        /// 和 cfg 的处理方式保持一致：改坏了能找回来。
        /// </summary>
        public string Save(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("保存路径不能为空", "path");
            }

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string backup = null;
            if (File.Exists(path))
            {
                backup = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(path, backup, true);
            }

            List<string> keys = new List<string>(_map.Keys);
            keys.Sort(delegate(string a, string b)
            {
                int byOrder = Order(Resolve(a)).CompareTo(Order(Resolve(b)));
                if (byOrder != 0)
                {
                    return byOrder;
                }
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 相机方位映射（顶/底面等安装位置）");
            sb.AppendLine("# key = 相机 ip / 序列号 / 完整 id；value = 方位（top/bottom/left/right/front/rear/line/spare）");
            sb.AppendLine("# 由业务平台设备信息页或 tools\\make-camera-cfg.ps1 维护；改完需要重启采集宿主才生效。");
            for (int i = 0; i < keys.Count; i++)
            {
                sb.Append(keys[i]).Append('=').Append(_map[keys[i]]).AppendLine();
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return backup;
        }

        /// <summary>方位的中文名，给界面用。</summary>
        public static string Label(string position)
        {
            if (string.IsNullOrEmpty(position))
            {
                return "未设置";
            }

            switch (Normalize(position))
            {
                case "top": return "顶面";
                case "bottom": return "底面";
                case "left": return "左侧";
                case "right": return "右侧";
                case "front": return "前侧";
                case "rear": return "后侧";
                case "line": return "线体";
                case "spare": return "备用";
                default: return position.Trim();
            }
        }

        /// <summary>排序权重：顶 → 底 → 左 → 右 → 前 → 后 → 线体 → 备用。</summary>
        public static int Order(string position)
        {
            string value = Normalize(position);
            for (int i = 0; i < Known.Length; i++)
            {
                if (string.Equals(Known[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return i + 1;
                }
            }
            return Known.Length + 1;
        }

        public static bool IsKnown(string position)
        {
            return Order(position) <= Known.Length;
        }

        public static string Normalize(string position)
        {
            return string.IsNullOrEmpty(position) ? string.Empty : position.Trim().ToLowerInvariant();
        }

        /// <summary>可选项列表（界面下拉用）。</summary>
        public static string[] Options()
        {
            string[] copy = new string[Known.Length];
            Array.Copy(Known, copy, Known.Length);
            return copy;
        }
    }
}
