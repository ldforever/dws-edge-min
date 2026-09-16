using System.Collections.Generic;

namespace DwsEdge.Platform
{
    /// <summary>采集宿主写入 spool 的事件，字段与 DwsEdge.Host 的 JSONL 一致。</summary>
    internal sealed class SpoolEvent
    {
        public string type { get; set; }
        public string providerId { get; set; }
        public string deviceId { get; set; }
        public string userId { get; set; }
        public string stage { get; set; }
        public long capturedAtMs { get; set; }
        public long receivedAtMs { get; set; }
        public long atMs { get; set; }
        public string traceId { get; set; }
        public int weightGrams { get; set; }
        public double lengthMm { get; set; }
        public double widthMm { get; set; }
        public double heightMm { get; set; }
        public double volumeMm3 { get; set; }
        public bool? online { get; set; }
        public List<SpoolCode> codes { get; set; }
        public List<SpoolImage> images { get; set; }
    }

    internal sealed class SpoolCode
    {
        public string value { get; set; }
        public string kind { get; set; }
        public string position { get; set; }
    }

    internal sealed class SpoolImage
    {
        public string kind { get; set; }
        public string deviceId { get; set; }
        public string format { get; set; }
        public int width { get; set; }
        public int height { get; set; }
        public int bytes { get; set; }
        public string path { get; set; }
    }

    /// <summary>平台对外输出的包裹记录：一个包裹一条，两次回调已合并。</summary>
    public sealed class ParcelRecord
    {
        public string traceId { get; set; }
        public string deviceId { get; set; }
        public string stage { get; set; }
        public long capturedAtMs { get; set; }
        public string time { get; set; }
        public List<string> codes { get; set; }
        public int codeCount { get; set; }
        public int weightGrams { get; set; }
        public double volumeMm3 { get; set; }
        public int imageCount { get; set; }
        public string firstImagePath { get; set; }
        public int updates { get; set; }
    }

    /// <summary>相机状态。</summary>
    public sealed class CameraRecord
    {
        public string deviceId { get; set; }
        public bool online { get; set; }
        public long atMs { get; set; }
        public string lastChangeTime { get; set; }
        public long statusChanges { get; set; }
        public long codeCount { get; set; }
    }

    /// <summary>平台统计。</summary>
    public sealed class PlatformStats
    {
        public long events { get; set; }
        public long parcels { get; set; }
        public long noread { get; set; }
        public long images { get; set; }
        public double readRate { get; set; }
        public int camerasTotal { get; set; }
        public int camerasOnline { get; set; }
        public long parseErrors { get; set; }
        public string serverTime { get; set; }
    }
}
