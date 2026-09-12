using System;
using DwsEdge.Core.Abstractions;

namespace DwsEdge.Providers.Simulator
{
    /// <summary>
    /// 模拟 provider 的工厂。放到 runtime\providers\ 后，把 gateway.ini 的
    /// provider 改成 "simulator" 即可在无设备环境下测试。
    /// </summary>
    public sealed class SimulatorProviderFactory : IAcquisitionProviderFactory
    {
        public string ProviderId
        {
            get { return "simulator"; }
        }

        public IAcquisitionProvider Create(ProviderSettings settings, IEventSink sink)
        {
            return new SimulatorProvider(settings, sink);
        }
    }
}
