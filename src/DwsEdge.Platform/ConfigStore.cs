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
    }
}
