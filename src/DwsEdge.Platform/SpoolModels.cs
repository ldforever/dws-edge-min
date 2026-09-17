using System.Collections.Generic;

namespace DwsEdge.Platform
{
    /// <summary>采集宿主写入 spool 的事件，字段与 DwsEdge.Host 的 JSONL 一致。</summary>
    internal sealed class SpoolEvent
    {
        public string type { get; set; }
        /// <summary>采集宿主内的自增事件号（同一次运行内唯一，用来识别"同一事件又送了一遍"）。</summary>
        public long eventId { get; set; }
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
        public string declaredKind { get; set; }
        public string declaredValue { get; set; }
        public string position { get; set; }
        public bool? discovered { get; set; }
        public long sessionId { get; set; }
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

        // ---- B1 幂等下发：一个 traceId 只允许下发一次 ----

        /// <summary>下发状态：pending（待下发）/ sent（已下发）/ failed（下发失败待重试）。</summary>
        public string dispatchState { get; set; }

        /// <summary>下发尝试次数（由下游模块在失败重试时累加）。</summary>
        public int dispatchAttempts { get; set; }

        /// <summary>下发成功时间。</summary>
        public string dispatchedAt { get; set; }

        /// <summary>最近一次下发失败原因。</summary>
        public string dispatchError { get; set; }
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

        // ---- A9：设备信息（清单声明 / 方位 / 是否真的被 SDK 发现）----

        /// <summary>cfg 里声明的接入方式：ip / key / id。</summary>
        public string declaredKind { get; set; }

        /// <summary>cfg 里声明的值（IP / 序列号 / 完整 id）。</summary>
        public string declaredValue { get; set; }

        /// <summary>清单标识显示文本，例如 "ip=172.20.10.11"；没匹配到清单时为空。</summary>
        public string declaredLabel { get; set; }

        /// <summary>安装方位代码（top/bottom/left/right/front/rear/line/spare）。</summary>
        public string position { get; set; }

        /// <summary>方位中文名（顶面/底面/…）。</summary>
        public string positionLabel { get; set; }

        /// <summary>方位排序权重，界面按它排。</summary>
        public int positionOrder { get; set; }

        /// <summary>方位刚在界面上改过、采集宿主还没重启（显示的是文件里的新值）。</summary>
        public bool positionPending { get; set; }

        /// <summary>SDK 是否真的发现了这台相机；false = 清单里声明了但设备没接入/没上电。</summary>
        public bool discovered { get; set; } = true;

        /// <summary>最近一次状态来自哪个采集宿主会话（换清单重启后，老会话的相机会被清掉）。</summary>
        public long sessionId { get; set; }

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

    /// <summary>按方位聚合的一行（设备信息页的"六面"概览）。</summary>
    public sealed class CameraFaceSummary
    {
        public string position { get; set; }
        public string label { get; set; }
        public int total { get; set; }
        public int online { get; set; }
        public int offline { get; set; }
        public List<string> cameras { get; set; }
    }

    /// <summary>设备信息页的完整视图：相机清单 + 汇总 + 方位聚合。</summary>
    public sealed class CameraDeviceView
    {
        public int total { get; set; }
        public int online { get; set; }
        public int offline { get; set; }

        /// <summary>SDK 真的发现了的台数。</summary>
        public int discovered { get; set; }

        /// <summary>cfg 里声明了 enable="1"、但 SDK 没发现的台数（离线或没接入）。</summary>
        public int declaredMissing { get; set; }

        /// <summary>还没标方位的台数（方位为空的相机，条码方位会不准）。</summary>
        public int positionMissing { get; set; }

        /// <summary>相机方位映射文件路径与是否存在。</summary>
        public string positionsFile { get; set; }
        public bool positionsFileExists { get; set; }

        /// <summary>方位选项（界面下拉用）。</summary>
        public List<string> positionOptions { get; set; }

        /// <summary>按方位聚合：顶面几台、底面几台……</summary>
        public List<CameraFaceSummary> faces { get; set; }

        public List<CameraRecord> cameras { get; set; }
    }

    /// <summary>保存方位映射的请求体。</summary>
    public sealed class CameraPositionsRequest
    {
        /// <summary>key = 相机 ip / 序列号 / 完整 id；value = 方位代码（空 = 删除这条）。</summary>
        public Dictionary<string, string> positions { get; set; }
    }

    /// <summary>B1：下游回报下发结果的请求体。</summary>
    public sealed class DispatchAckRequest
    {
        /// <summary>幂等键（包裹的 traceId）。</summary>
        public string traceId { get; set; }

        /// <summary>true = 下发成功；false = 失败（记录原因，等重试）。</summary>
        public bool success { get; set; }

        public string error { get; set; }
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

        // ---- B1：回调合并与幂等 ----

        /// <summary>被判定为重复、直接丢弃的事件数（同一包裹同一阶段同样内容又来一遍）。</summary>
        public long duplicateEvents { get; set; }

        /// <summary>发生过合并（收到 ≥2 次有效回调）的包裹数 —— 也就是"先条码后重量体积"合起来的数量。</summary>
        public long mergedParcels { get; set; }

        /// <summary>推送出去的包裹事件数（重复事件不推，所以它应该等于"有效更新次数"）。</summary>
        public long publishedParcels { get; set; }

        // ---- B1：幂等下发（一个 traceId 只下发一次；具体发送在 B4/B6 实现）----

        public int dispatchPending { get; set; }
        public int dispatchSent { get; set; }
        public int dispatchFailed { get; set; }

        public string serverTime { get; set; }
    }
}
