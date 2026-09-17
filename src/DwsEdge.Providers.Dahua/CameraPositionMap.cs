using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DwsEdge.Providers.Dahua
{
    /// <summary>
    /// 相机方位映射（顶/底/左/右/前/后）。
    ///
    /// 大华回调里不一定带方位（CodesInfo.Position 可能为空），这里用一份
    /// "相机 → 方位" 的对照文件兜底。key 可以是相机 IP、序列号或完整 id（厂商:序列号）。
    ///
    /// 文件格式（每行一条，# 或 ; 开头为注释）：
    ///     172.20.10.11=top
    ///     BK27440AAK00036=bottom
    ///     Huaray Technology:BK27440AAK00036=top
    /// </summary>
    internal sealed class CameraPositionMap
    {
        private readonly Dictionary<string, string> _byKey =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int Count
        {
            get { return _byKey.Count; }
        }

        public static CameraPositionMap Load(string path)
        {
            CameraPositionMap map = new CameraPositionMap();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return map;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch (Exception)
            {
                return map;
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
                string value = line.Substring(eq + 1).Trim().Trim('"');
                if (key.Length > 0 && value.Length > 0)
                {
                    map._byKey[key] = value;
                }
            }

            return map;
        }

        /// <summary>按相机 id / 序列号 / IP 解析方位；解析不到返回 null。</summary>
        public string Resolve(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
            {
                return null;
            }

            string value;
            if (_byKey.TryGetValue(cameraId, out value))
            {
                return value;
            }

            // "厂商:序列号" 形式，用序列号再试一次
            int colon = cameraId.LastIndexOf(':');
            if (colon >= 0 && colon < cameraId.Length - 1)
            {
                string serial = cameraId.Substring(colon + 1).Trim();
                if (_byKey.TryGetValue(serial, out value))
                {
                    return value;
                }
            }

            // 配置里写的是完整 id、回调用的是序列号/IP（或反之）：做一次包含匹配
            foreach (KeyValuePair<string, string> pair in _byKey)
            {
                if (pair.Key.IndexOf(cameraId, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return pair.Value;
                }
            }

            return null;
        }
    }
}
