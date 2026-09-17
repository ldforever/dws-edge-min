using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>B4 下游输出配置（存 runtime\config\downstream.json）。</summary>
    public sealed class DownstreamOptions
    {
        /// <summary>是否启用输出；关掉后不再往 TCP 发（待发队列保留，打开后继续）。</summary>
        public bool enabled { get; set; }

        /// <summary>目前只支持 tcp-client（B5 的 TCP 服务端、B6 的 HTTP 另做）。</summary>
        public string protocol { get; set; } = "tcp-client";

        public string host { get; set; } = "127.0.0.1";
        public int port { get; set; } = 9000;

        /// <summary>数据格式模板，例：{code}|{time}|{camera}|{weight}|{volume}|{traceId}\r\n</summary>
        public string template { get; set; } = "{code}|{time}|{camera}|{weight}|{volume}|{traceId}\\r\\n";

        /// <summary>报文编码：utf-8 / gbk / ascii。</summary>
        public string encoding { get; set; } = "utf-8";

        /// <summary>TCP 连接超时（毫秒）。</summary>
        public int connectTimeoutMs { get; set; } = 3000;

        /// <summary>连不上/发失败后的重试间隔（毫秒）。</summary>
        public int retryIntervalMs { get; set; } = 5000;

        /// <summary>0 = 一直重试（推荐，保证"不丢"）。</summary>
        public int maxAttempts { get; set; }

        /// <summary>分阶段 provider：等重量体积到齐（complete=true）才发。</summary>
        public bool sendOnlyComplete { get; set; } = true;

        /// <summary>批量发送时每条之间的间隔（毫秒），给下游留处理时间。</summary>
        public int sendIntervalMs { get; set; }

        public DownstreamOptions Clone()
        {
            return (DownstreamOptions)MemberwiseClone();
        }
    }

    /// <summary>一条下发记录（给界面看"发了什么、成功没有"）。</summary>
    public sealed class DispatchLogItem
    {
        public string time { get; set; }
        public string traceId { get; set; }
        public bool success { get; set; }
        public int attempt { get; set; }
        public int bytes { get; set; }
        public string message { get; set; }
        public string payload { get; set; }
        public string error { get; set; }
    }

    /// <summary>B4 运行状态。</summary>
    public sealed class DownstreamStats
    {
        public bool enabled { get; set; }
        public string target { get; set; }
        public bool connected { get; set; }
        public string connectedSince { get; set; }
        public long sent { get; set; }
        public long failed { get; set; }
        public long retries { get; set; }
        public long bytesSent { get; set; }
        public int queueDepth { get; set; }
        public string lastSentAt { get; set; }
        public string lastError { get; set; }
        public List<string> templateProblems { get; set; } = new List<string>();
    }

    /// <summary>
    /// 下游输出配置的文件存储（和条码规则一样：自动备份、热加载）。
    /// 文件：runtime\config\downstream.json
    /// </summary>
    public sealed class DownstreamStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly ILogger<DownstreamStore> _logger;
        private readonly object _sync = new object();
        private DownstreamOptions _current = new DownstreamOptions();
        private DateTime _loadedAtUtc = DateTime.MinValue;
        private DateTime _lastCheckUtc = DateTime.MinValue;

        public DownstreamStore(IConfiguration config, ILogger<DownstreamStore> logger)
        {
            _logger = logger;
            string root = config["Runtime:Root"] ?? "..";
            string runtimeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, root));
            FilePath = Path.Combine(runtimeRoot, @"config\downstream.json");
            EnsureDefaultFile();
            Reload();
        }

        public string FilePath { get; private set; }

        /// <summary>配置变化时通知发送服务重新连接（比如改了地址）。</summary>
        public Action OnChanged { get; set; }

        public DownstreamOptions Current
        {
            get
            {
                EnsureFresh();
                lock (_sync)
                {
                    return _current;
                }
            }
        }

        public string Save(DownstreamOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            List<string> problems = Validate(options);
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", problems.ToArray()));
            }

            string backup = null;
            string dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            lock (_sync)
            {
                if (File.Exists(FilePath))
                {
                    backup = FilePath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(FilePath, backup, true);
                }

                File.WriteAllText(FilePath, JsonSerializer.Serialize(options, JsonOptions), new UTF8Encoding(false));
                Apply(options);
                _loadedAtUtc = File.GetLastWriteTimeUtc(FilePath);
            }

            _logger.LogInformation("下游输出配置已保存：{0}:{1}（启用={2}）→ {3}",
                options.host, options.port, options.enabled, FilePath);
            if (OnChanged != null)
            {
                OnChanged();
            }
            return backup;
        }

        public List<string> Validate(DownstreamOptions options)
        {
            List<string> problems = new List<string>();
            if (options == null)
            {
                problems.Add("配置为空");
                return problems;
            }

            if (!string.Equals(options.protocol, "tcp-client", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("protocol 目前只支持 tcp-client");
            }
            if (string.IsNullOrEmpty(options.host))
            {
                problems.Add("host 不能为空");
            }
            if (options.port < 1 || options.port > 65535)
            {
                problems.Add("port 必须在 1-65535 之间");
            }
            if (options.connectTimeoutMs < 100 || options.connectTimeoutMs > 60000)
            {
                problems.Add("connectTimeoutMs 建议在 100-60000 之间");
            }
            if (options.retryIntervalMs < 200 || options.retryIntervalMs > 3600000)
            {
                problems.Add("retryIntervalMs 建议在 200-3600000 之间");
            }
            if (options.maxAttempts < 0)
            {
                problems.Add("maxAttempts 不能为负（0 = 一直重试）");
            }
            if (!IsKnownEncoding(options.encoding))
            {
                problems.Add("encoding 只支持 utf-8 / gbk / ascii");
            }

            problems.AddRange(MessageTemplate.Validate(options.template));
            return problems;
        }

        private static bool IsKnownEncoding(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return true;
            }
            string value = name.Trim().ToLowerInvariant();
            return value == "utf-8" || value == "utf8" || value == "gbk" || value == "gb2312" || value == "ascii";
        }

        /// <summary>把编码名转成 Encoding 实例。</summary>
        public static Encoding ResolveEncoding(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return new UTF8Encoding(false);
            }

            switch (name.Trim().ToLowerInvariant())
            {
                case "gbk":
                case "gb2312":
                    try
                    {
                        return Encoding.GetEncoding("GB2312");
                    }
                    catch (Exception)
                    {
                        return new UTF8Encoding(false);
                    }
                case "ascii":
                    return Encoding.ASCII;
                default:
                    return new UTF8Encoding(false);
            }
        }

        private void EnsureFresh()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastCheckUtc).TotalMilliseconds < 1000)
            {
                return;
            }
            _lastCheckUtc = now;

            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }
                if (File.GetLastWriteTimeUtc(FilePath) == _loadedAtUtc)
                {
                    return;
                }

                Reload();
                _logger.LogInformation("下游输出配置已变化，已重新加载：{0}", FilePath);
                if (OnChanged != null)
                {
                    OnChanged();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("检查下游输出配置失败：{0}", ex.Message);
            }
        }

        private void Reload()
        {
            DownstreamOptions loaded = null;
            try
            {
                if (File.Exists(FilePath))
                {
                    string text = File.ReadAllText(FilePath, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        loaded = JsonSerializer.Deserialize<DownstreamOptions>(text, JsonOptions);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("下游输出配置解析失败，沿用上一份：{0}", ex.Message);
            }

            if (loaded == null)
            {
                loaded = new DownstreamOptions();
            }

            lock (_sync)
            {
                Apply(loaded);
                _loadedAtUtc = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
            }
        }

        private void Apply(DownstreamOptions options)
        {
            _current = options;
            List<string> problems = MessageTemplate.Validate(options.template);
            if (problems.Count > 0)
            {
                _logger.LogWarning("下游报文模板有问题：{0}", string.Join("；", problems.ToArray()));
            }
        }

        /// <summary>第一次运行时生成一份默认配置（默认不启用，避免误发）。</summary>
        private void EnsureDefaultFile()
        {
            if (File.Exists(FilePath))
            {
                return;
            }

            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(FilePath, JsonSerializer.Serialize(new DownstreamOptions(), JsonOptions),
                    new UTF8Encoding(false));
                _logger.LogInformation("已生成下游输出配置模板（默认未启用）：{0}", FilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("生成下游输出配置失败：{0}", ex.Message);
            }
        }
    }
}
