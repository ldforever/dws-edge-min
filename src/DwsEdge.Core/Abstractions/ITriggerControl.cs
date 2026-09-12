using System;

namespace DwsEdge.Core.Abstractions
{
    /// <summary>
    /// 可选命令接口：不是每个 provider 都支持。
    /// 支持软触发的 provider（大华 DWS 的 CameraSoftTrigger、测试用模拟器）实现它，
    /// 宿主通过 <c>provider as ITriggerControl</c> 判断能力，从而不必认识任何厂商类型。
    /// </summary>
    public interface ITriggerControl
    {
        /// <summary>
        /// 触发一次相机拍照/拉流。
        /// 对应大华 SDK 的 <c>LogisticsWrapper.CameraSoftTrigger()</c>（原生 vslbSoftTrigger）。
        /// </summary>
        /// <returns>0 = 成功；非 0 = 失败（具体含义见 provider 日志）。</returns>
        int SoftTrigger();

        /// <summary>
        /// 人工补码（对应大华 SDK 的 ComplementCode）。
        /// </summary>
        /// <param name="code">要补录的条码。</param>
        /// <param name="timeMs">该条码对应的包裹时间戳（Unix 毫秒，UTC）；传 0 表示用当前时间。</param>
        /// <returns>0 = 成功；非 0 = 失败。</returns>
        int ComplementCode(string code, long timeMs);
    }
}
