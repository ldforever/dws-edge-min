using System.Collections.Generic;

namespace DwsEdge.Platform
{
    /// <summary>P0：相机连通性预检请求 —— 传要检查的 IP 列表（只对清单里 ip= 的行有意义）。</summary>
    public sealed class CameraProbeRequest
    {
        public List<string> ips { get; set; }
    }

    /// <summary>单个 IP 的预检结果。</summary>
    public sealed class CameraProbeItem
    {
        public string ip { get; set; }

        /// <summary>ICMP ping 通不通。</summary>
        public bool ping { get; set; }

        /// <summary>ping 往返毫秒（不通为 0）。</summary>
        public long pingMs { get; set; }

        /// <summary>采集宿主上报的 SDK 发现列表里有没有这台相机。</summary>
        public bool discovered { get; set; }

        /// <summary>SDK 给的设备标识（厂商:序列号），没发现时为空。</summary>
        public string deviceId { get; set; }

        /// <summary>一句话结论，界面上直接显示。</summary>
        public string message { get; set; }
    }

    public sealed class CameraProbeResult
    {
        public List<CameraProbeItem> results { get; set; }
        public string note { get; set; }
    }
}
