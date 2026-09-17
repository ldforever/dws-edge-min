using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using DwsEdge.Core.Abstractions;

namespace DwsEdge.Host
{
    /// <summary>
    /// 最小采集宿主（方案 B 的 Edge 进程）。
    ///
    /// 它做三件事：
    ///   1. 读 config\gateway.ini，决定用哪个 provider；
    ///   2. 从 providers\*.dll 里加载 provider 插件（宿主不引用任何厂商程序集）；
    ///   3. 启动采集，把 provider 出来的规范事件交给 HostEventSink（控制台 + 日志 + JSONL spool）。
    ///
    /// 用法：
    ///   DwsEdge.Host.exe                 常驻运行，Ctrl+C 停止
    ///   DwsEdge.Host.exe --duration 30   跑 30 秒后自动退出（做冒烟测试用）
    /// </summary>
    internal static class Program
    {
        private static readonly ManualResetEventSlim StopSignal = new ManualResetEventSlim(false);

        private static int Main(string[] args)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Directory.SetCurrentDirectory(baseDir);

            int durationSeconds = 0;
            int triggerIntervalMs = 3000;
            int triggerDelayMs = -1;
            bool triggerOnceFlag = false;
            bool verifyConfig = false;
            bool showHelp = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--duration" && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out durationSeconds);
                    i++;
                }
                else if (args[i] == "--trigger-once")
                {
                    triggerOnceFlag = true;
                }
                else if (args[i] == "--verify-config")
                {
                    verifyConfig = true;
                }
                else if (args[i] == "--trigger-interval" && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out triggerIntervalMs);
                    i++;
                }
                else if (args[i] == "--trigger-delay" && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out triggerDelayMs);
                    i++;
                }
                else if (args[i] == "--help" || args[i] == "-h")
                {
                    showHelp = true;
                }
            }

            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (Exception)
            {
                // 某些终端不允许改编码，忽略
            }

            Console.WriteLine("============================================================");
            Console.WriteLine(" DWS Edge Min - 方案B 采集宿主最小模型");
            Console.WriteLine(" 运行时目录: " + baseDir);
            Console.WriteLine("============================================================");

            if (showHelp)
            {
                PrintHelp();
                return 0;
            }

            string configPath = Path.Combine(baseDir, "config", "gateway.ini");
            if (!File.Exists(configPath))
            {
                Console.WriteLine("[host] 找不到配置文件：" + configPath);
                return 1;
            }

            SimpleConfig config = SimpleConfig.Load(configPath);
            string providerId = config.Get("runtime", "provider", "dahua-dws");
            bool spoolEnabled = ParseBool(config.Get("spool", "enabled", "true"), true);

            // 测试环境的软触发开关：配置文件里的 [test] 段，命令行参数可以覆盖
            TestOptions testOptions = new TestOptions();
            testOptions.EnableSoftTrigger = ParseBool(config.Get("test", "enableSoftTrigger", "false"), false);
            testOptions.TriggerOnStart = ParseBool(config.Get("test", "triggerOnStart", "false"), false);
            testOptions.TriggerDelayMs = ParseInt(config.Get("test", "triggerDelayMs", "1500"), 1500);
            testOptions.TriggerIntervalMs = ParseInt(config.Get("test", "triggerIntervalMs", "0"), 0);

            if (triggerOnceFlag)
            {
                testOptions.EnableSoftTrigger = true;
                testOptions.TriggerOnStart = true;
            }
            if (triggerIntervalMs > 0)
            {
                testOptions.EnableSoftTrigger = true;
                testOptions.TriggerIntervalMs = triggerIntervalMs;
            }
            if (triggerDelayMs >= 0)
            {
                testOptions.TriggerDelayMs = triggerDelayMs;
            }

            HostEventSink sink = new HostEventSink(baseDir, spoolEnabled);
            IAcquisitionProvider provider = null;
            TestConsole testConsole = null;
            ImageRetentionService retention = null;
            SpoolRetentionService spoolRetention = null;
            int exitCode = 0;

            try
            {
                ProviderRegistry registry = new ProviderRegistry();
                registry.LoadFromDirectory(Path.Combine(baseDir, "providers"), delegate(string message)
                {
                    sink.Log(LogLevel.Info, message);
                });

                sink.Log(LogLevel.Info, "配置的 provider = " + providerId);

                ProviderSettings settings = new ProviderSettings(baseDir, config.Section(providerId));
                provider = registry.Create(providerId, settings, sink);

                // 告诉事件出口：该 provider 是否分阶段上报（决定平台能否判定"包裹已完整"）
                sink.StagedParcelResult = (provider.Capabilities & ProviderCapabilities.StagedParcelResult) != 0;

                sink.Log(LogLevel.Info, "provider 能力 = " + provider.Capabilities);

                Console.CancelKeyPress += OnCancelKeyPress;

                // 校验模式：启动 SDK → 回读配置 → 给出 PASS/FAIL 后退出（供 apply-config.ps1 使用）
                // 注意：SDK 根本起不来（没插狗、原生 DLL 缺失、配置让 SDK 报错）也要算"校验不通过"，
                // 返回 2 让 apply-config.ps1 触发回滚，而不是落到通用异常分支的 3（3 是"不支持校验"）。
                if (verifyConfig)
                {
                    try
                    {
                        provider.Start();
                    }
                    catch (Exception ex)
                    {
                        sink.Log(LogLevel.Error, "校验失败：SDK 启动异常 - " + ex.Message);
                        Console.WriteLine("[verify] FAIL");
                        return 2;
                    }

                    exitCode = RunVerifyConfig(provider, sink);
                    return exitCode;
                }

                provider.Start();

                // 存图保留策略（按天数清理 + 磁盘水位保护），与厂商无关
                retention = StartRetention(baseDir, config, providerId, sink);
                // spool 事件文件保留策略（带业务端消费保护）
                spoolRetention = StartSpoolRetention(baseDir, config, sink);

                if (testOptions.EnableSoftTrigger)
                {
                    ITriggerControl control = provider as ITriggerControl;
                    if (control == null)
                    {
                        sink.Log(LogLevel.Warn, "[test] 当前 provider（" + provider.ProviderId + "）不支持软触发，测试开关未生效");
                    }
                    else
                    {
                        testConsole = new TestConsole(sink, control, testOptions, StopSignal);
                        testConsole.Start();
                    }
                }

                sink.Log(LogLevel.Info, "运行中…… 按 Ctrl+C 停止。" + (durationSeconds > 0 ? "（将在 " + durationSeconds + " 秒后自动退出）" : string.Empty));

                if (durationSeconds > 0)
                {
                    StopSignal.Wait(TimeSpan.FromSeconds(durationSeconds));
                    sink.Log(LogLevel.Info, "达到运行时长，准备退出。");
                }
                else
                {
                    StopSignal.Wait();
                    sink.Log(LogLevel.Info, "收到停止信号，准备退出。");
                }
            }
            catch (ProviderException ex)
            {
                Console.WriteLine();
                Console.WriteLine("[host] provider 启动失败：" + ex.Message);
                PrintTroubleshooting();
                exitCode = 2;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("[host] 未处理异常：" + ex);
                exitCode = 3;
            }
            finally
            {
                if (spoolRetention != null)
                {
                    spoolRetention.Dispose();
                }

                if (retention != null)
                {
                    retention.Dispose();
                }

                if (testConsole != null)
                {
                    testConsole.Dispose();
                }

                if (provider != null)
                {
                    try
                    {
                        provider.Stop();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[host] provider.Stop 异常：" + ex.Message);
                    }

                    try
                    {
                        provider.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }

                sink.Dispose();
                Console.WriteLine("[host] 已退出。图片目录：images\\  事件 spool：spool\\  日志：logs\\");
            }

            return exitCode;
        }

        /// <summary>
        /// 校验模式：调用 provider 的配置回读校验，打印明细与结论。
        /// 退出码：0=通过；2=不通过（apply-config.ps1 据此触发回滚）；3=provider 不支持校验。
        /// </summary>
        private static int RunVerifyConfig(IAcquisitionProvider provider, HostEventSink sink)
        {
            IConfigVerification verifier = provider as IConfigVerification;
            if (verifier == null)
            {
                sink.Log(LogLevel.Warn, "当前 provider（" + provider.ProviderId + "）不支持配置校验");
                Console.WriteLine("[verify] UNSUPPORTED");
                return 3;
            }

            ConfigVerificationReport report = verifier.VerifyConfig();
            for (int i = 0; i < report.Details.Count; i++)
            {
                sink.Log(LogLevel.Info, "校验：" + report.Details[i]);
            }
            for (int i = 0; i < report.Problems.Count; i++)
            {
                sink.Log(LogLevel.Error, "校验不通过：" + report.Problems[i]);
            }

            if (report.Success)
            {
                sink.Log(LogLevel.Info, "配置校验通过：SDK 启动成功，配置参数已生效");
                Console.WriteLine("[verify] PASS");
                return 0;
            }

            sink.Log(LogLevel.Error, "配置校验失败：共 " + report.Problems.Count + " 项问题");
            Console.WriteLine("[verify] FAIL");
            return 2;
        }

        /// <summary>
        /// 按 [storage] 的 spoolRetentionDays 启动 spool 事件文件保留策略。
        /// 只有业务平台写过 spool\.consumed 标记之后才会删，平台没消费过就不删。
        /// </summary>
        private static SpoolRetentionService StartSpoolRetention(string baseDir, SimpleConfig config, HostEventSink sink)
        {
            string spoolDir = Path.Combine(baseDir, "spool");
            int days = ParseInt(config.Get("storage", "spoolRetentionDays", "0"), 0);
            int interval = ParseInt(config.Get("storage", "cleanupIntervalMinutes", "30"), 30);
            bool cleanupOnStart = ParseBool(config.Get("storage", "cleanupOnStart", "true"), true);

            SpoolRetentionService service = new SpoolRetentionService(spoolDir, days, interval,
                delegate(string message, bool warning)
                {
                    sink.Log(warning ? LogLevel.Warn : LogLevel.Info, message);
                });

            if (!service.Enabled)
            {
                sink.Log(LogLevel.Info, "未启用 spool 保留策略（[storage] 里 spoolRetentionDays=0）");
                return null;
            }

            sink.Log(LogLevel.Info, string.Format(CultureInfo.InvariantCulture,
                "spool 保留策略：目录 {0}；保存 {1} 天；每 {2} 分钟检查{3}（仅在业务平台消费后删除）",
                service.SpoolDirectory, days, interval,
                cleanupOnStart ? "（启动时先清理一次）" : string.Empty));

            service.Start(cleanupOnStart);
            return service;
        }

        /// <summary>
        /// 按 [storage] 段启动存图保留策略（与厂商无关）。
        /// 图片目录优先取 [storage] imageDir；为空则沿用该 provider 段里的 imageDir，再兜底 images。
        /// 天数和磁盘水位都为 0（或未配置）时不启用，返回 null。
        /// </summary>
        private static ImageRetentionService StartRetention(string baseDir, SimpleConfig config, string providerId, HostEventSink sink)
        {
            Dictionary<string, string> providerSection = config.Section(providerId);
            string providerImageDir;
            if (!providerSection.TryGetValue("imageDir", out providerImageDir))
            {
                providerImageDir = null;
            }

            string imageDir = config.Get("storage", "imageDir", null);
            if (string.IsNullOrEmpty(imageDir))
            {
                imageDir = string.IsNullOrEmpty(providerImageDir) ? "images" : providerImageDir;
            }

            int retentionDays = ParseInt(config.Get("storage", "retentionDays", "0"), 0);
            int maxDiskPercent = ParseInt(config.Get("storage", "maxDiskPercent", "0"), 0);
            int intervalMinutes = ParseInt(config.Get("storage", "cleanupIntervalMinutes", "30"), 30);
            bool cleanupOnStart = ParseBool(config.Get("storage", "cleanupOnStart", "true"), true);

            string root = Path.IsPathRooted(imageDir) ? imageDir : Path.Combine(baseDir, imageDir);

            ImageRetentionService service = new ImageRetentionService(root, retentionDays, maxDiskPercent, intervalMinutes,
                delegate(string message, bool warning)
                {
                    sink.Log(warning ? LogLevel.Warn : LogLevel.Info, message);
                });

            if (!service.Enabled)
            {
                sink.Log(LogLevel.Info, "未启用存图保留策略（[storage] 里 retentionDays 和 maxDiskPercent 都为 0）");
                return null;
            }

            sink.Log(LogLevel.Info, string.Format(CultureInfo.InvariantCulture,
                "存图保留策略：目录 {0}；保存 {1}；磁盘水位 {2}；每 {3} 分钟检查{4}",
                service.ImageRoot,
                retentionDays <= 0 ? "永久" : retentionDays + " 天",
                maxDiskPercent <= 0 ? "不启用" : maxDiskPercent + "%",
                intervalMinutes,
                cleanupOnStart ? "（启动时先清理一次）" : string.Empty));

            service.Start(cleanupOnStart);
            return service;
        }

        private static void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            StopSignal.Set();
        }

        private static bool ParseBool(string value, bool defaultValue)
        {
            if (string.IsNullOrEmpty(value))
            {
                return defaultValue;
            }
            return value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseInt(string value, int defaultValue)
        {
            int n;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            {
                return n;
            }
            return defaultValue;
        }

        private static void PrintHelp()
        {
            Console.WriteLine();
            Console.WriteLine("用法：");
            Console.WriteLine("  DwsEdge.Host.exe                        常驻运行，Ctrl+C 停止");
            Console.WriteLine("  DwsEdge.Host.exe --duration 30          跑 30 秒后退出");
            Console.WriteLine("  DwsEdge.Host.exe --trigger-once         启动后软触发一次（测试开关）");
            Console.WriteLine("  DwsEdge.Host.exe --trigger-interval 3000 每 3 秒软触发一次（模拟连续过包）");
            Console.WriteLine("  DwsEdge.Host.exe --trigger-delay 800    启动触发的延迟毫秒（默认 1500）");
            Console.WriteLine("  DwsEdge.Host.exe --verify-config        启动 SDK 并回读校验配置（PASS/FAIL），供 apply-config.ps1 使用");
            Console.WriteLine();
            Console.WriteLine("运行前请确认：");
            Console.WriteLine("  1. runtime\\Cfg\\LogisticsBase.cfg 里的相机 IP 已改成现场相机；");
            Console.WriteLine("  2. 加密狗已插入（大华 SDK 需要）；");
            Console.WriteLine("  3. 本机与相机在同一网段，防火墙未拦截。");
            Console.WriteLine();
            Console.WriteLine("无设备想跑通链路：把 config\\gateway.ini 的 provider 改成 simulator，");
            Console.WriteLine("然后 DwsEdge.Host.exe --trigger-once --duration 5。");
        }

        private static void PrintTroubleshooting()
        {
            Console.WriteLine();
            Console.WriteLine("排查提示：");
            Console.WriteLine("  2200  未检测到加密狗 → 插好 USB 加密狗并确认驱动正常");
            Console.WriteLine("  3000  相机数与配置不符 → 改 Cfg\\LogisticsBase.cfg 里的 <ImageAcq num=...> 与");
            Console.WriteLine("                        <Camera ip=... enable=\"1\">，并确认本机与相机同网段");
            Console.WriteLine("  3001  相机被占用 → 关闭 MVViewer / EasyID 等工具");
            Console.WriteLine("  1000  找不到配置 → 检查 config\\gateway.ini 里的 cfgPath");
            Console.WriteLine("  1001  配置解析失败 → 确认 cfg 是 GB2312 编码、且与 SDK 版本匹配");
            Console.WriteLine("  其他  见 logs\\host-*.log，以及大华 SDK 自己的 runtime\\Log\\default.log");
        }
    }
}
