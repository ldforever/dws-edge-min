using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace DwsEdge.Core.Ipc
{
    /// <summary>一条命令通道调用的结果。</summary>
    public sealed class HostCommandResult
    {
        /// <summary>通道是否可用（false = 宿主没在跑 / 通道没起）。</summary>
        public bool ChannelAvailable;

        /// <summary>宿主返回的退出码，含义与 host-command.ps1 一致（0 成功 / 2 启动失败 / 4 命令失败 / 5 模式不允许 / 6 不支持）。</summary>
        public int ExitCode;

        /// <summary>宿主输出的原文（人看的，直接给界面或控制台）。</summary>
        public string Message;

        public bool Ok
        {
            get { return ChannelAvailable && ExitCode == 0; }
        }

        public static HostCommandResult Unavailable(string message)
        {
            HostCommandResult result = new HostCommandResult();
            result.ChannelAvailable = false;
            result.ExitCode = -1;
            result.Message = message;
            return result;
        }
    }

    /// <summary>
    /// 采集宿主的"常驻命令通道"（命名管道，仅本机）。
    ///
    /// 为什么要有它：
    ///   以前"软触发一次"只能靠再起一个 DwsEdge.Host.exe 进程去执行命令，
    ///   那个进程会自己开一次 SDK —— 相机被正在跑的宿主占着，结果是 3001（相机被占用），
    ///   所以现场必须先停宿主才能触发。有了常驻通道，宿主跑着的时候也能直接触发/补码。
    ///
    /// 协议（一问一答，UTF-8，行分隔）：
    ///   请求：一行文本
    ///         status
    ///         soft-trigger [--force]
    ///         recode &lt;条码&gt; [时间戳ms]
    ///   应答：第一行 = 退出码；其余 = 宿主输出原文
    ///
    /// 管道名由 runtime 目录算出来（同一台机器上跑多套 runtime 也不会串）：
    ///   dws-edge-host-&lt;runtime 路径 SHA1 前 8 位&gt;
    /// PowerShell 侧算法必须一致，见 tools\host-command.ps1。
    /// </summary>
    public static class HostCommandChannel
    {
        public const string PipePrefix = "dws-edge-host-";
        public const int DefaultTimeoutMs = 20000;

        /// <summary>规范化 runtime 目录（算管道名和比对都用它）。</summary>
        public static string NormalizeRuntimeDir(string runtimeDir)
        {
            if (string.IsNullOrEmpty(runtimeDir))
            {
                return string.Empty;
            }

            string full;
            try
            {
                full = Path.GetFullPath(runtimeDir);
            }
            catch (Exception)
            {
                full = runtimeDir;
            }

            full = full.Replace('/', '\\').TrimEnd('\\');
            return full.ToLowerInvariant();
        }

        /// <summary>由 runtime 目录得到管道名（宿主与调用方必须算出同一个）。</summary>
        public static string PipeNameFor(string runtimeDir)
        {
            string normalized = NormalizeRuntimeDir(runtimeDir);
            byte[] bytes = Encoding.UTF8.GetBytes(normalized);

            string hash;
            using (SHA1 sha = SHA1.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(digest.Length * 2);
                for (int i = 0; i < digest.Length; i++)
                {
                    sb.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                hash = sb.ToString();
            }

            return PipePrefix + hash.Substring(0, 8);
        }

        /// <summary>
        /// 发一条命令给正在跑的宿主。宿主没在跑（或通道没起）时返回 ChannelAvailable=false，
        /// 不抛异常 —— 调用方据此决定是否退回"新起进程"的老路。
        /// </summary>
        public static HostCommandResult Send(string runtimeDir, string request, int timeoutMs)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = DefaultTimeoutMs;
            }
            if (string.IsNullOrEmpty(request))
            {
                return HostCommandResult.Unavailable("命令为空");
            }

            string pipeName = PipeNameFor(runtimeDir);
            NamedPipeClientStream pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
                pipe.Connect(timeoutMs);

                Encoding utf8 = new UTF8Encoding(false);

                // 写完请求不关流：紧接着要读应答（关掉写侧会让服务端拿不到换行）
                byte[] payload = utf8.GetBytes(request.Trim() + "\n");
                pipe.Write(payload, 0, payload.Length);
                pipe.Flush();

                using (StreamReader reader = new StreamReader(pipe, utf8, false, 4096, true))
                {
                    string first = reader.ReadLine();
                    string rest = reader.ReadToEnd();

                    HostCommandResult result = new HostCommandResult();
                    result.ChannelAvailable = true;
                    result.Message = (rest ?? string.Empty).Trim();

                    int code;
                    if (!string.IsNullOrEmpty(first)
                        && int.TryParse(first.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                    {
                        result.ExitCode = code;
                    }
                    else
                    {
                        result.ExitCode = 4;
                        result.Message = ("宿主应答异常：" + (first ?? "(空)") + "\n" + result.Message).Trim();
                    }
                    return result;
                }
            }
            catch (TimeoutException)
            {
                return HostCommandResult.Unavailable("命令通道没连上（宿主没在跑？超时 " + timeoutMs + " ms）");
            }
            catch (IOException ex)
            {
                return HostCommandResult.Unavailable("命令通道不可用：" + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return HostCommandResult.Unavailable("命令通道被拒绝：" + ex.Message);
            }
            finally
            {
                if (pipe != null)
                {
                    try { pipe.Dispose(); }
                    catch (Exception) { }
                }
            }
        }
    }
}
