using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DwsEdge.Shell
{
    /// <summary>
    /// 界面外壳（kiosk 套壳）：把平台网页装成一个"桌面应用"的样子。
    ///
    /// 它做四件事（都很朴素，故意不引第三方库，net48 自带运行时就能跑）：
    ///   1. 单实例：同一个盒子只允许开一个外壳，避免操作员点出七八个窗口；
    ///   2. 等平台：启动时探 /api/health，没起来就自己把采集宿主 + 平台拉起来（-NoStart 可关），
    ///      期间显示"正在连接平台…"的等待窗口，把 address、已等待秒数、最近错误都写清楚；
    ///   3. 承载页面：用系统 Edge 的"应用模式"（无地址栏、独立任务栏图标）或 kiosk 全屏；
    ///   4. 看守：页面开着的时候每 5 秒探一次平台，掉了就把等待窗口弹出来提示"正在重连"，
    ///      恢复了自动收起来 —— 现场看到的就不是白屏，而是明确的状态。
    ///
    /// 参数：
    ///   -Url http://127.0.0.1:8090   平台地址（默认本机 8090）
    ///   -Kiosk                       全屏 kiosk（大屏/一体机用；默认是应用窗口）
    ///   -NoStart                     平台没起来时不要自动拉起来，只提示
    ///   -WaitSeconds 90              等待平台就绪的上限秒数
    ///   -RuntimeDir &lt;路径&gt;       runtime 目录（默认自动探测）
    ///   -SelfTest                    只做自检（探测 Edge/平台/路径）并写结果文件，不开窗口
    /// </summary>
    internal static class Program
    {
        private const string MutexName = @"Global\DwsEdge.Shell";
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DwsEdge");

        [STAThread]
        private static int Main(string[] args)
        {
            Options options;
            try
            {
                options = Options.Parse(args);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "DWS 外壳", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            if (options.SelfTest)
            {
                return SelfTest(options);
            }

            // 1) 单实例
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    return 0;   // 已经有一个外壳在跑，安静退出（现场重复点图标不弹一堆窗口）
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                string edge = FindEdge(options.EdgePath);
                using (ShellForm form = new ShellForm(options, edge))
                {
                    Application.Run(form);
                }
                return 0;
            }
        }

        /// <summary>自检：不弹窗口，把关键结论写进 %LOCALAPPDATA%\DwsEdge\shell-selftest.txt（并尽量打到控制台）。</summary>
        private static int SelfTest(Options options)
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("DWS 外壳自检");
            report.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("runtime 目录：" + (options.RuntimeDir ?? "(未找到)") + "  存在=" + (options.RuntimeDir != null && Directory.Exists(options.RuntimeDir)));
            report.AppendLine("平台地址：" + options.Url);

            string error;
            bool healthy = IsPlatformHealthy(options.Url, out error);
            report.AppendLine("平台健康检查：" + (healthy ? "通过" : "未通过") + (string.IsNullOrEmpty(error) ? string.Empty : "（" + error + "）"));

            string edge = FindEdge(options.EdgePath);
            report.AppendLine("Edge：" + (edge ?? "(没找到，将退回默认浏览器)"));
            report.AppendLine("显示模式：" + (options.Kiosk ? "kiosk 全屏" : "应用窗口"));
            report.AppendLine("自动拉起平台：" + (options.NoStart ? "关" : "开"));
            report.AppendLine("等待上限：" + options.WaitSeconds + " 秒");
            report.AppendLine("将要执行的 Edge 命令行：");
            report.AppendLine("  " + BuildEdgeArguments(options, edge));

            string text = report.ToString();
            string written = null;
            try
            {
                Directory.CreateDirectory(LogDir);
                written = Path.Combine(LogDir, "shell-selftest.txt");
                File.WriteAllText(written, text, new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // %LOCALAPPDATA% 写不进去（受限账户/策略）→ 落到 exe 旁边，保证自检结果拿得到
                try
                {
                    written = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shell-selftest.txt");
                    File.WriteAllText(written, text, new UTF8Encoding(false));
                }
                catch (Exception)
                {
                }
            }
            report.AppendLine("结果文件：" + (written ?? "(没写成)"));

            try
            {
                Console.WriteLine(text);
            }
            catch (Exception)
            {
            }

            return healthy ? 0 : 2;
        }

        /// <summary>探一次平台健康检查。net48 里用 HttpWebRequest，避免额外的包依赖。</summary>
        internal static bool IsPlatformHealthy(string url, out string error)
        {
            error = null;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url.TrimEnd('/') + "/api/health");
                request.Method = "GET";
                request.Timeout = 2500;
                request.ReadWriteTimeout = 2500;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if ((int)response.StatusCode != 200)
                    {
                        error = "HTTP " + (int)response.StatusCode;
                        return false;
                    }
                    using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                    {
                        reader.ReadToEnd();
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 探一次"采集宿主在不在"：平台没起来时宿主肯定也不在；平台起来后看命令通道能不能连上。
        /// 这样外壳就能区分"平台挂了"和"平台在、宿主挂了"——后者以前没人管。
        /// </summary>
        internal static bool IsHostRunning(string url, out string error)
        {
            error = null;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url.TrimEnd('/') + "/api/host/channel");
                request.Method = "GET";
                request.Timeout = 2500;
                request.ReadWriteTimeout = 2500;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    if (body.IndexOf("\"available\":true", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                    error = "命令通道连不上：采集宿主没在跑";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>把采集宿主 + 平台拉起来（复用包里的 start-all.ps1，隐藏窗口）。</summary>
        /// <summary>
        /// 把采集宿主 + 平台拉起来（复用包里的 start-all.ps1，隐藏窗口）。
        /// 关键：把 start-all 的输出重定向到 logs\shell-start.log —— 它要是因为什么原因没启动成功
        /// （端口被占、脚本觉得已在运行…），外壳能把原因显示出来，而不是安静地干等。
        /// </summary>
        internal static void StartRuntime(string runtimeDir)
        {
            if (string.IsNullOrEmpty(runtimeDir))
            {
                return;
            }

            string script = Path.Combine(runtimeDir, @"tools\start-all.ps1");
            if (!File.Exists(script))
            {
                return;
            }

            string logPath = Path.Combine(runtimeDir, @"logs\shell-start.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            }
            catch (Exception)
            {
            }

            // 用 -Command + *> 把 start-all 的所有输出落到日志里（-File 没法直接重定向到文件）
            string command = "& '" + script + "' -RuntimeDir '" + runtimeDir + "' *> '" + logPath + "'";
            ProcessStartInfo info = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + command + "\"");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(info);
        }

        /// <summary>读 start-all 的日志尾巴（等待失败时显示给现场看）。</summary>
        internal static string ReadStartLogTail(string runtimeDir, int maxLines)
        {
            try
            {
                string logPath = Path.Combine(runtimeDir, @"logs\shell-start.log");
                if (!File.Exists(logPath))
                {
                    return "(没有 logs\\shell-start.log：start-all.ps1 可能没被执行)";
                }

                string[] lines = File.ReadAllLines(logPath, Encoding.UTF8);
                int start = Math.Max(0, lines.Length - maxLines);
                StringBuilder tail = new StringBuilder();
                for (int i = start; i < lines.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(lines[i]))
                    {
                        tail.AppendLine(lines[i].Trim());
                    }
                }
                return tail.ToString().Trim();
            }
            catch (Exception ex)
            {
                return "(读日志失败：" + ex.Message + ")";
            }
        }

        /// <summary>找系统 Edge：先看两个 Program Files，再查注册表 App Paths。</summary>
        internal static string FindEdge(string explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            {
                return explicitPath;
            }

            string[] candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe")
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    return candidates[i];
                }
            }

            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"))
                {
                    if (key != null)
                    {
                        string value = key.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(value) && File.Exists(value))
                        {
                            return value;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        /// <summary>用 Edge 承载页面（应用模式或 kiosk），返回进程；没找到 Edge 时退回默认浏览器并返回 null。</summary>
        internal static Process LaunchBrowser(Options options, string edge)
        {
            string profileDir = Path.Combine(LogDir, "shell-profile");
            try
            {
                Directory.CreateDirectory(profileDir);
            }
            catch (Exception)
            {
            }

            if (string.IsNullOrEmpty(edge))
            {
                // 没有 Edge（比如某些精简系统）→ 退回系统默认浏览器，至少能用
                try { Process.Start(options.Url); } catch (Exception) { }
                return null;
            }

            string arguments = BuildEdgeArguments(options, edge);
            ProcessStartInfo info = new ProcessStartInfo(edge, arguments);
            info.UseShellExecute = false;
            return Process.Start(info);
        }

        /// <summary>拼 Edge 的命令行（自检时会把它打出来，方便核对）。</summary>
        internal static string BuildEdgeArguments(Options options, string edge)
        {
            string browser = string.IsNullOrEmpty(edge) ? "msedge.exe" : "\"" + edge + "\"";
            string profileDir = Path.Combine(LogDir, "shell-profile");

            string arguments = options.Kiosk
                ? "--kiosk \"" + options.Url + "\" --edge-kiosk-type=fullscreen"
                : "--app=\"" + options.Url + "\" --window-size=1680,1020 --window-position=40,40";

            arguments += " --user-data-dir=\"" + profileDir + "\" --no-first-run --no-default-browser-check"
                + " --disable-features=Translate,msEdgeIdentityIntegration --disable-sync";

            return browser + " " + arguments;
        }

        /// <summary>命令行参数。</summary>
        internal sealed class Options
        {
            public string Url = "http://127.0.0.1:8090";
            public bool Kiosk;
            public bool NoStart;
            public int WaitSeconds = 90;
            public string RuntimeDir;
            public string EdgePath;
            public bool SelfTest;

            public static Options Parse(string[] args)
            {
                Options options = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    switch (a.ToLowerInvariant())
                    {
                        case "-url":
                        case "--url":
                            options.Url = Next(args, ref i, a);
                            break;
                        case "-kiosk":
                        case "--kiosk":
                            options.Kiosk = true;
                            break;
                        case "-nostart":
                        case "--no-start":
                            options.NoStart = true;
                            break;
                        case "-waitseconds":
                        case "--wait-seconds":
                        {
                            int seconds;
                            if (!int.TryParse(Next(args, ref i, a), out seconds) || seconds < 5 || seconds > 1800)
                            {
                                throw new ArgumentException("-WaitSeconds 需要 5~1800 之间的整数");
                            }
                            options.WaitSeconds = seconds;
                            break;
                        }
                        case "-runtimedir":
                        case "--runtime-dir":
                            options.RuntimeDir = Next(args, ref i, a);
                            break;
                        case "-edge":
                        case "--edge":
                            options.EdgePath = Next(args, ref i, a);
                            break;
                        case "-selftest":
                        case "--self-test":
                            options.SelfTest = true;
                            break;
                        case "-h":
                        case "--help":
                            throw new ArgumentException(
                                "用法：DwsEdge.Shell.exe [-Url http://127.0.0.1:8090] [-Kiosk] [-NoStart] [-WaitSeconds 90] [-RuntimeDir <runtime>] [-SelfTest]");
                        default:
                            throw new ArgumentException("不认识的参数：" + a + "（用 -Help 看用法）");
                    }
                }

                if (string.IsNullOrEmpty(options.RuntimeDir))
                {
                    options.RuntimeDir = DetectRuntimeDir();
                }
                return options;
            }

            private static string Next(string[] args, ref int index, string name)
            {
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException(name + " 后面缺参数值");
                }
                index++;
                return args[index];
            }

            /// <summary>找一个目录，里面有 DwsEdge.Host.exe（交付包是 &lt;runtime&gt;\，开发时是仓库\runtime）。</summary>
            private static string DetectRuntimeDir()
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] candidates = new string[]
                {
                    baseDir,
                    Path.Combine(baseDir, "runtime"),
                    Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\runtime"))
                };
                for (int i = 0; i < candidates.Length; i++)
                {
                    try
                    {
                        if (File.Exists(Path.Combine(candidates[i], "DwsEdge.Host.exe")))
                        {
                            return Path.GetFullPath(candidates[i]);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                return baseDir;
            }
        }
    }
}
