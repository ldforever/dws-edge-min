using System;

namespace DwsEdge.Core.Abstractions
{
    /// <summary>
    /// provider 能力声明。业务层按能力降级，而不是假设每家相机都一样。
    /// 例如大华 DWS 自己会做包裹汇总；将来某个通用读码器只会上报相机级读码，
    /// 那时由平台侧聚合器接手（Capabilities 里不带 ParcelAggregation）。
    /// </summary>
    [Flags]
    public enum ProviderCapabilities
    {
        None = 0,

        /// <summary>provider 自己完成多相机包裹汇总。</summary>
        ParcelAggregation = 1 << 0,

        /// <summary>provider 自己完成跨相机同码去重。</summary>
        CrossCameraDedup = 1 << 1,

        /// <summary>能提供面单抠图。</summary>
        WaybillCrop = 1 << 2,

        /// <summary>能提供每台相机各自的图。</summary>
        PerCameraImage = 1 << 3,

        Weight = 1 << 4,
        Volume = 1 << 5,

        /// <summary>支持软件触发一次拍照（ITriggerControl.SoftTrigger）。</summary>
        SoftTrigger = 1 << 6,

        /// <summary>支持人工补码（ITriggerControl.ComplementCode）。</summary>
        ComplementCode = 1 << 7,

        /// <summary>能生成/写入厂商私有配置。</summary>
        ConfigWrite = 1 << 8,

        /// <summary>需要加密狗 / 授权，运维上要单独关注。</summary>
        RequiresDongle = 1 << 9,

        /// <summary>
        /// 包裹结果分阶段上报：同一个包裹会来多次（例如大华先回条码、再回条码+重量体积）。
        /// 业务层据此判断"这条包裹记录是否已经完整"（需要看到 Enriched 才算完整）。
        /// </summary>
        StagedParcelResult = 1 << 10
    }
}
