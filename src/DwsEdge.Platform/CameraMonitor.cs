using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DwsEdge.Core.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>B8 监控配置（runtime\config\monitor.json，支持热加载）。</summary>
    public sealed class MonitorOptions
    {
        public bool enabled { get; set; } = true;

        /// <summary>多少秒没收到任何数据（状态事件或出码）就算心跳超时。</summary>
        public int heartbeatTimeoutSeconds { get; set; } = 60;

        /// <summary>离线持续多少秒才告警（避免闪断刷屏）。</summary>
        public int offlineAlertSeconds { get; set; } = 10;

        /// <summary>窗口内掉线次数达到多少算"频繁掉线"。</summary>
        public int frequentOfflineCount { get; set; } = 3;
        public int frequentOfflineWindowMinutes { get; set; } = 30;

        /// <summary>在线率低于这个百分比就告警。</summary>
        public int onlineRateAlertPercent { get; set; } = 95;

        /// <summary>在线率统计窗口（分钟）；小于等于 0 表示从首次见到开始算。</summary>
        public int onlineRateWindowMinutes { get; set; } = 60;

        /// <summary>后台检查间隔（秒）。</summary>
        public int checkIntervalSeconds { get; set; } = 5;

        /// <summary>告警/事件日志保留天数。</summary>
        public int eventRetentionDays { get; set; } = 30;
    }

    /// <summary>一条相机事件（掉线/上线/告警产生/告警恢复）——"掉线记录可查"就是查它。</summary>
    public sealed class CameraEventRecord
    {
        public string time { get; set; }
        public long atMs { get; set; }
        public string camera { get; set; }
        /// <summary>offline / online / alert-raised / alert-cleared</summary>
        public string @event { get; set; }
        public string detail { get; set; }
        /// <summary>本次离线时长（毫秒），只有 online 事件才有。</summary>
        public long offlineDurationMs { get; set; }
        public string code { get; set; }
        public string severity { get; set; }
    }

    /// <summary>一条告警。</summary>
    public sealed class CameraAlert
    {
        public string id { get; set; }
        public string camera { get; set; }
        /// <summary>camera-offline / heartbeat-timeout / frequent-offline / low-online-rate / declared-missing</summary>
        public string code { get; set; }
        /// <summary>warning / critical</summary>
        public string severity { get; set; }
        public string message { get; set; }
        public bool active { get; set; }
        public string raisedAt { get; set; }
        public long raisedAtMs { get; set; }
        public string clearedAt { get; set; }
        public long clearedAtMs { get; set; }
    }

    /// <summary>
    /// B8 相机状态监控：在线率、掉线记录、心跳、告警。
    ///
    /// 数据来源就是采集宿主已经在发的相机状态事件（A2/A9），平台侧做统计与判定：
    ///   * 每次上下线都记一条事件（写 data\camera-events-yyyyMMdd.jsonl，重启后还能查）；
    ///   * 累计在线/离线时长 → 在线率；记录最近一次"心跳"（状态事件或出码）；
    ///   * 后台每隔几秒检查一次，命中阈值就产生告警，恢复后自动消除（产生/恢复都记事件并推给界面）。
    ///
    /// 判定的四类告警：
    ///   camera-offline      相机离线超过 offlineAlertSeconds
    ///   heartbeat-timeout   超过 heartbeatTimeoutSeconds 没有任何数据（掉线不报事件的相机靠这个兜）
    ///   frequent-offline    窗口内掉线次数达到 frequentOfflineCount
    ///   low-online-rate     在线率低于 onlineRateAlertPercent
    /// </summary>
    public sealed class CameraMonitor
    {
        private const int EventCapacity = 500;
        private const int AlertCapacity = 500;

        private sealed class CameraState
        {
            public string id;
            public bool online;
            public long firstSeenMs;
            public long stateSinceMs;
            public long lastHeartbeatMs;
            public long lastCodeMs;
            /// <summary>最近一次"变成离线"的时间（恢复时用它算本次离线时长）。</summary>
            public long lastOfflineAtMs;
            /// <summary>平台重启后从磁盘恢复这一台的时间：重启前的时间不参与告警判定（避免一启动就误报）。</summary>
            public long restoredAtMs;
            public long closedOnlineMs;
            public long closedOfflineMs;
            public readonly List<long> offlineTimes = new List<long>();
        }

        private readonly ILogger<CameraMonitor> _logger;
        private readonly object _sync = new object();
        private readonly Dictionary<string, CameraState> _cameras =
            new Dictionary<string, CameraState>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<CameraEventRecord> _events = new LinkedList<CameraEventRecord>();
        private readonly List<CameraAlert> _alerts = new List<CameraAlert>();

        private MonitorOptions _options = new MonitorOptions();
        private string _configPath;
        private DateTime _loadedAtUtc = DateTime.MinValue;
        private DateTime _lastCheckUtc = DateTime.MinValue;
        private long _alertSeq;

        /// <summary>告警产生/恢复时通知外部（平台用它推 SSE 给界面）。</summary>
        public Action<CameraAlert> OnAlertChanged { get; set; }

        /// <summary>可选：provider 报过"声明了但没发现"的相机（A9），也拿来告警。</summary>
        public Action<string, bool, string> OnLog { get; set; }

        public CameraMonitor(IConfiguration config, ILogger<CameraMonitor> logger)
        {
            _logger = logger;
            string root = config["Runtime:Root"] ?? "..";
            string runtimeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, root));
            _configPath = Path.Combine(runtimeRoot, @"config\monitor.json");
            DataDirectory = Path.Combine(runtimeRoot, "data");
            System.IO.Directory.CreateDirectory(DataDirectory);

            EnsureDefaultConfig();
            ReloadConfig();
            LoadRecentEvents();
        }

        public string DataDirectory { get; private set; }

        /// <summary>监控配置文件路径（runtime\config\monitor.json），界面上要显示给现场。</summary>
        public string ConfigFilePath
        {
            get { return _configPath; }
        }

        public MonitorOptions Options
        {
            get { EnsureConfigFresh(); lock (_sync) { return _options; } }
        }

        #region 配置

        public string SaveOptions(MonitorOptions options)
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
            if (File.Exists(_configPath))
            {
                backup = _configPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(_configPath, backup, true);
            }
            File.WriteAllText(_configPath, JsonSerializer.Serialize(options, JsonOptions), new UTF8Encoding(false));
            lock (_sync)
            {
                _options = options;
                _loadedAtUtc = File.GetLastWriteTimeUtc(_configPath);
            }
            _logger.LogInformation("监控配置已保存：{0}", _configPath);
            return backup;
        }

        public List<string> Validate(MonitorOptions options)
        {
            List<string> problems = new List<string>();
            if (options.heartbeatTimeoutSeconds < 5 || options.heartbeatTimeoutSeconds > 86400)
            {
                problems.Add("heartbeatTimeoutSeconds 建议在 5-86400 之间");
            }
            if (options.offlineAlertSeconds < 0 || options.offlineAlertSeconds > 86400)
            {
                problems.Add("offlineAlertSeconds 不能为负");
            }
            if (options.frequentOfflineCount < 1)
            {
                problems.Add("frequentOfflineCount 至少为 1");
            }
            if (options.onlineRateAlertPercent < 0 || options.onlineRateAlertPercent > 100)
            {
                problems.Add("onlineRateAlertPercent 必须在 0-100 之间");
            }
            if (options.checkIntervalSeconds < 1 || options.checkIntervalSeconds > 3600)
            {
                problems.Add("checkIntervalSeconds 建议在 1-3600 之间");
            }
            return problems;
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// 事件文件（jsonl）必须"一条一行"：JsonOptions 是给人看的缩进格式，
        /// 用它写出来的文件每行只是半截 JSON，重启后按行读会全部解析失败（相机状态、掉线记录全丢）。
        /// </summary>
        private static readonly JsonSerializerOptions LineJson = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        private void EnsureDefaultConfig()
        {
            if (File.Exists(_configPath))
            {
                return;
            }
            try
            {
                string dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(_configPath, JsonSerializer.Serialize(new MonitorOptions(), JsonOptions),
                    new UTF8Encoding(false));
                _logger.LogInformation("已生成监控配置模板：{0}", _configPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("生成监控配置失败：{0}", ex.Message);
            }
        }

        private void ReloadConfig()
        {
            MonitorOptions loaded = null;
            try
            {
                if (File.Exists(_configPath))
                {
                    string text = File.ReadAllText(_configPath, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        loaded = JsonSerializer.Deserialize<MonitorOptions>(text, JsonOptions);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("监控配置解析失败，沿用上一份：{0}", ex.Message);
            }
            if (loaded == null)
            {
                loaded = new MonitorOptions();
            }
            lock (_sync)
            {
                _options = loaded;
                _loadedAtUtc = File.Exists(_configPath) ? File.GetLastWriteTimeUtc(_configPath) : DateTime.MinValue;
            }
        }

        private void EnsureConfigFresh()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastCheckUtc).TotalMilliseconds < 1000)
            {
                return;
            }
            _lastCheckUtc = now;
            try
            {
                if (File.Exists(_configPath) && File.GetLastWriteTimeUtc(_configPath) != _loadedAtUtc)
                {
                    ReloadConfig();
                    _logger.LogInformation("监控配置已变化，已重新加载：{0}", _configPath);
                }
            }
            catch (Exception)
            {
            }
        }

        #endregion

        #region 事件接入

        /// <summary>采集宿主上报的相机状态（快照或上下线）。replay=true 表示平台重启后的回放，只补状态不产生告警。</summary>
        public void ApplyStatus(string cameraId, bool online, long atMs, bool isSnapshot, bool replay,
            string declaredLabel, string position, bool discovered)
        {
            if (string.IsNullOrEmpty(cameraId))
            {
                return;
            }
            if (atMs <= 0)
            {
                atMs = NowMs();
            }

            CameraAlert raised = null;
            string offlineDurationText = null;
            long offlineDurationMs = 0;

            lock (_sync)
            {
                CameraState state;
                if (!_cameras.TryGetValue(cameraId, out state))
                {
                    state = new CameraState();
                    state.id = cameraId;
                    state.online = online;
                    state.firstSeenMs = atMs;
                    state.stateSinceMs = atMs;
                    _cameras[cameraId] = state;
                }
                else if (replay && atMs < state.lastHeartbeatMs)
                {
                    // 回放（平台重启后重读 spool）时可能拿到比"已知状态"更早的旧事件，
                    // 直接跳过：不能把已经知道的最新状态倒回过去（那会造出假在线/假离线）。
                    return;
                }
                else
                {
                    // 累计上一段的时长
                    long span = Math.Max(0, atMs - state.stateSinceMs);
                    if (state.online) { state.closedOnlineMs += span; } else { state.closedOfflineMs += span; }

                    if (state.online != online)
                    {
                        if (!online)
                        {
                            state.offlineTimes.Add(atMs);
                            TrimOfflineTimes(state, atMs);
                        }
                        else if (state.lastOfflineAtMs > 0)
                        {
                            offlineDurationMs = Math.Max(0, atMs - state.lastOfflineAtMs);
                            offlineDurationText = (offlineDurationMs / 1000.0).ToString("0.0") + " 秒";
                        }
                    }
                    state.online = online;
                    state.stateSinceMs = atMs;
                }

                state.lastHeartbeatMs = atMs;
                if (!online)
                {
                    state.lastOfflineAtMs = atMs;
                }

                if (!replay)
                {
                    AddEvent(new CameraEventRecord
                    {
                        atMs = atMs,
                        camera = cameraId,
                        @event = online ? "online" : "offline",
                        detail = (online ? "相机上线" : "相机离线")
                            + (string.IsNullOrEmpty(position) ? "" : "（方位 " + CameraPositions.Label(position) + "）")
                            + (string.IsNullOrEmpty(declaredLabel) ? "" : " " + declaredLabel),
                        offlineDurationMs = 0
                    });

                    if (!online && !discovered)
                    {
                        // 清单里声明了但 SDK 没发现：直接给一条告警（不用等超时）
                        raised = RaiseAlert(cameraId, "declared-missing", "critical",
                            "清单里声明了但 SDK 没发现（没上电/没接网/被占用）");
                    }
                    else if (online)
                    {
                        ClearAlert(cameraId, "declared-missing");
                        var cleared = ClearAlert(cameraId, "camera-offline");
                        if (cleared != null && OnAlertChanged != null) { OnAlertChanged(cleared); }
                        ClearAlert(cameraId, "heartbeat-timeout");
                        if (!isSnapshot)
                        {
                            AddEvent(new CameraEventRecord
                            {
                                atMs = atMs,
                                camera = cameraId,
                                @event = "recovered",
                                detail = "相机恢复" + (offlineDurationText == null ? "" : "（本次离线 " + offlineDurationText + "）"),
                                offlineDurationMs = offlineDurationMs
                            });
                        }
                    }
                }
            }

            if (raised != null && OnAlertChanged != null)
            {
                OnAlertChanged(raised);
            }
        }

        /// <summary>相机出码 = 最强的心跳。</summary>
        public void ApplyCode(string cameraId, long atMs)
        {
            if (string.IsNullOrEmpty(cameraId))
            {
                return;
            }
            if (atMs <= 0) { atMs = NowMs(); }

            lock (_sync)
            {
                CameraState state;
                if (!_cameras.TryGetValue(cameraId, out state))
                {
                    state = new CameraState();
                    state.id = cameraId;
                    state.firstSeenMs = atMs;
                    state.stateSinceMs = atMs;
                    state.online = true;
                    _cameras[cameraId] = state;
                }
                state.lastCodeMs = atMs;
                state.lastHeartbeatMs = Math.Max(state.lastHeartbeatMs, atMs);
            }
        }

        private static void TrimOfflineTimes(CameraState state, long nowMs)
        {
            const int maxKeep = 200;
            while (state.offlineTimes.Count > maxKeep)
            {
                state.offlineTimes.RemoveAt(0);
            }
        }

        #endregion

        #region 后台检查：心跳 / 离线时长 / 频繁掉线 / 在线率

        /// <summary>后台定时调用：按阈值判定告警。</summary>
        public void Tick()
        {
            List<CameraAlert> changed = new List<CameraAlert>();
            MonitorOptions options;
            lock (_sync) { options = _options; }
            if (!options.enabled)
            {
                return;
            }

            long now = NowMs();
            lock (_sync)
            {
                foreach (KeyValuePair<string, CameraState> pair in _cameras)
                {
                    CameraState state = pair.Value;

                    // 1) 离线超过阈值
                    if (!state.online)
                    {
                        // 重启前的时间不算：恢复出来的状态从"平台起来那一刻"开始计时
                        long offlineMs = now - Math.Max(state.stateSinceMs, state.restoredAtMs);
                        if (offlineMs >= options.offlineAlertSeconds * 1000L)
                        {
                            CameraAlert a = RaiseAlert(state.id, "camera-offline", "critical",
                                "相机离线 " + (offlineMs / 1000) + " 秒");
                            if (a != null) { changed.Add(a); }
                        }
                    }

                    // 2) 心跳超时（掉线不上报的相机靠这个兜）
                    long heartbeatBase = Math.Max(state.lastHeartbeatMs, state.restoredAtMs);
                    long heartbeatAge = now - heartbeatBase;
                    if (heartbeatBase > 0 && heartbeatAge >= options.heartbeatTimeoutSeconds * 1000L)
                    {
                        CameraAlert a = RaiseAlert(state.id, "heartbeat-timeout", "critical",
                            "已 " + (heartbeatAge / 1000) + " 秒没有收到任何数据（状态或出码）");
                        if (a != null) { changed.Add(a); }
                    }
                    else
                    {
                        CameraAlert c = ClearAlert(state.id, "heartbeat-timeout");
                        if (c != null) { changed.Add(c); }
                    }

                    // 3) 窗口内频繁掉线
                    if (options.frequentOfflineCount > 0)
                    {
                        long windowMs = Math.Max(1, options.frequentOfflineWindowMinutes) * 60000L;
                        int count = 0;
                        for (int i = state.offlineTimes.Count - 1; i >= 0; i--)
                        {
                            if (now - state.offlineTimes[i] <= windowMs) { count++; } else { break; }
                        }
                        if (count >= options.frequentOfflineCount)
                        {
                            CameraAlert a = RaiseAlert(state.id, "frequent-offline", "warning",
                                options.frequentOfflineWindowMinutes + " 分钟内掉线 " + count + " 次");
                            if (a != null) { changed.Add(a); }
                        }
                    }

                    // 4) 在线率过低
                    if (options.onlineRateAlertPercent > 0)
                    {
                        double rate = OnlineRate(state, now, options);
                        if (rate >= 0 && rate < options.onlineRateAlertPercent && now - state.firstSeenMs > 60000)
                        {
                            CameraAlert a = RaiseAlert(state.id, "low-online-rate", "warning",
                                "在线率 " + rate.ToString("0.0") + "%，低于阈值 " + options.onlineRateAlertPercent + "%");
                            if (a != null) { changed.Add(a); }
                        }
                        else if (rate >= options.onlineRateAlertPercent)
                        {
                            CameraAlert c = ClearAlert(state.id, "low-online-rate");
                            if (c != null) { changed.Add(c); }
                        }
                    }
                }
            }

            if (OnAlertChanged != null)
            {
                for (int i = 0; i < changed.Count; i++) { OnAlertChanged(changed[i]); }
            }
        }

        /// <summary>在线率（0-100）；数据不足时返回 -1。</summary>
        private static double OnlineRate(CameraState state, long nowMs, MonitorOptions options)
        {
            long from = state.firstSeenMs;
            if (options.onlineRateWindowMinutes > 0)
            {
                from = Math.Max(from, nowMs - options.onlineRateWindowMinutes * 60000L);
            }

            long onlineMs = state.closedOnlineMs;
            long offlineMs = state.closedOfflineMs;

            // 把"当前这一段"按窗口裁一下（重启前的时间不计入，见 restoredAtMs 的注释）
            long baseline = Math.Max(state.stateSinceMs, state.restoredAtMs);
            long segmentStart = Math.Max(baseline, from);
            long segment = Math.Max(0, nowMs - segmentStart);
            if (state.online) { onlineMs += segment; } else { offlineMs += segment; }

            // 窗口之前的累计值要按比例扣掉（近似：只保留窗口内的部分）
            if (options.onlineRateWindowMinutes > 0 && baseline < from)
            {
                long before = from - state.firstSeenMs;
                long total = onlineMs + offlineMs;
                if (total > 0 && before > 0)
                {
                    double keep = 1.0 - Math.Min(0.99, (double)before / total);
                    onlineMs = (long)(onlineMs * keep);
                    offlineMs = (long)(offlineMs * keep);
                }
            }

            long sum = onlineMs + offlineMs;
            if (sum <= 0)
            {
                return -1;
            }
            return Math.Round(100.0 * onlineMs / sum, 1);
        }

        private CameraAlert RaiseAlert(string camera, string code, string severity, string message)
        {
            for (int i = 0; i < _alerts.Count; i++)
            {
                CameraAlert item = _alerts[i];
                if (item.active && string.Equals(item.camera, camera, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.code, code, StringComparison.OrdinalIgnoreCase))
                {
                    return null;   // 已经在告警中，不重复产生
                }
            }

            CameraAlert alert = new CameraAlert();
            alert.id = "A" + (++_alertSeq).ToString("D4");
            alert.camera = camera;
            alert.code = code;
            alert.severity = severity;
            alert.message = message;
            alert.active = true;
            alert.raisedAtMs = NowMs();
            alert.raisedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _alerts.Add(alert);
            TrimAlerts();

            AddEvent(new CameraEventRecord
            {
                camera = camera,
                @event = "alert-raised",
                code = code,
                severity = severity,
                detail = message
            });
            _logger.LogWarning("相机告警：{0} {1} —— {2}", camera, code, message);
            if (OnLog != null) { OnLog(camera, true, "相机告警：" + code + " " + message); }
            return alert;
        }

        private CameraAlert ClearAlert(string camera, string code)
        {
            for (int i = 0; i < _alerts.Count; i++)
            {
                CameraAlert item = _alerts[i];
                if (item.active && string.Equals(item.camera, camera, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.code, code, StringComparison.OrdinalIgnoreCase))
                {
                    item.active = false;
                    item.clearedAtMs = NowMs();
                    item.clearedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    AddEvent(new CameraEventRecord
                    {
                        camera = camera,
                        @event = "alert-cleared",
                        code = code,
                        severity = item.severity,
                        detail = "告警恢复：" + item.message
                    });
                    _logger.LogInformation("相机告警恢复：{0} {1}", camera, code);
                    return item;
                }
            }
            return null;
        }

        private void TrimAlerts()
        {
            while (_alerts.Count > AlertCapacity)
            {
                _alerts.RemoveAt(0);
            }
        }

        #endregion

        #region 查询

        /// <summary>给界面用的每台相机监控指标。</summary>
        public List<object> Statuses()
        {
            MonitorOptions options = Options;
            long now = NowMs();
            List<object> list = new List<object>();
            lock (_sync)
            {
                foreach (KeyValuePair<string, CameraState> pair in _cameras)
                {
                    CameraState state = pair.Value;
                    double rate = OnlineRate(state, now, options);
                    long currentMs = Math.Max(0, now - state.stateSinceMs);
                    List<object> alerts = new List<object>();
                    for (int i = 0; i < _alerts.Count; i++)
                    {
                        if (_alerts[i].active
                            && string.Equals(_alerts[i].camera, state.id, StringComparison.OrdinalIgnoreCase))
                        {
                            alerts.Add(new { code = _alerts[i].code, severity = _alerts[i].severity, message = _alerts[i].message });
                        }
                    }

                    list.Add(new
                    {
                        camera = state.id,
                        online = state.online,
                        onlineRatePercent = rate,
                        onlineSeconds = (state.closedOnlineMs + (state.online ? currentMs : 0)) / 1000,
                        offlineSeconds = (state.closedOfflineMs + (state.online ? 0 : currentMs)) / 1000,
                        currentStateSeconds = currentMs / 1000,
                        lastHeartbeatAt = FormatMs(state.lastHeartbeatMs),
                        lastHeartbeatAgeSeconds = state.lastHeartbeatMs > 0 ? (now - state.lastHeartbeatMs) / 1000 : -1,
                        lastCodeAt = FormatMs(state.lastCodeMs),
                        offlineCount = state.offlineTimes.Count,
                        alertCount = alerts.Count,
                        alerts
                    });
                }
            }
            return list;
        }

        public object Summary()
        {
            MonitorOptions options = Options;
            long now = NowMs();
            lock (_sync)
            {
                int online = 0;
                double rateSum = 0;
                int rateCount = 0;
                foreach (KeyValuePair<string, CameraState> pair in _cameras)
                {
                    if (pair.Value.online) { online++; }
                    double rate = OnlineRate(pair.Value, now, options);
                    if (rate >= 0) { rateSum += rate; rateCount++; }
                }
                int active = 0;
                for (int i = 0; i < _alerts.Count; i++) { if (_alerts[i].active) { active++; } }

                return new
                {
                    cameras = _cameras.Count,
                    online,
                    offline = _cameras.Count - online,
                    averageOnlineRatePercent = rateCount > 0 ? Math.Round(rateSum / rateCount, 1) : -1,
                    activeAlerts = active,
                    events = _events.Count,
                    enabled = options.enabled,
                    heartbeatTimeoutSeconds = options.heartbeatTimeoutSeconds,
                    offlineAlertSeconds = options.offlineAlertSeconds,
                    configFile = _configPath
                };
            }
        }

        /// <summary>掉线/上下线/告警事件（"掉线记录可查"）。</summary>
        public List<CameraEventRecord> RecentEvents(int limit, string camera)
        {
            List<CameraEventRecord> result = new List<CameraEventRecord>();
            lock (_sync)
            {
                LinkedListNode<CameraEventRecord> node = _events.First;
                while (node != null && result.Count < limit)
                {
                    if (string.IsNullOrEmpty(camera)
                        || string.Equals(node.Value.camera, camera, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(node.Value);
                    }
                    node = node.Next;
                }
            }
            return result;
        }

        public object Alerts(int limit, bool onlyActive)
        {
            List<CameraAlert> result = new List<CameraAlert>();
            lock (_sync)
            {
                for (int i = _alerts.Count - 1; i >= 0 && result.Count < limit; i--)
                {
                    if (!onlyActive || _alerts[i].active)
                    {
                        result.Add(_alerts[i]);
                    }
                }
            }
            int active = 0;
            lock (_sync)
            {
                for (int i = 0; i < _alerts.Count; i++) { if (_alerts[i].active) { active++; } }
            }
            return new { activeCount = active, items = result };
        }

        private static string FormatMs(long ms)
        {
            if (ms <= 0) { return null; }
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        /// <summary>
        /// 一次性取"界面首屏 / 周期推送"需要的全部监控数据（汇总 + 每台指标 + 活动告警）。
        /// 采集宿主每推一次相机状态都单独补一份快照，界面就不需要为了刷新在线率再去轮询。
        /// </summary>
        public object Snapshot(int alertLimit)
        {
            List<CameraAlert> active = new List<CameraAlert>();
            lock (_sync)
            {
                for (int i = _alerts.Count - 1; i >= 0 && active.Count < alertLimit; i--)
                {
                    if (_alerts[i].active) { active.Add(_alerts[i]); }
                }
            }

            return new
            {
                summary = Summary(),
                cameras = Statuses(),
                alerts = active,
                serverTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        #endregion

        #region 事件落盘 / 恢复

        private void AddEvent(CameraEventRecord record)
        {
            if (record == null)
            {
                return;
            }
            if (record.atMs <= 0) { record.atMs = NowMs(); }
            if (string.IsNullOrEmpty(record.time))
            {
                record.time = DateTimeOffset.FromUnixTimeMilliseconds(record.atMs).ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss");
            }

            _events.AddFirst(record);
            while (_events.Count > EventCapacity)
            {
                _events.RemoveLast();
            }

            try
            {
                string path = Path.Combine(DataDirectory, "camera-events-" + DateTime.Now.ToString("yyyyMMdd") + ".jsonl");
                using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 1))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.WriteLine(JsonSerializer.Serialize(record, LineJson));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("写相机事件失败：{0}", ex.Message);
            }
        }

        /// <summary>平台重启后把最近几天的相机事件读回来，掉线记录不会因为重启而消失。</summary>
        private void LoadRecentEvents()
        {
            try
            {
                List<CameraEventRecord> all = new List<CameraEventRecord>();
                for (int i = 0; i < 3; i++)
                {
                    DateTime day = DateTime.Today.AddDays(-i);
                    string path = Path.Combine(DataDirectory, "camera-events-" + day.ToString("yyyyMMdd") + ".jsonl");
                    if (!File.Exists(path))
                    {
                        continue;
                    }
                    foreach (string line in ReadLinesShared(path))
                    {
                        if (string.IsNullOrWhiteSpace(line)) { continue; }
                        try
                        {
                            CameraEventRecord item = JsonSerializer.Deserialize<CameraEventRecord>(line, JsonOptions);
                            if (item != null && !string.IsNullOrEmpty(item.camera)) { all.Add(item); }
                        }
                        catch (JsonException) { }
                    }
                }

                if (all.Count == 0)
                {
                    return;
                }

                all.Sort(delegate(CameraEventRecord a, CameraEventRecord b) { return a.atMs.CompareTo(b.atMs); });
                lock (_sync)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        CameraEventRecord item = all[i];
                        CameraState state;
                        if (!_cameras.TryGetValue(item.camera, out state))
                        {
                            state = new CameraState();
                            state.id = item.camera;
                            state.firstSeenMs = item.atMs;
                            state.stateSinceMs = item.atMs;
                            _cameras[item.camera] = state;
                        }

                        // 把"上一段"的时长累计进来：在线率不会因为平台重启而清零
                        long span = Math.Max(0, item.atMs - state.stateSinceMs);
                        if (span > 0)
                        {
                            if (state.online) { state.closedOnlineMs += span; } else { state.closedOfflineMs += span; }
                        }

                        state.lastHeartbeatMs = Math.Max(state.lastHeartbeatMs, item.atMs);

                        if (string.Equals(item.@event, "offline", StringComparison.OrdinalIgnoreCase))
                        {
                            state.online = false;
                            state.stateSinceMs = item.atMs;
                            state.lastOfflineAtMs = item.atMs;
                            state.offlineTimes.Add(item.atMs);
                            TrimOfflineTimes(state, item.atMs);
                        }
                        else if (string.Equals(item.@event, "online", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(item.@event, "recovered", StringComparison.OrdinalIgnoreCase))
                        {
                            state.online = true;
                            state.stateSinceMs = item.atMs;
                        }
                        else if (string.Equals(item.@event, "alert-raised", StringComparison.OrdinalIgnoreCase))
                        {
                            CameraAlert alert = new CameraAlert();
                            alert.id = "A" + (++_alertSeq).ToString("D4");
                            alert.camera = item.camera;
                            alert.code = item.code;
                            alert.severity = item.severity;
                            alert.message = item.detail;
                            alert.active = true;
                            alert.raisedAt = item.time;
                            alert.raisedAtMs = item.atMs;
                            _alerts.Add(alert);
                        }
                        else if (string.Equals(item.@event, "alert-cleared", StringComparison.OrdinalIgnoreCase))
                        {
                            for (int k = _alerts.Count - 1; k >= 0; k--)
                            {
                                if (_alerts[k].active
                                    && string.Equals(_alerts[k].camera, item.camera, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(_alerts[k].code, item.code, StringComparison.OrdinalIgnoreCase))
                                {
                                    _alerts[k].active = false;
                                    _alerts[k].clearedAt = item.time;
                                    _alerts[k].clearedAtMs = item.atMs;
                                }
                            }
                        }
                    }

                    // 恢复出来的相机打上"平台启动时刻"：心跳超时/离线告警从这一刻起算，
                    // 否则平台重启后一启动就会拿磁盘里的旧时间戳报一堆假警。
                    long restoredAt = NowMs();
                    foreach (KeyValuePair<string, CameraState> pair in _cameras)
                    {
                        pair.Value.restoredAtMs = restoredAt;
                    }

                    for (int i = all.Count - 1; i >= 0 && _events.Count < EventCapacity; i--)
                    {
                        _events.AddLast(all[i]);
                    }
                    TrimAlerts();
                }

                _logger.LogInformation("已恢复相机事件 {0} 条 / 告警 {1} 条（最近 3 天）", all.Count, _alerts.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("恢复相机事件失败：{0}", ex.Message);
            }
        }

        private static IEnumerable<string> ReadLinesShared(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                while (!reader.EndOfStream)
                {
                    string line = reader.ReadLine();
                    if (line != null) { yield return line; }
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// B8 后台检查器：定时判定告警 + 定时把监控快照推给界面。
    ///
    /// 为什么判定必须"按时间"跑，而不是只在事件到达时算：
    ///   * 相机掉线可能压根不上报事件（拔网线、断电），只有"多久没数据"才能发现；
    ///   * 在线率、离线时长本身就是时间的函数；
    ///   * 界面上的"最近心跳 12 秒前"要自己走，不能等下一次事件才刷新。
    /// </summary>
    public sealed class MonitorWatcher : BackgroundService
    {
        private readonly CameraMonitor _monitor;
        private readonly SpoolStore _store;
        private readonly ILogger<MonitorWatcher> _logger;
        private long _lastSnapshotMs;

        public MonitorWatcher(CameraMonitor monitor, SpoolStore store, ILogger<MonitorWatcher> logger)
        {
            _monitor = monitor;
            _store = store;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            MonitorOptions options = _monitor.Options;
            _logger.LogInformation(
                "相机状态监控已启动：心跳超时 {0}s / 离线告警 {1}s / 频繁掉线 {2} 次{3} 分钟 / 在线率下限 {4}% / 检查间隔 {5}s",
                options.heartbeatTimeoutSeconds, options.offlineAlertSeconds, options.frequentOfflineCount,
                options.frequentOfflineWindowMinutes, options.onlineRateAlertPercent, options.checkIntervalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                int interval = Math.Max(1, _monitor.Options.checkIntervalSeconds);
                try
                {
                    _monitor.Tick();          // 判定告警（有变化会回调 OnAlertChanged → SSE）
                    PublishSnapshot(false);   // 常规快照：界面上的在线率/心跳自己走
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("相机监控检查失败：{0}", ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>把监控快照推给界面（SSE）。force=true 表示"给刚接入的页面补一份"，不受节流限制。</summary>
        public void PublishSnapshot(bool force)
        {
            if (_store == null)
            {
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!force && now - _lastSnapshotMs < 900)
            {
                return;
            }
            _lastSnapshotMs = now;
            _store.PublishMonitor(_monitor);
        }
    }
}
