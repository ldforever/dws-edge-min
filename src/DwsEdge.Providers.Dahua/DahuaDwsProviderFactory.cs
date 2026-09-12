using System;
using DwsEdge.Core.Abstractions;

namespace DwsEdge.Providers.Dahua
{
    /// <summary>
    /// 宿主扫描 providers\*.dll 时通过这个工厂创建 provider。
    /// 新增一家相机 = 新增一个类似的文件 + 一个 factory，宿主不用改。
    /// </summary>
    public sealed class DahuaDwsProviderFactory : IAcquisitionProviderFactory
    {
        public string ProviderId
        {
            get { return "dahua-dws"; }
        }

        public IAcquisitionProvider Create(ProviderSettings settings, IEventSink sink)
        {
            return new DahuaDwsProvider(settings, sink);
        }
    }
}
