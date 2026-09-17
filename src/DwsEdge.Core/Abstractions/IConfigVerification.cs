using System;
using System.Collections.Generic;

namespace DwsEdge.Core.Abstractions
{
    /// <summary>配置校验结果：一键应用配置后用它判断"参数是否真的生效"。</summary>
    public sealed class ConfigVerificationReport
    {
        public bool Success;
        public List<string> Details = new List<string>();
        public List<string> Problems = new List<string>();
    }

    /// <summary>
    /// 可选接口：provider 校验自己的配置文件是否被正确读取、参数是否生效。
    /// 大华 provider 用 CameraPlan + SDK 上报的相机数量实现；模拟器只做配置文件结构校验。
    /// </summary>
    public interface IConfigVerification
    {
        ConfigVerificationReport VerifyConfig();
    }
}
