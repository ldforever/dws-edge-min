using System;
using System.Collections.Generic;

namespace DwsEdge.Core.Model
{
    /// <summary>
    /// 包裹结果的阶段。大华 DWS 对一个包裹会回调两次，这里被归一化成两个阶段。
    /// </summary>
    public enum ParcelStage
    {
        /// <summary>只有条码（大华 LogisticsCodeEventArgs.OutputResult == 0）。</summary>
        Detected = 0,

        /// <summary>条码 + 重量 + 体积（OutputResult == 1）。</summary>
        Enriched = 1
    }

    /// <summary>
    /// 厂商无关的包裹事件。
    /// 注意：这是"已经归并好的包裹级结果"。如果将来换成只有单相机读码的相机，
    /// 由平台侧聚合器把多条 CameraReadEvent 归并成这个模型，模型本身不变。
    /// </summary>
    public sealed class ParcelEvent
    {
        public int SchemaVersion = 1;

        /// <summary>宿主内自增序号，便于排序和排查。</summary>
        public long EventId;

        /// <summary>来自哪个 provider（如 dahua-dws）。</summary>
        public string ProviderId;

        /// <summary>汇总相机标识（大华回调里的 CameraID）。</summary>
        public string DeviceId;

        public ParcelStage Stage;

        /// <summary>SDK 上报的包裹时间戳（Unix 毫秒）。</summary>
        public long CapturedAtMs;

        /// <summary>本地接收时间戳（Unix 毫秒），用于对比链路延迟。</summary>
        public long ReceivedAtMs;

        public List<CodeItem> Codes = new List<CodeItem>();
        public List<ImageRef> Images = new List<ImageRef>();

        /// <summary>重量（克）；-1 表示本条事件没有重量。</summary>
        public int WeightGrams = -1;

        /// <summary>体积（毫米 / 立方毫米）；0 表示无。</summary>
        public double LengthMm;
        public double WidthMm;
        public double HeightMm;
        public double VolumeMm3;

        /// <summary>
        /// 幂等键：provider + 相机 + 时间戳 + 条码。
        /// Detected 与 Enriched 两次回调共享同一个 TraceId，业务层靠它合并。
        /// </summary>
        public string TraceId;
    }

    /// <summary>
    /// 单台相机的读码事件（相机级）。
    /// 大华的"所有相机扫码信息回调"映射到这里；将来通用读码器 provider 也发这个。
    /// </summary>
    public sealed class CameraReadEvent
    {
        public int SchemaVersion = 1;
        public string ProviderId;
        public string DeviceId;    // 相机 key
        public string CameraIp;
        public long CapturedAtMs;
        public long ReceivedAtMs;
        public List<CodeItem> Codes = new List<CodeItem>();
        public List<ImageRef> Images = new List<ImageRef>();
    }

    /// <summary>
    /// 相机上下线事件。
    /// </summary>
    public sealed class CameraStatusEvent
    {
        public string ProviderId;
        public string DeviceId;
        public string UserId;
        public bool Online;
        public long AtMs;
    }
}
