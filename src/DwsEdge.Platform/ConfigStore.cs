using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using DwsEdge.Core.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>
    /// 一键应用配置的请求体（对应界面上的"配置"页）。
    /// </summary>
    public sealed class ConfigApplyRequest
    {
        /// <summary>触发模式：hard（光电硬触发）/ soft（软触发）/ free（自由拉流）；空 = 不改。</summary>
        public string triggerMode { get; set; }

        /// <summary>相机清单，每行一台：ip=172.20.10.11 或 key=序列号 或 id=厂商:序列号，可带 ,pos=top；null = 不改。</summary>
        public List<string> cameras { get; set; }

        /// <summary>只写配置、不做重启校验。</summary>
        public bool skipVerify { get; set; }

        /// <summary>校验前自动停止正在运行的采集宿主（校验要独占 SDK）。</summary>
        public bool stopHost { get; set; } = true;

        /// <summary>校验通过后自动拉起采集宿主。</summary>
        public bool restartHost { get; set; } = true;

        /// <summary>校验超时（秒），0 = 用脚本默认值。</summary>
        public int verifyTimeoutSeconds { get; set; }
    }

    /// <summary>一键应用的结果，回给界面直接展示。</summary>
    public sealed class ConfigApplyResult
    {
        public bool ok { get; set; }
        public int exitCode { get; set; }

        /// <summary>结论文案：配置已生效 / 已回滚 / 需人工检查 / 应用失败。</summary>
        public string conclusion { get; set; }

        public string output { get; set; }
        public long durationMs { get; set; }
        public string at { get; set; }
        public string commandLine { get; set; }
        public string error { get; set; }
    }

    /// <summary>
    /// 平台侧的"配置域"服务：
    ///   * 读当前 SDK 配置（触发模式、相机清单、存图策略）给界面显示；
    ///   * 调 tools\apply-config.ps1 做"一键应用 → 重启校验 → 失败回滚"（A8）；
    ///   * 读写相机方位映射（A9 设备信息页）。
    ///
    /// 这里刻意只调用现成的 PowerShell 脚本，不在 C# 里重写一遍配置逻辑：
    /// 命令行和界面走的是同一条路径，现场怎么修、界面就怎么改，不会出现两种行为。
    /// </summary>
    public sealed class ConfigStore
    {
        private static readonly string[] AllowedTriggerModes = { "hard", "soft", "free" };

        private readonly ILogger<ConfigStore> _logger;
        private readonly string _runtimeRoot;
        private readonly string _cfgPath;
        private readonly string _gatewayPath;
        private readonly string _positionsPath;
        private readonly string _applyScript;
        private readonly string _cameraListPath;
        private readonly string _powershell;
        private readonly int _timeoutSeconds;
        private readonly object _applySync = new object();

        private ConfigApplyResult _lastApply;

        public ConfigStore(IConfiguration config, ILogger<ConfigStore> logger)
        {
            _logger = logger;

            string root = config["Runtime:Root"] ?? "..";
            _runtimeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, root));
            _cfgPath = Path.Combine(_runtimeRoot, @"Cfg\LogisticsBase.cfg");
            _gatewayPath = Path.Combine(_runtimeRoot, @"config\gateway.ini");
            _positionsPath = Path.Combine(_runtimeRoot, CameraPositions.DefaultRelativePath);
            _cameraListPath = Path.Combine(_runtimeRoot, @"config\cameras-applied.txt");
            _powershell = config["Runtime:PowerShell"] ?? "powershell.exe";

            int timeout = 240;
            int.TryParse(config["Runtime:ConfigTimeoutSeconds"], out timeout);
            _timeoutSeconds = Math.Max(30, timeout);

            _applyScript = ResolveToolsScript(config["Runtime:Tools"], "apply-config.ps1");
        }

        /// <summary>相机方位映射文件路径（设备信息页用）。</summary>
        public string PositionsPath
        {
            get { return _positionsPath; }
        }

        public string RuntimeRoot
        {
            get { return _runtimeRoot; }
        }

        /// <summary>GET /api/config：把当前配置整理成界面能直接渲染的形状。</summary>
        public object Read()
        {
            CameraPlan plan = CameraPlan.Read(_cfgPath);
            CameraPositions positions = CameraPositions.Load(_positionsPath);

            List<object> cameras = new List<object>();
            for (int i = 0; i < plan.Cameras.Count; i++)
            {
                CameraPlanEntry entry = plan.Cameras[i];
                if (!entry.Enabled)
                {
                    continue;
                }

                string position = positions.Resolve(entry.Value);
                cameras.Add(new
                {
                    index = cameras.Count + 1,
                    kind = entry.Kind,
                    value = entry.Value,
                    line = entry.Kind + "=" + entry.Value + (string.IsNullOrEmpty(position) ? string.Empty : ",pos=" + position),
                    position,
                    positionLabel = CameraPositions.Label(position)
                });
            }

            // 触发模式：0=自由拉流 1=硬触发 2=软触发
            string triggerMode = plan.TriggerMode;
            string triggerName = triggerMode == "0" ? "free" : (triggerMode == "2" ? "soft" : (triggerMode == "1" ? "hard" : "unknown"));

            return new
            {
                runtimeRoot = _runtimeRoot,
                cfgPath = _cfgPath,
                cfgExists = File.Exists(_cfgPath),
                gatewayPath = _gatewayPath,
                provider = ReadGatewayProvider(),
                mode = plan.Mode,
                num = plan.NumValue,
                randWorkMode = plan.RandWorkMode,
                triggerMode,
                triggerName,
                enabledCameras = plan.EnabledCount,
                declaredCameras = plan.Cameras.Count,
                cameras,
                positionsFile = _positionsPath,
                positionsCount = positions.Count,
                toolsReady = !string.IsNullOrEmpty(_applyScript) && File.Exists(_applyScript),
                applyScript = _applyScript,
                cameraListFile = _cameraListPath,
                lastApply = _lastApply,
                problems = plan.Errors(),
                warnings = plan.Warnings()
            };
        }

        /// <summary>POST /api/config/apply：一键应用 + 校验 + 自动回滚。</summary>
        public IResult Apply(ConfigApplyRequest request)
        {
            if (request == null)
            {
                return Results.BadRequest(new { error = "请求体不能为空" });
            }
            if (string.IsNullOrEmpty(_applyScript) || !File.Exists(_applyScript))
            {
                return Results.Json(new ConfigApplyResult
                {
                    ok = false,
                    conclusion = "工具未找到",
                    error = "找不到 tools\\apply-config.ps1；请先跑一次 build.ps1（它会把 tools 复制到 runtime\\tools）"
                }, statusCode: 500);
            }

            string triggerMode = (request.triggerMode ?? string.Empty).Trim().ToLowerInvariant();
            if (triggerMode.Length > 0 && Array.IndexOf(AllowedTriggerModes, triggerMode) < 0)
            {
                return Results.BadRequest(new { error = "triggerMode 只能是 hard / soft / free" });
            }

            bool changeCameras = request.cameras != null;
            if (triggerMode.Length == 0 && !changeCameras)
            {
                return Results.BadRequest(new { error = "至少要改一项：triggerMode 或 cameras" });
            }

            List<string> cameraLines = null;
            if (changeCameras)
            {
                cameraLines = new List<string>();
                for (int i = 0; i < request.cameras.Count; i++)
                {
                    string line = (request.cameras[i] ?? string.Empty).Trim();
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    string check = line;
                    int comma = check.IndexOf(',');
                    if (comma > 0)
                    {
                        check = check.Substring(0, comma);
                    }
                    int eq = check.IndexOf('=');
                    string kind = eq > 0 ? check.Substring(0, eq).Trim().ToLowerInvariant() : string.Empty;
                    string value = eq > 0 ? check.Substring(eq + 1).Trim() : string.Empty;
                    if ((kind != "ip" && kind != "key" && kind != "id") || value.Length == 0)
                    {
                        return Results.BadRequest(new { error = "相机清单格式错误（第 " + (i + 1) + " 行）：" + line + "，应为 ip=... / key=... / id=...，可加 ,pos=top" });
                    }
                    cameraLines.Add(line);
                }

                if (cameraLines.Count == 0)
                {
                    return Results.BadRequest(new { error = "相机清单为空" });
                }
            }

            if (!Monitor.TryEnter(_applySync))
            {
                return Results.Json(new ConfigApplyResult
                {
                    ok = false,
                    conclusion = "已有一次应用在进行中",
                    error = "请等上一次应用结束再点"
                }, statusCode: 409);
            }

            try
            {
                ConfigApplyResult result = RunApply(triggerMode, cameraLines, request);
                _lastApply = result;
                return Results.Json(result);
            }
            finally
            {
                Monitor.Exit(_applySync);
            }
        }

        private ConfigApplyResult RunApply(string triggerMode, List<string> cameraLines, ConfigApplyRequest request)
        {
            Stopwatch watch = Stopwatch.StartNew();
            ConfigApplyResult result = new ConfigApplyResult();
            result.at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            try
            {
                List<string> args = new List<string>();
                args.Add("-NoProfile");
                args.Add("-ExecutionPolicy");
                args.Add("Bypass");
                args.Add("-File");
                args.Add(_applyScript);
                args.Add("-RuntimeDir");
                args.Add(_runtimeRoot);

                if (triggerMode.Length > 0)
                {
                    args.Add("-TriggerMode");
                    args.Add(triggerMode);
                }

                if (cameraLines != null)
                {
                    WriteCameraList(cameraLines);
                    args.Add("-CameraList");
                    args.Add(_cameraListPath);
                }

                if (request.skipVerify)
                {
                    args.Add("-SkipVerify");
                }
                else
                {
                    if (request.stopHost)
                    {
                        args.Add("-StopHost");
                    }
                    if (request.restartHost)
                    {
                        args.Add("-RestartHost");
                    }
                }

                if (request.verifyTimeoutSeconds > 0)
                {
                    args.Add("-VerifyTimeoutSeconds");
                    args.Add(request.verifyTimeoutSeconds.ToString());
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = _powershell;
                psi.WorkingDirectory = _runtimeRoot;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                for (int i = 0; i < args.Count; i++)
                {
                    psi.ArgumentList.Add(args[i]);
                }

                result.commandLine = _powershell + " " + string.Join(" ", args.ToArray());
                _logger.LogInformation("一键应用配置：{0}", result.commandLine);

                StringBuilder stdout = new StringBuilder();
                StringBuilder stderr = new StringBuilder();

                using (Process process = new Process())
                {
                    process.StartInfo = psi;
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                        {
                            lock (stdout) { stdout.AppendLine(e.Data); }
                        }
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                        {
                            lock (stderr) { stderr.AppendLine(e.Data); }
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    int timeoutMs = (_timeoutSeconds + (request.verifyTimeoutSeconds > 0 ? request.verifyTimeoutSeconds : 120)) * 1000;
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        result.ok = false;
                        result.exitCode = -1;
                        result.conclusion = "超时";
                        result.error = "一键应用超过 " + (timeoutMs / 1000) + " 秒未结束（已强制结束）；请检查是否有第二个采集宿主在占用相机";
                    }
                    else
                    {
                        result.exitCode = process.ExitCode;
                        result.conclusion = DescribeExitCode(result.exitCode);
                        result.ok = result.exitCode == 0;
                    }
                }

                lock (stdout) { result.output = stdout.ToString(); }
                if (result.output == null)
                {
                    result.output = string.Empty;
                }
                lock (stderr)
                {
                    if (stderr.Length > 0)
                    {
                        result.output += Environment.NewLine + "---- stderr ----" + Environment.NewLine + stderr.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                result.ok = false;
                result.exitCode = -1;
                result.conclusion = "调用失败";
                result.error = ex.Message;
                _logger.LogError(ex, "一键应用配置失败");
            }

            watch.Stop();
            result.durationMs = watch.ElapsedMilliseconds;
            return result;
        }

        private static string DescribeExitCode(int code)
        {
            switch (code)
            {
                case 0: return "配置已生效（SDK 启动成功、参数已回读校验）";
                case 2: return "校验失败，已自动回滚到应用前的配置";
                case 3: return "校验失败，回滚后仍起不来（设备/加密狗/环境问题，需人工检查）";
                case 1: return "应用失败（已回滚），请看输出里的原因";
                default: return "未知结果（退出码 " + code + "）";
            }
        }

        private void WriteCameraList(List<string> cameraLines)
        {
            string dir = Path.GetDirectoryName(_cameraListPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 由业务平台配置页写入（一键应用用的相机清单）");
            sb.AppendLine("# 每行一台：ip=... / key=... / id=...，可选 ,pos=top");
            sb.AppendLine("# 生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            for (int i = 0; i < cameraLines.Count; i++)
            {
                sb.AppendLine(cameraLines[i]);
            }

            File.WriteAllText(_cameraListPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>GET /api/camera-positions：当前方位映射。</summary>
        public object ReadPositions()
        {
            CameraPositions positions = CameraPositions.Load(_positionsPath);
            List<object> rows = new List<object>();
            foreach (KeyValuePair<string, string> pair in positions.Entries)
            {
                rows.Add(new
                {
                    key = pair.Key,
                    position = pair.Value,
                    label = CameraPositions.Label(pair.Value)
                });
            }

            return new
            {
                file = _positionsPath,
                exists = File.Exists(_positionsPath),
                count = positions.Count,
                options = CameraPositions.Options(),
                entries = rows
            };
        }

        /// <summary>POST /api/camera-positions：保存方位映射（自动备份原文件）。</summary>
        public IResult SavePositions(CameraPositionsRequest request)
        {
            if (request == null || request.positions == null || request.positions.Count == 0)
            {
                return Results.BadRequest(new { error = "positions 不能为空" });
            }

            CameraPositions positions = CameraPositions.Load(_positionsPath);
            int changed = 0;
            foreach (KeyValuePair<string, string> pair in request.positions)
            {
                string key = (pair.Key ?? string.Empty).Trim();
                if (key.Length == 0)
                {
                    continue;
                }

                string value = (pair.Value ?? string.Empty).Trim();
                if (value.Length == 0)
                {
                    if (positions.Remove(key))
                    {
                        changed++;
                    }
                    continue;
                }

                if (!CameraPositions.IsKnown(value))
                {
                    return Results.BadRequest(new
                    {
                        error = "方位只能是 " + string.Join("/", CameraPositions.Options()) + "，收到：" + value
                    });
                }

                if (!string.Equals(positions.Get(key), CameraPositions.Normalize(value), StringComparison.OrdinalIgnoreCase))
                {
                    changed++;
                }
                positions.Set(key, value);
            }

            string backup = positions.Save(_positionsPath);
            _logger.LogInformation("相机方位映射已保存：{0} 条变更，文件 {1}", changed, _positionsPath);

            return Results.Json(new
            {
                ok = true,
                changed,
                count = positions.Count,
                file = _positionsPath,
                backup,
                note = "方位已写入文件；采集宿主重启后生效（可在配置页用一键应用重启它）。"
            });
        }

        /// <summary>
        /// 找 tools 目录：优先 runtime\tools（发布时和 runtime 一起拷过去），
        /// 再回退到 runtime 的上一级 tools（开发时的仓库布局）。
        /// </summary>
        private string ResolveToolsScript(string configured, string scriptName)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(configured))
            {
                candidates.Add(Path.GetFullPath(Path.Combine(_runtimeRoot, configured)));
                candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured)));
            }
            candidates.Add(Path.Combine(_runtimeRoot, "tools"));
            candidates.Add(Path.Combine(Path.GetDirectoryName(_runtimeRoot), "tools"));

            for (int i = 0; i < candidates.Count; i++)
            {
                string file = Path.Combine(candidates[i], scriptName);
                if (File.Exists(file))
                {
                    return file;
                }
            }

            return Path.Combine(candidates[candidates.Count - 1], scriptName);
        }

        private string ReadGatewayProvider()
        {
            try
            {
                if (!File.Exists(_gatewayPath))
                {
                    return null;
                }

                string[] lines = File.ReadAllLines(_gatewayPath, Encoding.UTF8);
                bool inRuntime = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    {
                        continue;
                    }
                    if (line[0] == '[')
                    {
                        inRuntime = string.Equals(line, "[runtime]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (inRuntime)
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), "provider", StringComparison.OrdinalIgnoreCase))
                        {
                            return line.Substring(eq + 1).Trim();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("读取 gateway.ini 失败：{0}", ex.Message);
            }
            return null;
        }

        // ================================================================
        // C5 配置页（简版）：存图策略图形化 + 配置备份与回滚
        // ================================================================

        #region C5 存图策略

        /// <summary>存图策略（界面上一个表单；写入 gateway.ini 的 provider 段与 [storage] 段）。</summary>
        public sealed class StorageOptions
        {
            // ---- provider 段（大华/模拟器通用：存哪些图）----
            public bool saveOriginal { get; set; } = true;
            public bool saveWaybill { get; set; } = true;
            public bool savePerCamera { get; set; }
            public bool attachAllCameraCodeInfo { get; set; }
            /// <summary>provider 段里的图片目录（相对 runtime）。</summary>
            public string providerImageDir { get; set; }

            // ---- [storage] 段（与厂商无关的保留策略）----
            /// <summary>图片目录：留空表示沿用 provider 段里的 imageDir。</summary>
            public string imageDir { get; set; }
            /// <summary>图片保存天数；0 = 永久保留。</summary>
            public int retentionDays { get; set; } = 7;
            /// <summary>存图磁盘占用上限（%）；0 = 不启用。</summary>
            public int maxDiskPercent { get; set; } = 85;
            public int cleanupIntervalMinutes { get; set; } = 30;
            public bool cleanupOnStart { get; set; } = true;
            /// <summary>spool 事件文件保留天数；0 = 永久保留。</summary>
            public int spoolRetentionDays { get; set; } = 7;
        }

        /// <summary>GET /api/config/storage：当前存图策略 + 取值范围（界面直接渲染）。</summary>
        public object ReadStorage()
        {
            string text = File.Exists(_gatewayPath) ? File.ReadAllText(_gatewayPath, Encoding.UTF8) : string.Empty;
            string provider = ReadGatewayProvider();
            if (string.IsNullOrEmpty(provider))
            {
                provider = "dahua-dws";
            }

            StorageOptions options = new StorageOptions();
            // 存图开关是"相机 provider"的参数：当前 provider 段里有就认它，否则落到写这些键的那个段
            // （现场常见情况：正式跑 dahua-dws，测试环境为了不插狗把 provider 改成 simulator）
            string switchSection = StorageSwitchSection(text, provider);
            options.saveOriginal = IniBool(text, switchSection, "saveOriginal", true);
            options.saveWaybill = IniBool(text, switchSection, "saveWaybill", true);
            options.savePerCamera = IniBool(text, switchSection, "savePerCamera", false);
            options.attachAllCameraCodeInfo = IniBool(text, switchSection, "attachAllCameraCodeInfo", false);
            options.providerImageDir = IniText(text, provider, "imageDir");
            options.imageDir = IniText(text, "storage", "imageDir");
            options.retentionDays = IniInt(text, "storage", "retentionDays", 7);
            options.maxDiskPercent = IniInt(text, "storage", "maxDiskPercent", 85);
            options.cleanupIntervalMinutes = IniInt(text, "storage", "cleanupIntervalMinutes", 30);
            options.cleanupOnStart = IniBool(text, "storage", "cleanupOnStart", true);
            options.spoolRetentionDays = IniInt(text, "storage", "spoolRetentionDays", 7);

            string effectiveDir = string.IsNullOrEmpty(options.imageDir) ? options.providerImageDir : options.imageDir;
            if (string.IsNullOrEmpty(effectiveDir))
            {
                effectiveDir = "images";
            }

            return new
            {
                file = _gatewayPath,
                exists = File.Exists(_gatewayPath),
                provider,
                providerSection = provider,
                storageSection = switchSection,
                imageRoot = Path.Combine(_runtimeRoot, effectiveDir),
                options,
                limits = new
                {
                    retentionDays = "0-3650（0 = 永久保留）",
                    maxDiskPercent = "0 或 10-99（0 = 不启用磁盘水位保护）",
                    cleanupIntervalMinutes = "1-1440",
                    spoolRetentionDays = "0-3650（0 = 永久保留）",
                    imageDir = "相对路径，留空 = 沿用 provider 段；不能含 .. 或绝对路径"
                },
                note = "存图策略由采集宿主执行：保存后要重启采集宿主才生效（宿主只在启动时读 gateway.ini）"
            };
        }

        /// <summary>POST /api/config/storage：校验 → 备份 → 只改这几个键（注释与其他段原样保留）。</summary>
        public object SaveStorage(StorageOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }
            if (!File.Exists(_gatewayPath))
            {
                throw new InvalidOperationException("找不到采集宿主配置：" + _gatewayPath);
            }

            List<string> problems = ValidateStorage(options);
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", problems.ToArray()));
            }

            string provider = ReadGatewayProvider();
            if (string.IsNullOrEmpty(provider))
            {
                provider = "dahua-dws";
            }

            string backup = _gatewayPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(_gatewayPath, backup, true);

            bool bom = HasUtf8Bom(_gatewayPath);
            string text = File.ReadAllText(_gatewayPath, Encoding.UTF8);
            string switchSection = StorageSwitchSection(text, provider);
            List<string> changed = new List<string>();

            text = SetIni(text, provider, "imageDir", options.providerImageDir, changed, "provider.imageDir");
            text = SetIni(text, switchSection, "saveOriginal", BoolText(options.saveOriginal), changed, "saveOriginal");
            text = SetIni(text, switchSection, "saveWaybill", BoolText(options.saveWaybill), changed, "saveWaybill");
            text = SetIni(text, switchSection, "savePerCamera", BoolText(options.savePerCamera), changed, "savePerCamera");
            text = SetIni(text, switchSection, "attachAllCameraCodeInfo", BoolText(options.attachAllCameraCodeInfo),
                changed, "attachAllCameraCodeInfo");
            text = SetIni(text, "storage", "imageDir", options.imageDir, changed, "imageDir");
            text = SetIni(text, "storage", "retentionDays", options.retentionDays.ToString(), changed, "retentionDays");
            text = SetIni(text, "storage", "maxDiskPercent", options.maxDiskPercent.ToString(), changed, "maxDiskPercent");
            text = SetIni(text, "storage", "cleanupIntervalMinutes", options.cleanupIntervalMinutes.ToString(),
                changed, "cleanupIntervalMinutes");
            text = SetIni(text, "storage", "cleanupOnStart", BoolText(options.cleanupOnStart), changed, "cleanupOnStart");
            text = SetIni(text, "storage", "spoolRetentionDays", options.spoolRetentionDays.ToString(),
                changed, "spoolRetentionDays");

            File.WriteAllText(_gatewayPath, text, new UTF8Encoding(bom));
            _logger.LogInformation("存图策略已保存：{0}（改了 {1} 项，备份 {2}）", _gatewayPath, changed.Count, backup);

            return new
            {
                ok = true,
                file = _gatewayPath,
                backup,
                storageSection = switchSection,
                changed,
                changedCount = changed.Count,
                needRestartHost = true,
                note = changed.Count == 0
                    ? "内容没有变化（已照写一遍）"
                    : "已保存；重启采集宿主后生效（宿主只在启动时读 gateway.ini）"
            };
        }

        /// <summary>存图策略的参数校验（界面上的红字提示就是这里的返回）。</summary>
        public List<string> ValidateStorage(StorageOptions options)
        {
            List<string> problems = new List<string>();
            if (options == null)
            {
                problems.Add("配置不能为空");
                return problems;
            }
            if (options.retentionDays < 0 || options.retentionDays > 3650)
            {
                problems.Add("图片保存天数要在 0-3650 之间（0 = 永久保留）");
            }
            if (options.maxDiskPercent != 0 && (options.maxDiskPercent < 10 || options.maxDiskPercent > 99))
            {
                problems.Add("磁盘水位要在 10-99 之间（0 = 关闭水位保护）");
            }
            if (options.cleanupIntervalMinutes < 1 || options.cleanupIntervalMinutes > 1440)
            {
                problems.Add("清理间隔要在 1-1440 分钟之间");
            }
            if (options.spoolRetentionDays < 0 || options.spoolRetentionDays > 3650)
            {
                problems.Add("事件文件保留天数要在 0-3650 之间（0 = 永久保留）");
            }
            string dirProblem = CheckRelativeDir(options.imageDir, "storage.imageDir");
            if (dirProblem != null) { problems.Add(dirProblem); }
            dirProblem = CheckRelativeDir(options.providerImageDir, "provider.imageDir");
            if (dirProblem != null) { problems.Add(dirProblem); }
            if (options.savePerCamera && !options.attachAllCameraCodeInfo)
            {
                problems.Add("勾了\"保存每台相机各自的图\"就必须同时勾上\"回传每台相机的码信息\"，否则拿不到相机标识");
            }
            return problems;
        }

        /// <summary>
        /// 存图开关（saveOriginal / saveWaybill / savePerCamera / attachAllCameraCodeInfo）该写进哪个段。
        ///
        /// 这几个键是"相机 provider"自己的参数，不是通用段。现场常见两种写法：
        ///   * provider=dahua-dws：直接写 [dahua-dws]；
        ///   * 测试时 provider=simulator：模拟器段里没有这些键，如果按"当前 provider"写，
        ///     就会往 [simulator] 里插一堆没人读的键，而 [dahua-dws] 里的老值原封不动 —— 现场看到的就是"改了没生效"。
        /// 所以顺序是：当前 provider 段已有这些键 → 用它；否则用已经有这些键的段；再否则才落到当前 provider 段。
        /// </summary>
        private static string StorageSwitchSection(string text, string provider)
        {
            if (!string.IsNullOrEmpty(provider) && IniText(text, provider, "saveOriginal") != null)
            {
                return provider;
            }
            if (IniText(text, "dahua-dws", "saveOriginal") != null)
            {
                return "dahua-dws";
            }
            return string.IsNullOrEmpty(provider) ? "dahua-dws" : provider;
        }

        private static string CheckRelativeDir(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;   // 留空是合法的（沿用另一个键）
            }
            string trimmed = value.Trim();
            if (trimmed.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return label + " 不能包含 ..";
            }
            if (Path.IsPathRooted(trimmed))
            {
                return label + " 要用相对 runtime 的路径（不要写盘符或 \\\\ 开头）";
            }
            if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return label + " 含非法字符";
            }
            return null;
        }

        #endregion

        #region C5 备份与回滚

        /// <summary>POST /api/config/rollback 的请求体：只给文件名（不允许带路径）。</summary>
        public sealed class RollbackRequest
        {
            /// <summary>备份文件名，例如 gateway.ini.bak-20260918-101010。</summary>
            public string file { get; set; }
        }

        /// <summary>GET /api/config/backups：列出 config\ 与 Cfg\ 下的历史备份（新的在前）。</summary>
        public List<object> ReadBackups()
        {
            List<object> list = new List<object>();
            string[] dirs = { Path.Combine(_runtimeRoot, "config"), Path.Combine(_runtimeRoot, "Cfg") };

            for (int i = 0; i < dirs.Length; i++)
            {
                if (!Directory.Exists(dirs[i]))
                {
                    continue;
                }

                string[] files = Directory.GetFiles(dirs[i], "*.bak-*");
                for (int k = 0; k < files.Length; k++)
                {
                    FileInfo info = new FileInfo(files[k]);
                    string name = info.Name;
                    int mark = name.LastIndexOf(".bak-", StringComparison.Ordinal);
                    string target = mark > 0 ? name.Substring(0, mark) : name;
                    string targetPath = Path.Combine(dirs[i], target);

                    list.Add(new
                    {
                        fileName = name,
                        folder = dirs[i],
                        target,
                        targetExists = File.Exists(targetPath),
                        size = info.Length,
                        modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        effect = RollbackEffect(target),
                        sizeText = (info.Length / 1024.0).ToString("0.0") + " KB"
                    });
                }
            }

            list.Sort(delegate(object a, object b)
            {
                string ma = (string)a.GetType().GetProperty("modified").GetValue(a, null);
                string mb = (string)b.GetType().GetProperty("modified").GetValue(b, null);
                return string.Compare(mb, ma, StringComparison.Ordinal);
            });
            return list;
        }

        /// <summary>回滚：把某个备份覆盖回原文件（当前内容先另存一份，防止"回滚错了"。</summary>
        public object Rollback(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new InvalidOperationException("要指定备份文件名");
            }
            if (fileName.IndexOf('\\') >= 0 || fileName.IndexOf('/') >= 0 || fileName.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException("只接受文件名，不接受路径：" + fileName);
            }

            int mark = fileName.LastIndexOf(".bak-", StringComparison.Ordinal);
            if (mark <= 0)
            {
                throw new InvalidOperationException("这不是一个备份文件名（应形如 xxx.bak-20260918-101010）：" + fileName);
            }
            string target = fileName.Substring(0, mark);

            string source = null;
            string targetPath = null;
            string[] dirs = { Path.Combine(_runtimeRoot, "config"), Path.Combine(_runtimeRoot, "Cfg") };
            for (int i = 0; i < dirs.Length; i++)
            {
                string candidate = Path.Combine(dirs[i], fileName);
                if (File.Exists(candidate))
                {
                    source = candidate;
                    targetPath = Path.Combine(dirs[i], target);
                    break;
                }
            }
            if (source == null)
            {
                throw new InvalidOperationException("找不到备份文件：" + fileName);
            }

            string keep = null;
            if (File.Exists(targetPath))
            {
                keep = targetPath + ".before-rollback-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(targetPath, keep, true);
            }

            File.Copy(source, targetPath, true);
            _logger.LogWarning("配置回滚：{0} → {1}（回滚前的内容另存为 {2}）", fileName, target, keep ?? "（原本不存在）");

            return new
            {
                ok = true,
                target,
                restoredFrom = fileName,
                backupOfCurrent = keep,
                effect = RollbackEffect(target),
                note = RollbackEffect(target)
            };
        }

        /// <summary>回滚后怎么生效（界面上直接告诉用户）。</summary>
        private static string RollbackEffect(string target)
        {
            string name = (target ?? string.Empty).ToLowerInvariant();
            if (name.EndsWith(".cfg"))
            {
                return "SDK 配置：回滚后要点一次\"一键应用配置\"（写配置 → 重启校验）";
            }
            if (name.StartsWith("gateway.ini"))
            {
                return "采集宿主配置：回滚后要重启采集宿主才生效";
            }
            if (name.EndsWith(".json"))
            {
                return "平台配置：回滚后自动热加载（最多 1 秒生效）";
            }
            return "回滚完成";
        }

        #endregion

        #region C5 INI 小工具（只改指定键，注释与其他段原样保留）

        private static bool HasUtf8Bom(string path)
        {
            try
            {
                byte[] head = new byte[3];
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int read = stream.Read(head, 0, 3);
                    return read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string BoolText(bool value)
        {
            return value ? "true" : "false";
        }

        private static string IniText(string text, string section, string key)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(section))
            {
                return null;
            }

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            bool inSection = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                {
                    continue;
                }
                if (line[0] == '[')
                {
                    inSection = string.Equals(line.Trim('[', ']').Trim(), section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection)
                {
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(eq + 1).Trim();
                }
            }
            return null;
        }

        private static int IniInt(string text, string section, string key, int fallback)
        {
            string raw = IniText(text, section, key);
            int value;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out value))
            {
                return value;
            }
            return fallback;
        }

        private static bool IniBool(string text, string section, string key, bool fallback)
        {
            string raw = IniText(text, section, key);
            if (string.IsNullOrEmpty(raw))
            {
                return fallback;
            }
            raw = raw.Trim().ToLowerInvariant();
            if (raw == "1" || raw == "true" || raw == "yes" || raw == "on")
            {
                return true;
            }
            if (raw == "0" || raw == "false" || raw == "no" || raw == "off")
            {
                return false;
            }
            return fallback;
        }

        /// <summary>
        /// 改一个键：段里已有就替换那一行，没有就在段尾插入；段不存在就在文件末尾新建。
        /// 只动这一行，注释、空行、其他段全部原样保留（现场配置里有大量说明性注释，不能被冲掉）。
        /// </summary>
        private static string SetIni(string text, string section, string key, string value,
            List<string> changed, string label)
        {
            string newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            List<string> lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            // 末尾通常会有一个空串（文件以换行结尾），单独处理，避免多出空行
            bool trailing = lines.Count > 0 && lines[lines.Count - 1].Length == 0;
            if (trailing)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            string wanted = key + "=" + (value ?? string.Empty);
            int sectionStart = -1;
            int sectionEnd = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (line.Length > 0 && line[0] == '[')
                {
                    if (sectionStart >= 0)
                    {
                        sectionEnd = i;
                        break;
                    }
                    if (string.Equals(line.Trim('[', ']').Trim(), section, StringComparison.OrdinalIgnoreCase))
                    {
                        sectionStart = i;
                    }
                }
            }

            if (sectionStart < 0)
            {
                lines.Add(string.Empty);
                lines.Add("[" + section + "]");
                lines.Add(wanted);
                changed.Add(label);
            }
            else
            {
                int hit = -1;
                for (int i = sectionStart + 1; i < sectionEnd && i < lines.Count; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    {
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = i;
                        break;
                    }
                }

                if (hit >= 0)
                {
                    if (!string.Equals(lines[hit].Trim(), wanted, StringComparison.Ordinal))
                    {
                        lines[hit] = wanted;
                        changed.Add(label);
                    }
                }
                else
                {
                    lines.Insert(sectionEnd, wanted);
                    changed.Add(label);
                }
            }

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                builder.Append(lines[i]);
                builder.Append(newline);
            }
            if (!trailing)
            {
                // 原文件本来就不以换行结尾：去掉我们多加的那个
                builder.Length -= newline.Length;
            }
            return builder.ToString();
        }

        #endregion
    }
}
