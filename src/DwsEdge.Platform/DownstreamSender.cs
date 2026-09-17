using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>
    /// B4 下游输出：TCP 客户端 + 模板 + 失败重传。
    ///
    /// 工作方式：
    ///   1. 从 SpoolStore 取"待下发/下发失败且已完整"的包裹（按时间从旧到新）；
    ///   2. 维护一条 TCP 长连接，断了就按 retryIntervalMs 重连；
    ///   3. 每条按模板渲染成报文，写成功后立刻 ack（标记 sent）→ 断电最多重发最后一条；
    ///   4. 发送失败 → ack 失败（标记 failed 并记录原因），保持队列，等下一轮重试。
    ///
    /// 关于"不丢不重"：
    ///   * 不丢：待发状态落在历史库里（dispatchState），平台重启后队列自动恢复；
    ///   * 不重：发成功才标记 sent，下游断线期间只是排队，重连后每条只发一次；
    ///     唯一可能重复的窗口是"写成功但进程在 ack 前被杀"，所以默认模板里带 {traceId}，
    ///     下游按它去重即可（这是工业协议里常规的至少一次 + 幂等键做法）。
    /// </summary>
    public sealed class DownstreamSender : BackgroundService
    {
        private const int LogCapacity = 200;

        private readonly SpoolStore _store;
        private readonly DownstreamStore _config;
        private readonly ILogger<DownstreamSender> _logger;
        private readonly object _sync = new object();
        private readonly LinkedList<DispatchLogItem> _log = new LinkedList<DispatchLogItem>();

        private TcpClient _client;
        private NetworkStream _stream;
        private string _connectedTarget;
        private DateTime _connectedAtUtc = DateTime.MinValue;
        private DateTime _lastConnectAttemptUtc = DateTime.MinValue;

        private long _sent;
        private long _failed;
        private long _retries;
        private long _bytesSent;
        private string _lastSentAt;
        private string _lastError;

        public DownstreamSender(SpoolStore store, DownstreamStore config, ILogger<DownstreamSender> logger)
        {
            _store = store;
            _config = config;
            _logger = logger;
            _config.OnChanged = OnConfigChanged;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("下游输出服务已启动（配置：{0}）", _config.FilePath);

            while (!stoppingToken.IsCancellationRequested)
            {
                int delayMs = 500;
                try
                {
                    DownstreamOptions options = _config.Current;
                    if (options == null || !options.enabled)
                    {
                        delayMs = 1000;
                    }
                    else
                    {
                        delayMs = SendOnce(options);
                    }
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    _logger.LogWarning("下游输出循环异常：{0}", ex.Message);
                    delayMs = 2000;
                }

                try
                {
                    await Task.Delay(delayMs, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            CloseConnection();
        }

        /// <summary>跑一轮：取待发包 → 发送。返回下一轮建议的等待时间。</summary>
        private int SendOnce(DownstreamOptions options)
        {
            int batch = 50;
            List<ParcelRecord> pending = _store.PendingDispatch(batch);
            if (pending.Count == 0)
            {
                return 500;
            }

            if (!EnsureConnected(options))
            {
                return Math.Max(500, options.retryIntervalMs);
            }

            foreach (ParcelRecord record in pending)
            {
                if (record.complete != true && options.sendOnlyComplete)
                {
                    continue;   // 还在等重量体积
                }

                if (options.maxAttempts > 0 && record.dispatchAttempts >= options.maxAttempts)
                {
                    continue;   // 超过最大次数就放着，等人工处理
                }

                string payload = MessageTemplate.Render(options.template, record);
                byte[] bytes = DownstreamStore.ResolveEncoding(options.encoding).GetBytes(payload);

                try
                {
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();

                    // 写成功立刻 ack：把"可能重复"的窗口压到最小
                    _store.AckDispatch(record.traceId, true, null);

                    _sent++;
                    _bytesSent += bytes.Length;
                    _lastSentAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    AddLog(new DispatchLogItem
                    {
                        time = _lastSentAt,
                        traceId = record.traceId,
                        success = true,
                        attempt = record.dispatchAttempts + 1,
                        bytes = bytes.Length,
                        payload = Trim(payload),
                        message = "已下发"
                    });
                    _logger.LogInformation("已下发包裹 {0}（{1} 字节）", record.traceId, bytes.Length);

                    if (options.sendIntervalMs > 0)
                    {
                        Thread.Sleep(options.sendIntervalMs);
                    }
                }
                catch (Exception ex)
                {
                    _failed++;
                    _retries++;
                    _lastError = ex.Message;
                    _store.AckDispatch(record.traceId, false, ex.Message);
                    AddLog(new DispatchLogItem
                    {
                        time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        traceId = record.traceId,
                        success = false,
                        attempt = record.dispatchAttempts + 1,
                        bytes = bytes.Length,
                        payload = Trim(payload),
                        message = "下发失败，等待重试",
                        error = ex.Message
                    });
                    _logger.LogWarning("下发包裹 {0} 失败（{1}），将重试", record.traceId, ex.Message);

                    CloseConnection();
                    return Math.Max(500, options.retryIntervalMs);
                }
            }

            return 200;
        }

        private static string Trim(string payload)
        {
            if (string.IsNullOrEmpty(payload))
            {
                return string.Empty;
            }
            string one = payload.Replace("\r", "\\r").Replace("\n", "\\n");
            return one.Length <= 300 ? one : one.Substring(0, 300) + "…";
        }

        private bool EnsureConnected(DownstreamOptions options)
        {
            string target = options.host + ":" + options.port.ToString(CultureInfo.InvariantCulture);
            if (IsConnectionAlive() && string.Equals(_connectedTarget, target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 重连节流：不要每轮都去连
            if ((DateTime.UtcNow - _lastConnectAttemptUtc).TotalMilliseconds < Math.Max(500, options.retryIntervalMs))
            {
                return false;
            }
            _lastConnectAttemptUtc = DateTime.UtcNow;

            CloseConnection();
            try
            {
                TcpClient client = new TcpClient();
                IAsyncResult ar = client.BeginConnect(options.host, options.port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(options.connectTimeoutMs))
                {
                    client.Close();
                    throw new TimeoutException("连接超时（" + options.connectTimeoutMs + " ms）");
                }
                client.EndConnect(ar);
                client.NoDelay = true;

                _client = client;
                _stream = client.GetStream();
                _connectedTarget = target;
                _connectedAtUtc = DateTime.UtcNow;
                _lastError = null;
                _logger.LogInformation("下游 TCP 已连接：{0}", target);
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                AddLog(new DispatchLogItem
                {
                    time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    traceId = null,
                    success = false,
                    attempt = 0,
                    bytes = 0,
                    message = "连接 " + target + " 失败",
                    error = ex.Message
                });
                _logger.LogWarning("连接下游 {0} 失败：{1}", target, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 连接是否还活着。
        ///
        /// 关键点：对端进程被杀时，本端 socket 不会立刻报错 —— 直接写会"成功"，
        /// 数据被丢进黑洞却把包裹标记成已下发（= 丢数据）。
        /// 所以写之前先看一眼：如果收到了对端的 FIN（可读但没数据）或 socket 出错，就当成断线重连。
        /// </summary>
        private bool IsConnectionAlive()
        {
            if (_client == null || _stream == null)
            {
                return false;
            }

            try
            {
                Socket socket = _client.Client;
                if (socket == null || !socket.Connected)
                {
                    return false;
                }
                if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                {
                    return false;   // 对端已关闭
                }
                if (socket.Poll(0, SelectMode.SelectError))
                {
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void CloseConnection()
        {
            try
            {
                if (_stream != null)
                {
                    _stream.Dispose();
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (_client != null)
                {
                    _client.Close();
                }
            }
            catch (Exception)
            {
            }
            _stream = null;
            _client = null;
            _connectedTarget = null;
        }

        private void OnConfigChanged()
        {
            // 地址/编码变了就断开重连（下次循环会按新配置连）
            CloseConnection();
        }

        private void AddLog(DispatchLogItem item)
        {
            lock (_sync)
            {
                _log.AddFirst(item);
                while (_log.Count > LogCapacity)
                {
                    _log.RemoveLast();
                }
            }
        }

        public List<DispatchLogItem> RecentLog(int limit)
        {
            List<DispatchLogItem> result = new List<DispatchLogItem>();
            lock (_sync)
            {
                LinkedListNode<DispatchLogItem> node = _log.First;
                while (node != null && result.Count < limit)
                {
                    result.Add(node.Value);
                    node = node.Next;
                }
            }
            return result;
        }

        public DownstreamStats Stats()
        {
            DownstreamOptions options = _config.Current;
            DownstreamStats stats = new DownstreamStats();
            stats.enabled = options.enabled;
            stats.target = options.host + ":" + options.port.ToString(CultureInfo.InvariantCulture);
            stats.connected = _client != null && _client.Connected;
            stats.connectedSince = _connectedAtUtc == DateTime.MinValue
                ? null
                : _connectedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            stats.sent = _sent;
            stats.failed = _failed;
            stats.retries = _retries;
            stats.bytesSent = _bytesSent;
            stats.lastSentAt = _lastSentAt;
            stats.lastError = _lastError;
            stats.queueDepth = _store.PendingDispatchCount();
            stats.templateProblems = MessageTemplate.Validate(options.template);
            return stats;
        }

        /// <summary>测试连接（不发送数据），返回是否成功与错误信息。</summary>
        public object TestConnection(DownstreamOptions options)
        {
            string target = options.host + ":" + options.port.ToString(CultureInfo.InvariantCulture);
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect(options.host, options.port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(options.connectTimeoutMs))
                    {
                        return new { ok = false, target, error = "连接超时（" + options.connectTimeoutMs + " ms）" };
                    }
                    client.EndConnect(ar);
                }
                return new { ok = true, target, note = "连接成功（已立即断开，不影响正式连接）" };
            }
            catch (Exception ex)
            {
                return new { ok = false, target, error = ex.Message };
            }
        }

        public override void Dispose()
        {
            CloseConnection();
            base.Dispose();
        }
    }
}
