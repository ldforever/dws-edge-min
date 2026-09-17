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
        public bool? isSnapshot { get; set; }
        public string model { get; set; }
        public string serialNumber { get; set; }
        public string vendor { get; set; }
        public string firmware { get; set; }
        public int offlineCount { get; set; }
        public int reconnectCount { get; set; }
        public long lastOfflineAtMs { get; set; }
        public long lastOfflineDurationMs { get; set; }
        public long firstSeenAtMs { get; set; }
        /// <summary>该 provider 是否分阶段上报包裹结果（由事件带的 stagedResult 决定）。</summary>
        public bool? stagedResult { get; set; }
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
    public sealed class CodeDetail
    {
        public string value { get; set; }
        /// <summary>1d / 2d / unknown。</summary>
        public string kind { get; set; }
        /// <summary>方位（top/bottom/left/right/front/rear 或厂商给的值）。</summary>
        public string position { get; set; }
    }

    public sealed class ParcelRecord
    {
        public string traceId { get; set; }
        public string deviceId { get; set; }
        public string stage { get; set; }
        public long capturedAtMs { get; set; }
        public string time { get; set; }
        /// <summary>条码值列表（兼容旧的接口消费方）。</summary>
        public List<string> codes { get; set; }
        /// <summary>完整条码信息：值 + 类型 + 方位。</summary>
        public List<CodeDetail> codeDetails { get; set; }
        public int codeCount { get; set; }
        public int weightGrams { get; set; }
        public double volumeMm3 { get; set; }
        public double lengthMm { get; set; }
        public double widthMm { get; set; }
        public double heightMm { get; set; }
        public int imageCount { get; set; }
        public string firstImagePath { get; set; }
        public int updates { get; set; }

        /// <summary>provider 是否分阶段上报（true 时"看到 enriched 才算完整"）。</summary>
        public bool staged { get; set; }

        /// <summary>该包裹记录是否已完整（非分阶段 provider 首次事件即完整）。</summary>
        public bool complete { get; set; }
    }

    /// <summary>相机状态。</summary>
    public sealed class CameraRecord
    {
        public string deviceId { get; set; }
        public string userId { get; set; }
        public bool online { get; set; }
        public long atMs { get; set; }
        public string lastChangeTime { get; set; }
        public long statusChanges { get; set; }
        public long codeCount { get; set; }
        public string model { get; set; }
        public string serialNumber { get; set; }
        public string vendor { get; set; }
        public string firmware { get; set; }
        /// <summary>最近一次状态来自启动快照（true）还是上下线增量（false）。</summary>
        public bool fromSnapshot { get; set; }

        /// <summary>累计掉线次数 / 恢复次数（取采集宿主上报的最大值，宿主重启不会让计数回落）。</summary>
        public long offlineCount { get; set; }
        public long reconnectCount { get; set; }

        /// <summary>最近一次掉线的时间与离线时长。</summary>
        public long lastOfflineAtMs { get; set; }
        public long lastOfflineDurationMs { get; set; }
        public string lastOfflineTime { get; set; }
        public string lastOfflineDurationText { get; set; }

        /// <summary>该相机最近一次出码的时间（出码包裹数见 codeCount）。</summary>
        public string lastCodeTime { get; set; }
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

        /// <summary>图片目录扫描结果（后台定期刷新）。</summary>
        public long imageFileCount { get; set; }
        public long imageDiskBytes { get; set; }

        /// <summary>图片所在磁盘的容量与占用。</summary>
        public long diskTotalBytes { get; set; }
        public long diskFreeBytes { get; set; }
        public int diskUsedPercent { get; set; }

        /// <summary>尚未补全的包裹数（分阶段 provider 只收到条码、还没收到重量体积）。</summary>
        public int pendingParcels { get; set; }

        /// <summary>缺少追踪号的事件数（用了兜底键，可追溯性较弱）。</summary>
        public long missingTraceId { get; set; }

        /// <summary>疑似追踪号冲突次数（同一追踪号下条码集合完全不相交）。</summary>
        public long traceIdConflicts { get; set; }

        public string serverTime { get; set; }
    }
}
