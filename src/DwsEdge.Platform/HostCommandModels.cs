namespace DwsEdge.Platform
{
    /// <summary>
    /// A4：给运行中的采集宿主发命令的请求体。
    /// 走的是宿主常驻命令通道（命名管道），不需要停宿主、也不再抢相机。
    /// </summary>
    public sealed class HostCommandRequest
    {
        /// <summary>soft-trigger（软触发一次）/ recode（人工补码）/ status（查能力与触发模式）。</summary>
        public string command { get; set; }

        /// <summary>补码用：要写进去的条码。</summary>
        public string code { get; set; }

        /// <summary>补码用：该条码的时间戳（Unix 毫秒），0 表示用当前时间。</summary>
        public long timeMs { get; set; }

        /// <summary>跳过"必须软触发模式"的前置校验（排查用）。</summary>
        public bool force { get; set; }
    }
}
