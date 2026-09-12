using System;

namespace DwsEdge.Core.Abstractions
{
    /// <summary>
    /// 采集 provider —— 整个系统里唯一的厂商相关边界。
    /// 大华 DWS 是一个实现；将来换海康、通用 TCP 读码器、工业相机 + 本地解码，
    /// 只要再实现一个这个接口，核心和业务层都不动。
    /// </summary>
    public interface IAcquisitionProvider : IDisposable
    {
        /// <summary>provider 唯一标识，例如 "dahua-dws"。</summary>
        string ProviderId { get; }

        /// <summary>能力声明，业务层据此决定是否需要自己补齐聚合/去重。</summary>
        ProviderCapabilities Capabilities { get; }

        void Start();
        void Stop();
    }

    /// <summary>
    /// provider 工厂：宿主扫描 providers 目录，用这个创建实例。
    /// </summary>
    public interface IAcquisitionProviderFactory
    {
        string ProviderId { get; }
        IAcquisitionProvider Create(ProviderSettings settings, IEventSink sink);
    }

    /// <summary>
    /// provider 启动/运行失败。宿主统一捕获并把 SDK 返回码翻译成人话。
    /// </summary>
    public sealed class ProviderException : Exception
    {
        public ProviderException(string message)
            : base(message)
        {
        }

        public ProviderException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
