using System;
using System.Globalization;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DwsEdge.Core.Abstractions;
using DwsEdge.Core.Config;

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

        /// <summary>是否允许把状态写进 logs\host-status.json（只有常驻模式才写，见 Main 里的说明）。</summary>
        private static bool StatusFileEnabled;

        private static int Main(string[] args)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Directory.SetCurrentDirectory(baseDir);

            int durationSeconds = 0;
            int triggerIntervalMs = 0;
            bool triggerIntervalSpecified = false;
            int triggerDelayMs = -1;
            bool triggerOnceFlag = false;
            bool verifyConfig = false;
            bool showHelp = false;
            // A4：一次性命令（执行完带退出码直接退出，不进常驻循环）
            string commandName = null;      // soft-trigger / recode / status
            string recodeCode = null;
            long recodeTimeMs = 0;
            bool forceCommand = false;
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
                    triggerIntervalSpecified = true;
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
                else if (args[i] == "--soft-trigger")
                {
                    commandName = "soft-trigger";
                }
                else if (args[i] == "--recode")
                {
                    commandName = "recode";
                }
                else if (args[i] == "--command-status")
                {
                    commandName = "status";
                }
                else if (args[i] == "--code" && i + 1 < args.Length)
                {
                    recodeCode = args[i + 1];
                    i++;
                }
                else if (args[i] == "--time-ms" && i + 1 < args.Length)
                {
                    long.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out recodeTimeMs);
                    i++;
                }
                else if (args[i] == "--force")
                {
                    forceCommand = true;
                }
                else
                {
                    // 任何没被识别的参数（不管带不带 --）都要报错退出：
                    // 早先只拦"不带 -- 的"，打错一个开关会被静默忽略、宿主照常常驻运行，
                    // 现场会以为"命令发出去了"，实际什么都没做。
                    Console.WriteLine("[host] 未知参数：" + args[i] + "（用 --help 看用法）");
                    return 1;
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
            // 只有显式传了 --trigger-interval 才开定时触发。
            // 早先这里用默认值 3000 配合 "> 0" 判断，等于"任何一次启动都每 3 秒自动触发一次" ——
            // 现场无参数启动宿主会凭空产生包裹，是个很危险的默认值。
            if (triggerIntervalSpecified && triggerIntervalMs > 0)
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
            HostCommandServer commandServer = null;
            ImageRetentionService retention = null;
            SpoolRetentionService spoolRetention = null;
            int exitCode = 0;

            // 启动重试：相机晚接上、加密狗晚插、相机被占用时，不要"起不来就退出"，
            // 而是按 [startup] 的配置重试，并把状态写进 logs\host-status.json（界面据此显示"正在等相机"）。
            StartupRetryOptions retryOptions = StartupRetryOptions.From(config);

            // 只有"常驻模式"才写 host-status.json。
            // 一次性命令（--verify-config / --soft-trigger / --recode）也会走 finally，
            // 如果它们也写状态文件，界面就会在每次"一键应用"校验之后误报"宿主已停止"。
            StatusFileEnabled = string.IsNullOrEmpty(commandName) && !verifyConfig;

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

                string sdkCfgPath = Path.Combine(baseDir, config.Get(providerId, "cfgPath", @"Cfg\LogisticsBase.cfg"));

                // A4：--command-status 只是"查能力和当前模式"，不需要启动 SDK ——
                // 设备异常/没插狗时也能秒回，而不是卡在 SDK 启动上等超时。
                if (string.Equals(commandName, "status", StringComparison.OrdinalIgnoreCase))
                {
                    exitCode = RunCommand(provider, sink, sdkCfgPath, commandName, null, 0, forceCommand, Console.Out);
                    return exitCode;
                }

                if (string.IsNullOrEmpty(commandName))
                {
                    // 常驻模式：SDK 起不来（没相机 / 没加密狗 / 被占用）时按配置重试。
                    // 重试期间进程保持存活、状态写进 logs\host-status.json，相机一接上就自动跑起来。
                    if (!StartProviderWithRetry(provider, sink, baseDir, retryOptions))
                    {
                        exitCode = 2;
                        WriteHostStatus(baseDir, "stopped", 0, 0, "启动重试已用尽或已取消", null);
                        PrintTroubleshooting();
                        return exitCode;
                    }
                }
                else
                {
                    // 一次性命令（--soft-trigger / --recode / --verify-config）：快速失败，
                    // 不能让脚本干等 30 分钟。
                    provider.Start();
                }

                // A4：软触发 / 补码必须等 SDK 就绪，所以放在 Start 之后。
                // 执行完带着退出码退出，不进常驻循环，也不需要 Ctrl+C；finally 里会正常停掉 provider。
                if (!string.IsNullOrEmpty(commandName))
                {
                    exitCode = RunCommand(provider, sink, sdkCfgPath, commandName, recodeCode, recodeTimeMs, forceCommand,
                        Console.Out);
                    return exitCode;
                }

                // 存图保留策略（按天数清理 + 磁盘水位保护），与厂商无关
                retention = StartRetention(baseDir, config, providerId, sink);
                // spool 事件文件保留策略（带业务端消费保护）
                spoolRetention = StartSpoolRetention(baseDir, config, sink);

                // 常驻命令通道：宿主跑着也能软触发/补码/查状态（不用再起一个宿主进程抢相机）
                commandServer = new HostCommandServer(baseDir,
                    delegate(string cmd, string cmdCode, long cmdTimeMs, bool cmdForce, TextWriter cmdOutput)
                    {
                        return RunCommand(provider, sink, sdkCfgPath, cmd, cmdCode, cmdTimeMs, cmdForce, cmdOutput);
                    },
                    delegate(string message, bool warning)
                    {
                        sink.Log(warning ? LogLevel.Warn : LogLevel.Info, message);
                    });
                commandServer.Start();
                sink.Log(LogLevel.Info, "命令通道已开启（命名管道 " + commandServer.PipeName
                    + "）：宿主运行中也能软触发/补码，用 tools\\host-command.ps1 -SoftTrigger 或平台界面上的按钮");

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
                if (commandServer != null)
                {
                    commandServer.Dispose();
                }

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
                WriteHostStatus(baseDir, "stopped", 0, 0, "宿主已退出", null);
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

        /// <summary>
        /// A4：一次性命令。返回进程退出码（脚本据此判断成败，现场不用去翻日志猜）：
        ///   0 成功
        ///   4 命令执行失败（provider 返回非 0，具体含义见日志）
        ///   5 触发模式不允许（当前不是软触发模式，加 --force 可跳过校验）
        ///   6 当前 provider 不支持这条命令
        /// 每条命令都会写进 logs\host-*.log，日志里带命令、参数、结果与退出码。
        ///
        /// output 决定"给人看的输出"往哪儿写：
        ///   * 命令行一次性模式传 Console.Out（原来的行为不变）；
        ///   * 常驻命令通道（命名管道）传 StringWriter，把输出原样回给调用方（界面/脚本）。
        /// </summary>
        private static int RunCommand(IAcquisitionProvider provider, HostEventSink sink, string cfgPath,
            string command, string code, long timeMs, bool force, TextWriter output)
        {
            if (output == null)
            {
                output = Console.Out;
            }

            string providerId = provider != null ? provider.ProviderId : "unknown";
            ITriggerControl control = provider as ITriggerControl;

            sink.Log(LogLevel.Info, "[cmd] 收到命令：" + command
                + (string.IsNullOrEmpty(code) ? string.Empty : " 条码=" + code)
                + (timeMs > 0 ? " 时间戳=" + timeMs : string.Empty)
                + (force ? "（--force：跳过触发模式校验）" : string.Empty)
                + "　provider=" + providerId);

            string mode;
            bool modeKnown = TryReadTriggerMode(cfgPath, out mode);
            string modeText = modeKnown ? TriggerModeText(mode) : "未知（读不到 triggerMode）";

            if (string.Equals(command, "status", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine("[cmd] provider=" + providerId);
                output.WriteLine("[cmd] 支持软触发/补码=" + (control != null ? "是" : "否"));
                output.WriteLine("[cmd] 触发模式=" + modeText);
                output.WriteLine("[cmd] 注意：本命令只查能力与模式，不启动 SDK；要验证设备请用 --verify-config 或 --soft-trigger");
                sink.Log(LogLevel.Info, "[cmd] 能力查询：支持命令=" + (control != null ? "是" : "否") + "，触发模式=" + modeText);
                return 0;
            }

            if (control == null)
            {
                string message = "[cmd] 当前 provider（" + providerId + "）不支持软触发/补码命令";
                output.WriteLine(message);
                sink.Log(LogLevel.Error, message + "　退出码 6");
                return 6;
            }

            if (string.Equals(command, "soft-trigger", StringComparison.OrdinalIgnoreCase))
            {
                // ★ A4 验收点：软触发只在"软触发模式"下生效
                if (!force && modeKnown && !string.Equals(mode, "2", StringComparison.Ordinal))
                {
                    string tip = "[cmd] 当前触发模式是 " + modeText + "，软触发不会生效："
                        + "先用 tools\\set-trigger-mode.ps1 -Mode soft 改成软触发模式（triggerMode=2），"
                        + "或用 --force 跳过这条校验。退出码 5";
                    output.WriteLine(tip);
                    sink.Log(LogLevel.Warn, tip);
                    return 5;
                }
                if (!force && !modeKnown)
                {
                    sink.Log(LogLevel.Warn, "[cmd] 读不到 triggerMode（可能没有 SDK 配置文件），跳过触发模式校验");
                }

                int ret = control.SoftTrigger();
                if (ret == 0)
                {
                    output.WriteLine("[cmd] 软触发成功（返回 0）");
                    sink.Log(LogLevel.Info, "[cmd] 软触发成功（返回 0）　退出码 0");
                    return 0;
                }

                output.WriteLine("[cmd] 软触发失败（返回 " + ret + "）");
                sink.Log(LogLevel.Error, "[cmd] 软触发失败（返回 " + ret + "）　退出码 4");
                return 4;
            }

            if (string.Equals(command, "recode", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    string tip = "[cmd] 补码命令缺少 --code <条码>。退出码 1";
                    output.WriteLine(tip);
                    sink.Log(LogLevel.Error, tip);
                    return 1;
                }

                int ret = control.ComplementCode(code.Trim(), timeMs);
                if (ret == 0)
                {
                    output.WriteLine("[cmd] 补码成功：" + code.Trim() + "（返回 0）");
                    sink.Log(LogLevel.Info, "[cmd] 补码成功：" + code.Trim() + "（返回 0）　退出码 0");
                    return 0;
                }

                output.WriteLine("[cmd] 补码失败：" + code.Trim() + "（返回 " + ret + "）");
                sink.Log(LogLevel.Error, "[cmd] 补码失败：" + code.Trim() + "（返回 " + ret + "）　退出码 4");
                return 4;
            }

            output.WriteLine("[host] 未知命令：" + command);
            sink.Log(LogLevel.Error, "[cmd] 未知命令：" + command + "　退出码 1");
            return 1;
        }

        /// <summary>读 cfg 里的 triggerMode；读不到（没有 cfg / 解析不出）返回 false。</summary>
        private static bool TryReadTriggerMode(string cfgPath, out string mode)
        {
            mode = null;
            try
            {
                if (string.IsNullOrEmpty(cfgPath) || !File.Exists(cfgPath))
                {
                    return false;
                }
                CameraPlan plan = CameraPlan.Read(cfgPath);
                if (plan == null || string.IsNullOrEmpty(plan.TriggerMode))
                {
                    return false;
                }
                mode = plan.TriggerMode.Trim();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string TriggerModeText(string mode)
        {
            if (mode == "2") { return "软触发（triggerMode=2）"; }
            if (mode == "1") { return "硬触发（triggerMode=1，光电）"; }
            if (mode == "0") { return "自由拉流（triggerMode=0，狂扫）"; }
            return mode;
        }

        /// <summary>
        /// 启动 provider，并在"可重试的 SDK 错误"上重试。
        ///
        /// 为什么需要它：宿主以前是"SDK 起不来就退出"。可现实里最常见的情况是
        /// **相机还没上电 / 还没插网线 / 加密狗还没插** —— 这时候进程直接退出，
        /// 等人把相机接好了，还得有人再点一次启动。现在改成：按 [startup] 的配置重试，
        /// 期间进程活着、状态写进 logs\host-status.json（界面显示"正在等相机"），
        /// 相机一接上就自动开始采集。
        ///
        /// 返回 true = 启动成功；false = 不该重试（真错误）或重试已用尽/被 Ctrl+C 取消。
        /// </summary>
        private static bool StartProviderWithRetry(IAcquisitionProvider provider, HostEventSink sink,
            string baseDir, StartupRetryOptions options)
        {
            DateTime startedAt = DateTime.Now;
            int attempt = 0;

            while (true)
            {
                attempt++;
                try
                {
                    provider.Start();
                    WriteHostStatus(baseDir, "running", attempt, 0, null, null);
                    if (attempt > 1)
                    {
                        sink.Log(LogLevel.Info, "采集已启动（第 " + attempt + " 次尝试成功）");
                    }
                    return true;
                }
                catch (ProviderException ex)
                {
                    int code;
                    bool codeKnown = TryGetSdkCode(ex.Message, out code);
                    double waitedMinutes = (DateTime.Now - startedAt).TotalMinutes;
                    bool retryable = options.Enabled
                        && codeKnown
                        && options.Codes.Contains(code)
                        && waitedMinutes < options.MaxMinutes
                        && !StopSignal.IsSet;

                    WriteHostStatus(baseDir, retryable ? "retrying" : "stopped", attempt,
                        codeKnown ? code : 0, ex.Message, retryable ? (int?)options.IntervalSeconds : null);

                    if (!retryable)
                    {
                        sink.Log(LogLevel.Error, "采集宿主启动失败：" + ex.Message);
                        Console.WriteLine("[host] provider 启动失败：" + ex.Message);
                        return false;
                    }

                    string line = "第 " + attempt + " 次启动失败（SDK 返回 " + code + "：" + DescribeSdkCode(code)
                        + "）→ " + options.IntervalSeconds + " 秒后重试；已等 " + (int)waitedMinutes + " 分钟，上限 "
                        + options.MaxMinutes + " 分钟（Ctrl+C 可退出）";
                    sink.Log(LogLevel.Warn, line);
                    Console.WriteLine("[host] " + line);

                    // 分片等待：Ctrl+C 能立刻生效，不用等满一个间隔
                    for (int i = 0; i < options.IntervalSeconds * 10; i++)
                    {
                        if (StopSignal.Wait(100))
                        {
                            sink.Log(LogLevel.Info, "收到停止信号，取消启动重试。");
                            return false;
                        }
                    }
                }
            }
        }

        /// <summary>从异常消息里取出 SDK 返回码（消息形如"Start 失败：返回 3000；相机数与配置不符…"）。</summary>
        private static bool TryGetSdkCode(string message, out int code)
        {
            code = 0;
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            Match match = Regex.Match(message, @"返回\s*(-?\d{1,5})");
            if (!match.Success)
            {
                return false;
            }
            return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
        }

        /// <summary>把 SDK 返回码翻译成现场看得懂的话。</summary>
        private static string DescribeSdkCode(int code)
        {
            if (code == 2200) { return "没检测到加密狗"; }
            if (code == 3000) { return "相机数与配置不符 / 没有相机连上"; }
            if (code == 3001) { return "相机被占用"; }
            if (code == 3002 || code == 3003) { return "相机清单与实际不符"; }
            return "SDK 错误";
        }

        /// <summary>
        /// 把宿主状态写到 logs\host-status.json，供平台与界面显示。
        /// 平台据此区分"宿主没在跑"、"正在等相机（第 N 次重试）"与"运行中"。
        /// </summary>
        private static void WriteHostStatus(string baseDir, string state, int attempt, int code,
            string message, int? retryInSeconds)
        {
            // 一次性命令（校验/软触发/补码）不写状态文件：它们也会走 finally，
            // 写了就会把常驻宿主的状态覆盖成"已停止"，界面跟着误报。
            if (!StatusFileEnabled)
            {
                return;
            }

            try
            {
                string dir = Path.Combine(baseDir, "logs");
                Directory.CreateDirectory(dir);

                StringBuilder json = new StringBuilder();
                json.Append("{\"state\":\"").Append(state).Append('"');
                json.Append(",\"attempt\":").Append(attempt.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"code\":").Append(code.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"retryInSeconds\":").Append(retryInSeconds.HasValue
                    ? retryInSeconds.Value.ToString(CultureInfo.InvariantCulture)
                    : "null");
                json.Append(",\"pid\":").Append(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"atMs\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
                json.Append(",\"message\":\"").Append(EscapeJson(message)).Append('"');
                json.Append('}');

                File.WriteAllText(Path.Combine(dir, "host-status.json"), json.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 状态文件写不进去不影响采集
            }
        }

        private static string EscapeJson(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\\')
                {
                    sb.Append('\\').Append(c);
                }
                else if (c == '\r')
                {
                    sb.Append("\\r");
                }
                else if (c == '\n')
                {
                    sb.Append("\\n");
                }
                else if (c == '\t')
                {
                    sb.Append("\\t");
                }
                else if (c < ' ')
                {
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>[startup] 段：启动重试策略（哪些错误重试、间隔、总时长上限）。</summary>
        private sealed class StartupRetryOptions
        {
            public bool Enabled = true;
            public int IntervalSeconds = 10;
            public int MaxMinutes = 30;
            public HashSet<int> Codes = new HashSet<int>();

            public static StartupRetryOptions From(SimpleConfig config)
            {
                StartupRetryOptions options = new StartupRetryOptions();
                options.Enabled = ParseBool(config.Get("startup", "retryEnabled", "true"), true);
                options.IntervalSeconds = Math.Max(2, ParseInt(config.Get("startup", "retryIntervalSeconds", "10"), 10));
                options.MaxMinutes = Math.Max(0, ParseInt(config.Get("startup", "retryMaxMinutes", "30"), 30));

                string codes = config.Get("startup", "retryOn", "2200,3000,3001");
                if (!string.IsNullOrEmpty(codes))
                {
                    string[] parts = codes.Split(',');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        int code;
                        if (int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                        {
                            options.Codes.Add(code);
                        }
                    }
                }

                // 上限为 0 就等于关掉重试（回归测试与"快速失败"场景用得上）
                if (options.MaxMinutes <= 0)
                {
                    options.Enabled = false;
                }
                return options;
            }
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
            Console.WriteLine("A4 一次性命令（执行完带退出码退出，适合脚本/上位机调用）：");
            Console.WriteLine("  DwsEdge.Host.exe --command-status              看当前 provider 与触发模式");
            Console.WriteLine("  DwsEdge.Host.exe --soft-trigger                软触发一次（要求 triggerMode=2）");
            Console.WriteLine("  DwsEdge.Host.exe --soft-trigger --force        跳过触发模式校验（排查用）");
            Console.WriteLine("  DwsEdge.Host.exe --recode --code SF1234567890  人工补码（可加 --time-ms <Unix 毫秒>）");
            Console.WriteLine();
            Console.WriteLine("宿主【运行中】发命令（常驻命名管道通道，不用停宿主、不抢相机）：");
            Console.WriteLine("  tools\\host-command.ps1 -SoftTrigger           软触发一次（宿主跑着也能用）");
            Console.WriteLine("  tools\\host-command.ps1 -Recode -Code SF123    人工补码");
            Console.WriteLine("  平台界面实时监控页的「软触发一次」按钮走的也是这条通道");
            Console.WriteLine();
            Console.WriteLine("退出码：0 成功 / 1 参数错误 / 2 provider 启动失败 / 3 未处理异常 /");
            Console.WriteLine("        4 命令执行失败 / 5 触发模式不允许（加 --force） / 6 provider 不支持该命令");
            Console.WriteLine("现场更省事：用 tools\\host-command.ps1 -Status / -SoftTrigger / -Recode -Code <条码>");
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
