using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using DwsEdge.Core.Ipc;

namespace DwsEdge.Host
{
    /// <summary>
    /// 常驻命令通道的服务端（命名管道，仅本机）。
    ///
    /// 宿主启动采集之后把它拉起来，于是"宿主跑着"的时候也能执行软触发/补码/状态查询：
    ///   * 不用再起第二个宿主进程（那会抢相机，SDK 报 3001 相机被占用）；
    ///   * 平台界面上的"软触发一次"按钮走的就是这条通道。
    ///
    /// 命令的解析与执行都在宿主里（复用 RunCommand），这里只管收发与并发。
    /// </summary>
    internal sealed class HostCommandServer : IDisposable
    {
        /// <summary>执行一条命令，返回退出码与给调用方看的输出。与 RunCommand 的签名对齐。</summary>
        public delegate int CommandHandler(string command, string code, long timeMs, bool force, TextWriter output);

        private readonly string _pipeName;
        private readonly CommandHandler _handler;
        private readonly Action<string, bool> _log;      // (消息, 是否警告)
        private readonly object _sync = new object();

        /// <summary>正在 WaitForConnection 的那个实例：Dispose 时关掉它把阻塞的 Accept 解出来。</summary>
        private NamedPipeServerStream _pending;
        private volatile bool _running;

        public string PipeName
        {
            get { return _pipeName; }
        }

        public HostCommandServer(string runtimeDir, CommandHandler handler, Action<string, bool> log)
        {
            _pipeName = HostCommandChannel.PipeNameFor(runtimeDir);
            _handler = handler;
            _log = log;
        }

        public void Start()
        {
            _running = true;
            Thread thread = new Thread(AcceptLoop);
            thread.IsBackground = true;
            thread.Name = "host-command-pipe";
            thread.Start();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                NamedPipeServerStream server = null;
                try
                {
                    // 多个实例：平台界面上连点两下、或者脚本和界面同时调用时不会互相挡
                    server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 8,
                        PipeTransmissionMode.Byte, PipeOptions.None);

                    lock (_sync)
                    {
                        _pending = server;
                    }
                    server.WaitForConnection();
                    lock (_sync)
                    {
                        _pending = null;
                    }

                    NamedPipeServerStream connected = server;
                    server = null;
                    ThreadPool.QueueUserWorkItem(delegate(object state) { Handle((NamedPipeServerStream)state); }, connected);
                }
                catch (Exception ex)
                {
                    // Dispose 时 _pending 被关掉，WaitForConnection 会抛异常 —— 这时候安静退出
                    if (_running)
                    {
                        Log("命令通道异常（继续监听）：" + ex.Message, true);
                        Thread.Sleep(200);
                    }
                }
                finally
                {
                    if (server != null)
                    {
                        try { server.Dispose(); }
                        catch (Exception) { }
                    }
                }
            }
        }

        private void Handle(NamedPipeServerStream server)
        {
            try
            {
                using (server)
                {
                    Encoding utf8 = new UTF8Encoding(false);

                    string request;
                    using (StreamReader reader = new StreamReader(server, utf8, false, 4096, true))
                    {
                        request = reader.ReadLine();
                    }

                    string command, code;
                    long timeMs;
                    bool force;
                    ParseRequest(request, out command, out code, out timeMs, out force);

                    StringWriter output = new StringWriter(CultureInfo.InvariantCulture);
                    int exitCode;
                    try
                    {
                        exitCode = _handler(command, code, timeMs, force, output);
                    }
                    catch (Exception ex)
                    {
                        exitCode = 3;
                        output.WriteLine("[cmd] 命令执行异常：" + ex.Message);
                        Log("命令通道执行 " + command + " 异常：" + ex, true);
                    }

                    string text = exitCode.ToString(CultureInfo.InvariantCulture)
                        + "\n" + output.ToString().Replace("\r\n", "\n");
                    byte[] payload = utf8.GetBytes(text);
                    server.Write(payload, 0, payload.Length);
                    server.Flush();

                    Log("命令通道执行 " + (request ?? "(空)") + " → 退出码 " + exitCode, exitCode != 0);
                }
            }
            catch (Exception ex)
            {
                Log("命令通道处理请求失败：" + ex.Message, true);
            }
        }

        /// <summary>
        /// 解析一行请求：
        ///   status
        ///   soft-trigger [--force]
        ///   recode &lt;条码&gt; [时间戳ms]
        /// </summary>
        private static void ParseRequest(string request, out string command, out string code, out long timeMs, out bool force)
        {
            command = string.Empty;
            code = null;
            timeMs = 0;
            force = false;

            if (string.IsNullOrEmpty(request))
            {
                return;
            }

            string[] parts = request.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return;
            }

            command = parts[0].Trim().ToLowerInvariant();
            for (int i = 1; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.Equals(part, "--force", StringComparison.OrdinalIgnoreCase))
                {
                    force = true;
                    continue;
                }

                long parsed;
                if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                {
                    timeMs = parsed;
                    continue;
                }

                if (string.IsNullOrEmpty(code))
                {
                    code = part;
                }
            }
        }

        private void Log(string message, bool warning)
        {
            if (_log != null)
            {
                try { _log(message, warning); }
                catch (Exception) { }
            }
        }

        public void Dispose()
        {
            _running = false;

            NamedPipeServerStream pending;
            lock (_sync)
            {
                pending = _pending;
                _pending = null;
            }

            if (pending != null)
            {
                // 关掉正在等待连接的实例，让 Accept 循环里的 WaitForConnection 抛出来
                try { pending.Dispose(); }
                catch (Exception) { }
            }
        }
    }
}
