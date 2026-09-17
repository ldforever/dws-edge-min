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

        // ---- B5：服务端模式的监听与客户端 ----

        /// <summary>一个已连接的下游客户端。</summary>
        private sealed class ClientConn
        {
            public string id;
            public string remote;
            public TcpClient client;
            public NetworkStream stream;
            public DateTime connectedAtUtc = DateTime.UtcNow;
            public long sent;
            public long bytes;
            public string lastError;
        }

        private readonly List<ClientConn> _clients = new List<ClientConn>();
        private TcpListener _listener;
        private string _listenTarget;
        private long _clientSeq;

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
                        CloseAllClients();
                        delayMs = 1000;
                    }
                    else if (DownstreamOptions.IsServerMode(options))
                    {
                        // B5：服务端模式 —— 先保证在监听，然后广播给所有已连接客户端
                        EnsureListening(options);
                        delayMs = SendServerRound(options);
                    }
                    else if (DownstreamOptions.IsHttpMode(options))
                    {
                        // B6：HTTP 推送模式
                        delayMs = SendHttpRound(options);
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

        #region B6：HTTP 推送模式

        private System.Net.Http.HttpClient _http;
        private string _httpConfigKey;

        /// <summary>按需创建/复用 HttpClient（地址、超时、请求头变了就重建）。</summary>
        private System.Net.Http.HttpClient EnsureHttpClient(DownstreamOptions options)
        {
            string key = options.url + "|" + options.httpTimeoutMs + "|" + options.contentType + "|"
                + string.Join(",", (options.headers ?? new List<string>()).ToArray());
            if (_http != null && string.Equals(_httpConfigKey, key, StringComparison.Ordinal))
            {
                return _http;
            }

            CloseHttpClient();
            System.Net.Http.HttpClientHandler handler = new System.Net.Http.HttpClientHandler();
            handler.AllowAutoRedirect = false;
            handler.UseProxy = false;

            System.Net.Http.HttpClient client = new System.Net.Http.HttpClient(handler);
            client.Timeout = TimeSpan.FromMilliseconds(Math.Max(100, options.httpTimeoutMs));
            _http = client;
            _httpConfigKey = key;
            return client;
        }

        private void CloseHttpClient()
        {
            try
            {
                if (_http != null)
                {
                    _http.Dispose();
                }
            }
            catch (Exception)
            {
            }
            _http = null;
            _httpConfigKey = null;
        }

        /// <summary>
        /// 跑一轮 HTTP 推送：每条包裹一个 POST，请求头带幂等键（值 = traceId）。
        /// 2xx = 成功（ack sent）；其他状态码/超时/网络错误 = 失败（ack failed，记录状态码与响应片段，等下一轮重试）。
        /// </summary>
        private int SendHttpRound(DownstreamOptions options)
        {
            List<ParcelRecord> pending = _store.PendingDispatch(20);
            if (pending.Count == 0)
            {
                return 500;
            }

            System.Net.Http.HttpClient client;
            try
            {
                client = EnsureHttpClient(options);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return Math.Max(500, options.retryIntervalMs);
            }

            foreach (ParcelRecord record in pending)
            {
                if (record.complete != true && options.sendOnlyComplete)
                {
                    continue;
                }
                if (options.maxAttempts > 0 && record.dispatchAttempts >= options.maxAttempts)
                {
                    continue;
                }

                string payload = MessageTemplate.Render(options.template, record);
                string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                int status = 0;
                string bodySnippet = null;
                string error = null;

                try
                {
                    System.Net.Http.HttpRequestMessage request =
                        new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, options.url);

                    // 幂等键：下游按它去重，所以重试不会产生重复业务
                    request.Headers.TryAddWithoutValidation(options.idempotencyHeader, record.traceId ?? string.Empty);
                    AddCustomHeaders(request, options);

                    // 注意：StringContent 的第三个参数只能是 media type（"text/plain"），
                    // 带上 "; charset=utf-8" 会直接抛 "The format of value ... is invalid"。
                    // charset 由 StringContent 按 encoding 自动补上。
                    string mediaType = options.contentType ?? "text/plain";
                    int semicolon = mediaType.IndexOf(';');
                    if (semicolon > 0)
                    {
                        mediaType = mediaType.Substring(0, semicolon).Trim();
                    }
                    if (mediaType.Length == 0)
                    {
                        mediaType = "text/plain";
                    }

                    request.Content = new System.Net.Http.StringContent(payload,
                        DownstreamStore.ResolveEncoding(options.encoding), mediaType);

                    System.Net.Http.HttpResponseMessage response = client.SendAsync(request).GetAwaiter().GetResult();
                    status = (int)response.StatusCode;
                    bodySnippet = ReadSnippet(response);

                    if (!DownstreamStore.IsHttpSuccess(status))
                    {
                        error = "HTTP " + status + (string.IsNullOrEmpty(bodySnippet) ? "" : "：" + bodySnippet);
                    }
                    response.Dispose();
                }
                catch (Exception ex)
                {
                    error = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                }

                if (error == null)
                {
                    _store.AckDispatch(record.traceId, true, null);
                    _sent++;
                    _bytesSent += DownstreamStore.ResolveEncoding(options.encoding).GetByteCount(payload);
                    _lastSentAt = time;
                    _lastError = null;
                    AddLog(new DispatchLogItem
                    {
                        time = time,
                        traceId = record.traceId,
                        success = true,
                        attempt = record.dispatchAttempts + 1,
                        bytes = DownstreamStore.ResolveEncoding(options.encoding).GetByteCount(payload),
                        payload = Trim(payload),
                        message = "HTTP " + status + " 推送成功（幂等键 " + record.traceId + "）"
                    });
                    _logger.LogInformation("HTTP 推送成功：{0} → {1}（{2}）", record.traceId, options.url, status);
                }
                else
                {
                    _failed++;
                    _retries++;
                    _lastError = error;
                    _store.AckDispatch(record.traceId, false, error);
                    AddLog(new DispatchLogItem
                    {
                        time = time,
                        traceId = record.traceId,
                        success = false,
                        attempt = record.dispatchAttempts + 1,
                        bytes = DownstreamStore.ResolveEncoding(options.encoding).GetByteCount(payload),
                        payload = Trim(payload),
                        message = "HTTP 推送失败，等待重试",
                        error = error
                    });
                    _logger.LogWarning("HTTP 推送失败：{0} → {1}；{2}", record.traceId, options.url, error);
                    return Math.Max(500, options.retryIntervalMs);
                }
            }

            return 200;
        }

        private static void AddCustomHeaders(System.Net.Http.HttpRequestMessage request, DownstreamOptions options)
        {
            if (options.headers == null)
            {
                return;
            }

            for (int i = 0; i < options.headers.Count; i++)
            {
                string line = options.headers[i];
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }
                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        private static string ReadSnippet(System.Net.Http.HttpResponseMessage response)
        {
            try
            {
                string text = response.Content == null
                    ? null
                    : response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(text))
                {
                    return null;
                }
                text = text.Replace("\r", " ").Replace("\n", " ");
                return text.Length <= 120 ? text : text.Substring(0, 120) + "…";
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region B5：TCP 服务端模式

        /// <summary>保证监听已启动（地址/端口变了会重建监听）。</summary>
        private void EnsureListening(DownstreamOptions options)
        {
            string target = options.host + ":" + options.port.ToString(CultureInfo.InvariantCulture);
            if (_listener != null && string.Equals(_listenTarget, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CloseListener();
            try
            {
                System.Net.IPAddress address;
                string host = string.IsNullOrEmpty(options.host) ? "0.0.0.0" : options.host.Trim();
                if (host == "0.0.0.0" || host == "*" || host == "+")
                {
                    address = System.Net.IPAddress.Any;
                }
                else if (!System.Net.IPAddress.TryParse(host, out address))
                {
                    throw new InvalidOperationException("服务端模式的 host 必须是本机可绑定的 IP（如 0.0.0.0 / 192.168.1.10）");
                }

                TcpListener listener = new TcpListener(address, options.port);
                listener.Start();
                _listener = listener;
                _listenTarget = target;
                _lastError = null;
                _logger.LogInformation("下游 TCP 服务端已开始监听：{0}（等待下游接入）", target);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogWarning("启动下游 TCP 服务端监听失败（{0}）：{1}", target, ex.Message);
                CloseListener();
            }
        }

        /// <summary>接收新接入的下游客户端（非阻塞：只取已经排队的连接）。</summary>
        private void AcceptPendingClients(DownstreamOptions options)
        {
            if (_listener == null)
            {
                return;
            }

            while (true)
            {
                try
                {
                    if (!_listener.Pending())
                    {
                        return;
                    }

                    TcpClient client = _listener.AcceptTcpClient();
                    client.NoDelay = true;
                    ClientConn conn = new ClientConn();
                    conn.id = "C" + Interlocked.Increment(ref _clientSeq);
                    conn.client = client;
                    conn.stream = client.GetStream();
                    conn.remote = client.Client.RemoteEndPoint == null ? "?" : client.Client.RemoteEndPoint.ToString();
                    _clients.Add(conn);

                    _logger.LogInformation("下游客户端已接入：{0}（{1}），当前 {2} 个", conn.id, conn.remote, _clients.Count);
                    AddLog(new DispatchLogItem
                    {
                        time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        success = true,
                        message = "客户端接入 " + conn.id + "（" + conn.remote + "）"
                    });

                    if (options.replayRecentCount > 0)
                    {
                        ReplayToClient(conn, options);
                    }
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    _logger.LogWarning("接收下游客户端失败：{0}", ex.Message);
                    return;
                }
            }
        }

        /// <summary>给新接入的客户端补发最近 N 条（下游重启/断线后能补上数据）。</summary>
        private void ReplayToClient(ClientConn conn, DownstreamOptions options)
        {
            try
            {
                List<ParcelRecord> recent = _store.LatestParcels(options.replayRecentCount);
                if (recent.Count == 0)
                {
                    return;
                }

                int oks = 0;
                for (int i = recent.Count - 1; i >= 0; i--)   // 从旧到新补发
                {
                    string payload = MessageTemplate.Render(options.template, recent[i]);
                    byte[] bytes = DownstreamStore.ResolveEncoding(options.encoding).GetBytes(payload);
                    if (WriteToClient(conn, bytes))
                    {
                        oks++;
                    }
                    else
                    {
                        break;
                    }
                }

                _logger.LogInformation("已给客户端 {0} 补发最近 {1} 条", conn.id, oks);
                AddLog(new DispatchLogItem
                {
                    time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    success = true,
                    message = "给 " + conn.id + " 补发最近 " + oks + " 条"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("补发失败：{0}", ex.Message);
            }
        }

        /// <summary>写一个客户端；失败就把这个客户端踢掉（不影响其他客户端和采集）。</summary>
        private bool WriteToClient(ClientConn conn, byte[] bytes)
        {
            try
            {
                if (conn.stream == null || conn.client == null || !conn.client.Connected)
                {
                    return false;
                }

                // 先看对端是否已经关了（FIN）—— 否则写进去是黑洞，会被误判成成功
                System.Net.Sockets.Socket socket = conn.client.Client;
                if (socket != null && socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                {
                    return false;
                }

                conn.stream.Write(bytes, 0, bytes.Length);
                conn.stream.Flush();
                conn.sent++;
                conn.bytes += bytes.Length;
                return true;
            }
            catch (Exception ex)
            {
                conn.lastError = ex.Message;
                return false;
            }
        }

        /// <summary>广播一轮：把待下发的包裹发给所有在线客户端。</summary>
        private int SendServerRound(DownstreamOptions options)
        {
            AcceptPendingClients(options);
            DropDeadClients();

            List<ParcelRecord> pending = _store.PendingDispatch(50);
            if (pending.Count == 0)
            {
                return 300;
            }
            if (_clients.Count == 0)
            {
                // 没有客户端接入：包裹留在队列里等，不算失败（这就是"不丢"）
                return 500;
            }

            foreach (ParcelRecord record in pending)
            {
                if (record.complete != true && options.sendOnlyComplete)
                {
                    continue;
                }
                if (options.maxAttempts > 0 && record.dispatchAttempts >= options.maxAttempts)
                {
                    continue;
                }

                string payload = MessageTemplate.Render(options.template, record);
                byte[] bytes = DownstreamStore.ResolveEncoding(options.encoding).GetBytes(payload);

                int okCount = 0;
                List<ClientConn> snapshot = new List<ClientConn>(_clients);
                for (int i = 0; i < snapshot.Count; i++)
                {
                    if (WriteToClient(snapshot[i], bytes))
                    {
                        okCount++;
                    }
                }

                string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                if (okCount > 0)
                {
                    // 只要有一个客户端收到就算"已下发"（业务语义）；其余客户端靠 replay 补
                    _store.AckDispatch(record.traceId, true, null);
                    _sent++;
                    _bytesSent += bytes.Length;
                    _lastSentAt = time;
                    AddLog(new DispatchLogItem
                    {
                        time = time,
                        traceId = record.traceId,
                        success = true,
                        attempt = record.dispatchAttempts + 1,
                        bytes = bytes.Length,
                        payload = Trim(payload),
                        message = "已广播给 " + okCount + " 个客户端"
                    });
                }
                else
                {
                    _failed++;
                    _retries++;
                    _store.AckDispatch(record.traceId, false, "没有可用的下游客户端");
                    AddLog(new DispatchLogItem
                    {
                        time = time,
                        traceId = record.traceId,
                        success = false,
                        attempt = record.dispatchAttempts + 1,
                        bytes = bytes.Length,
                        payload = Trim(payload),
                        message = "广播失败（客户端都断了）",
                        error = "没有可用的下游客户端"
                    });
                }

                DropDeadClients();
                if (_clients.Count == 0)
                {
                    return 500;
                }
            }

            return 200;
        }

        /// <summary>清掉已经断开/出错的客户端（客户端断开不影响采集，也不影响其他客户端）。</summary>
        private void DropDeadClients()
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                ClientConn conn = _clients[i];
                bool alive = true;
                try
                {
                    if (conn.client == null || !conn.client.Connected)
                    {
                        alive = false;
                    }
                    else
                    {
                        System.Net.Sockets.Socket socket = conn.client.Client;
                        if (socket != null && socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                        {
                            alive = false;   // 对端已关闭
                        }
                    }
                }
                catch (Exception)
                {
                    alive = false;
                }

                if (!alive)
                {
                    RemoveClient(conn, "客户端断开");
                }
            }
        }

        private void RemoveClient(ClientConn conn, string reason)
        {
            try
            {
                _clients.Remove(conn);
                if (conn.stream != null)
                {
                    conn.stream.Dispose();
                }
                if (conn.client != null)
                {
                    conn.client.Close();
                }
            }
            catch (Exception)
            {
            }

            _logger.LogInformation("下游客户端已移除：{0}（{1}），剩余 {2} 个", conn.id, reason, _clients.Count);
            AddLog(new DispatchLogItem
            {
                time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                success = false,
                message = "客户端 " + conn.id + " 移除：" + reason + (string.IsNullOrEmpty(conn.lastError) ? "" : "（" + conn.lastError + "）")
            });
        }

        private void CloseListener()
        {
            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                }
            }
            catch (Exception)
            {
            }
            _listener = null;
            _listenTarget = null;
        }

        private void CloseAllClients()
        {
            List<ClientConn> snapshot = new List<ClientConn>(_clients);
            for (int i = 0; i < snapshot.Count; i++)
            {
                RemoveClient(snapshot[i], "停止输出");
            }
            CloseListener();
        }

        #endregion

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
            // 地址/编码/模式变了：断开客户端与监听，下次循环按新配置重建
            CloseConnection();
            CloseAllClients();
            CloseHttpClient();
        }

        private bool IsPortInUseBySelf(DownstreamOptions options)
        {
            string target = options.host + ":" + options.port.ToString(CultureInfo.InvariantCulture);
            return _listener != null && string.Equals(_listenTarget, target, StringComparison.OrdinalIgnoreCase);
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

            // B5：服务端模式的监听状态与客户端列表
            stats.serverMode = DownstreamOptions.IsServerMode(options);
            stats.httpMode = DownstreamOptions.IsHttpMode(options);
            stats.httpTarget = DownstreamOptions.IsHttpMode(options) ? options.url : null;
            stats.idempotencyHeader = options.idempotencyHeader;
            stats.listening = _listener != null;
            stats.listenTarget = _listenTarget;
            List<object> clients = new List<object>();
            for (int i = 0; i < _clients.Count; i++)
            {
                ClientConn conn = _clients[i];
                clients.Add(new
                {
                    id = conn.id,
                    remote = conn.remote,
                    connectedAt = conn.connectedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    sent = conn.sent,
                    bytes = conn.bytes,
                    lastError = conn.lastError
                });
            }
            stats.clients = clients;
            stats.clientCount = clients.Count;
            return stats;
        }

        /// <summary>测试连接（不发送数据），返回是否成功与错误信息。</summary>
        public object TestConnection(DownstreamOptions options)
        {
            // 服务端模式：能监听成功就算通过（顺便告诉现场当前监听到哪个地址）
            if (DownstreamOptions.IsServerMode(options))
            {
                try
                {
                    System.Net.IPAddress address;
                    string host = string.IsNullOrEmpty(options.host) ? "0.0.0.0" : options.host.Trim();
                    if (host == "0.0.0.0" || host == "*" || host == "+")
                    {
                        address = System.Net.IPAddress.Any;
                    }
                    else if (!System.Net.IPAddress.TryParse(host, out address))
                    {
                        return new { ok = false, target = host + ":" + options.port, error = "host 必须是本机可绑定的 IP（如 0.0.0.0）" };
                    }

                    TcpListener probe = new TcpListener(address, options.port);
                    probe.Start();
                    probe.Stop();
                    return new
                    {
                        ok = true,
                        target = host + ":" + options.port,
                        note = "端口可用，服务端模式会在这里等待下游接入" +
                               (IsPortInUseBySelf(options) ? "（注意：当前已在监听同一个端口）" : "")
                    };
                }
                catch (Exception ex)
                {
                    return new
                    {
                        ok = false,
                        target = options.host + ":" + options.port,
                        error = "端口不可用：" + ex.Message + (IsPortInUseBySelf(options) ? "（本服务已经在监听这个端口，属正常）" : "")
                    };
                }
            }

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
            CloseAllClients();
            CloseHttpClient();
            base.Dispose();
        }
    }
}
