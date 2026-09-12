using System;
using System.Globalization;
using System.Threading;
using DwsEdge.Core.Abstractions;

namespace DwsEdge.Host
{
    /// <summary>测试环境的软触发开关参数（来自 gateway.ini 的 [test] 段，命令行可覆盖）。</summary>
    internal sealed class TestOptions
    {
        /// <summary>是否启用软触发测试开关。关闭时下面的任何触发都不会发生。</summary>
        public bool EnableSoftTrigger;

        /// <summary>启动后自动触发一次（冒烟用）。</summary>
        public bool TriggerOnStart;

        /// <summary>启动自动触发的延迟（毫秒）。</summary>
        public int TriggerDelayMs = 1500;

        /// <summary>每隔多少毫秒自动触发一次（模拟连续过包）；0 = 关闭。</summary>
        public int TriggerIntervalMs;
    }

    /// <summary>
    /// 测试环境下的软触发驱动：
    ///   - 启动触发：`triggerOnStart=true` 或命令行 `--trigger-once`；
    ///   - 定时触发：`triggerIntervalMs=3000` 或命令行 `--trigger-interval 3000`（模拟连续过包）；
    ///   - 交互触发：运行时按 Enter（或输入 t）触发一次，输入 `c &lt;条码&gt;` 补码，输入 q 退出。
    ///
    /// 它只依赖 ITriggerControl 这个可选接口，所以对真实相机（dahua-dws）和模拟相机（simulator）完全通用。
    /// </summary>
    internal sealed class TestConsole : IDisposable
    {
        private readonly IEventSink _sink;
        private readonly ITriggerControl _control;
        private readonly TestOptions _options;
        private readonly ManualResetEventSlim _stopSignal;
        private Thread _inputThread;
        private Thread _intervalThread;

        public TestConsole(IEventSink sink, ITriggerControl control, TestOptions options, ManualResetEventSlim stopSignal)
        {
            _sink = sink;
            _control = control;
            _options = options;
            _stopSignal = stopSignal;
        }

        public void Start()
        {
            if (!_options.EnableSoftTrigger)
            {
                return;
            }

            if (_options.TriggerOnStart)
            {
                ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    if (!_stopSignal.Wait(_options.TriggerDelayMs))
                    {
                        TriggerOnce("启动自动触发");
                    }
                });
            }

            if (_options.TriggerIntervalMs > 0)
            {
                _intervalThread = new Thread(IntervalLoop);
                _intervalThread.IsBackground = true;
                _intervalThread.Name = "test-trigger-interval";
                _intervalThread.Start();
                _sink.Log(LogLevel.Info, "[test] 已开启定时软触发：每 " + _options.TriggerIntervalMs + " ms 触发一次（模拟连续过包）");
            }

            if (!Console.IsInputRedirected)
            {
                _inputThread = new Thread(InputLoop);
                _inputThread.IsBackground = true;
                _inputThread.Name = "test-console-input";
                _inputThread.Start();
                Console.WriteLine("[test] 软触发已开启：按 Enter（或输入 t）触发一次，输入 c <条码> 补码，输入 q 退出。");
            }
        }

        public int TriggerOnce(string reason)
        {
            int ret = _control.SoftTrigger();
            _sink.Log(ret == 0 ? LogLevel.Info : LogLevel.Warn,
                "[test] 软触发（" + reason + "）返回 " + ret);
            return ret;
        }

        public void Dispose()
        {
            // 两个线程都是后台线程，主流程退出时不需要强等
        }

        private void IntervalLoop()
        {
            while (!_stopSignal.Wait(_options.TriggerIntervalMs))
            {
                TriggerOnce("定时 " + _options.TriggerIntervalMs.ToString(CultureInfo.InvariantCulture) + "ms");
            }
        }

        private void InputLoop()
        {
            while (!_stopSignal.IsSet)
            {
                string line;
                try
                {
                    line = Console.ReadLine();
                }
                catch (Exception)
                {
                    return;
                }

                if (line == null)
                {
                    return;
                }

                string trimmed = line.Trim();
                string lower = trimmed.ToLowerInvariant();

                if (lower == "q" || lower == "quit" || lower == "exit")
                {
                    _sink.Log(LogLevel.Info, "[test] 收到退出命令");
                    _stopSignal.Set();
                    return;
                }

                if (lower.Length == 0 || lower == "t" || lower == "trigger")
                {
                    TriggerOnce("手动触发");
                    continue;
                }

                if (lower.StartsWith("c "))
                {
                    string code = trimmed.Substring(2).Trim();
                    int ret = _control.ComplementCode(code, 0);
                    _sink.Log(ret == 0 ? LogLevel.Info : LogLevel.Warn, "[test] 补码 " + code + " 返回 " + ret);
                    continue;
                }

                Console.WriteLine("[test] 可用命令：Enter 或 t = 软触发一次；c <条码> = 补码；q = 退出");
            }
        }
    }
}
