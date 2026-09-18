using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>
    /// A8-3：一份"采集侧配置模板"。
    /// 装的是现场最常复制来复制去的那几样：相机清单（含方位）、触发模式、存图策略。
    /// 存成独立 JSON 文件，拷到同行的设备上就能用（离线交付，不依赖网络）。
    /// </summary>
    public sealed class ConfigTemplate
    {
        public string name { get; set; }
        public string note { get; set; }
        public string createdAt { get; set; }
        /// <summary>生成时的 provider 与机器名，方便分辨"这份模板是哪台设备上的"。</summary>
        public string source { get; set; }
        /// <summary>触发模式：0 自由拉流 / 1 硬触发 / 2 软触发。</summary>
        public string triggerMode { get; set; }
        /// <summary>相机清单（每行一台：ip=172.20.10.11,pos=top）。</summary>
        public List<string> cameras { get; set; } = new List<string>();
        public ConfigStore.StorageOptions storage { get; set; }
    }

    /// <summary>新建 / 删除模板的请求体。</summary>
    public sealed class TemplateSaveRequest
    {
        public string name { get; set; }
        public string note { get; set; }
    }

    /// <summary>套用模板的请求体：哪几部分要套用。</summary>
    public sealed class TemplateApplyRequest
    {
        public string name { get; set; }
        /// <summary>套用相机清单（会走"一键应用"：写 cfg → 重启校验 → 失败自动回滚）。</summary>
        public bool applyCameras { get; set; } = true;
        /// <summary>套用触发模式。</summary>
        public bool applyTrigger { get; set; } = true;
        /// <summary>套用存图策略（写 gateway.ini，重启采集宿主后生效）。</summary>
        public bool applyStorage { get; set; }
        public bool skipVerify { get; set; }
        public bool stopHost { get; set; } = true;
        public bool restartHost { get; set; } = true;
    }

    /// <summary>
    /// A8-3 配置模板：另存 / 列表 / 差异对比 / 套用 / 删除 / 导出。
    ///
    /// 设计取舍：模板只装"采集侧"的配置（相机清单 + 触发模式 + 存图策略）——
    /// 这三样是现场装机时最需要一次配好、批量复制的；平台侧的东西（下游、规则、监控阈值、班次）
    /// 各自都有独立的保存与备份，硬塞进一个模板反而容易互相覆盖。
    /// </summary>
    public sealed class TemplateStore
    {
        private readonly ILogger<TemplateStore> _logger;
        private readonly ConfigStore _config;
        private readonly string _dir;
        private readonly string _runtimeRoot;

        public TemplateStore(IConfiguration config, ConfigStore configStore, ILogger<TemplateStore> logger)
        {
            _logger = logger;
            _config = configStore;
            _runtimeRoot = configStore.RuntimeRoot;
            _dir = Path.Combine(_runtimeRoot, @"config\templates");
            try
            {
                Directory.CreateDirectory(_dir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("创建模板目录失败：{0}", ex.Message);
            }
        }

        public string DirectoryPath
        {
            get { return _dir; }
        }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public List<object> List()
        {
            List<object> result = new List<object>();
            if (!Directory.Exists(_dir))
            {
                return result;
            }

            string[] files = Directory.GetFiles(_dir, "*.json");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                ConfigTemplate template = Load(files[i]);
                if (template == null)
                {
                    continue;
                }
                FileInfo info = new FileInfo(files[i]);
                result.Add(new
                {
                    name = template.name,
                    note = template.note,
                    createdAt = template.createdAt,
                    source = template.source,
                    triggerMode = template.triggerMode,
                    triggerName = TriggerName(template.triggerMode),
                    cameraCount = template.cameras == null ? 0 : template.cameras.Count,
                    retentionDays = template.storage == null ? 0 : template.storage.retentionDays,
                    maxDiskPercent = template.storage == null ? 0 : template.storage.maxDiskPercent,
                    fileName = info.Name,
                    sizeText = (info.Length / 1024.0).ToString("0.0") + " KB",
                    filePath = files[i]
                });
            }
            return result;
        }

        private static string TriggerName(string mode)
        {
            if (mode == "2") { return "软触发"; }
            if (mode == "1") { return "硬触发"; }
            if (mode == "0") { return "自由拉流"; }
            return "未知";
        }

        private static string ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "模板名不能为空";
            }
            string value = name.Trim();
            if (value.Length > 32)
            {
                return "模板名最长 32 个字符";
            }
            char[] bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < bad.Length; i++)
            {
                if (value.IndexOf(bad[i]) >= 0)
                {
                    return "模板名不能包含 \\ / : * ? \" < > | 这类字符";
                }
            }
            if (value.IndexOf("..", StringComparison.Ordinal) >= 0 || value.IndexOf('.') == 0)
            {
                return "模板名不能以点开头，也不能包含 ..";
            }
            return null;
        }

        private string PathOf(string name)
        {
            return Path.Combine(_dir, name.Trim() + ".json");
        }

        private ConfigTemplate Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                ConfigTemplate template = JsonSerializer.Deserialize<ConfigTemplate>(
                    File.ReadAllText(path, Encoding.UTF8), Json);
                if (template != null && string.IsNullOrEmpty(template.name))
                {
                    template.name = Path.GetFileNameWithoutExtension(path);
                }
                return template;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("读取模板失败 {0}：{1}", path, ex.Message);
                return null;
            }
        }

        /// <summary>把"当前配置"另存为模板。</summary>
        public object Save(string name, string note)
        {
            string problem = ValidateName(name);
            if (problem != null)
            {
                throw new InvalidOperationException(problem);
            }
            string path = PathOf(name);
            if (File.Exists(path))
            {
                throw new InvalidOperationException("同名模板已存在：" + name.Trim() + "（先删除或用别的名字）");
            }

            ConfigTemplate template = Snapshot();
            template.name = name.Trim();
            template.note = note;
            template.createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            File.WriteAllText(path, JsonSerializer.Serialize(template, Json), new UTF8Encoding(false));
            _logger.LogInformation("配置模板已保存：{0}（{1} 台相机）", path, template.cameras.Count);

            return new
            {
                ok = true,
                template.name,
                file = path,
                cameraCount = template.cameras.Count,
                note = "模板已保存；拷这个 json 到别的设备就能套用"
            };
        }

        /// <summary>把当前配置抓成一份模板对象（用于保存与差异对比）。</summary>
        public ConfigTemplate Snapshot()
        {
            ConfigTemplate template = new ConfigTemplate();
            template.triggerMode = _config.CurrentTriggerMode();
            template.cameras = _config.CurrentCameraLines();
            template.source = Environment.MachineName + " / " + _config.CurrentProvider();
            template.storage = _config.CurrentStorage();
            return template;
        }

        public void Delete(string name)
        {
            string problem = ValidateName(name);
            if (problem != null)
            {
                throw new InvalidOperationException(problem);
            }
            string path = PathOf(name);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("找不到模板：" + name.Trim());
            }
            File.Delete(path);
            _logger.LogInformation("配置模板已删除：{0}", path);
        }

        public IResult Download(string name)
        {
            string problem = ValidateName(name);
            if (problem != null)
            {
                return Results.BadRequest(new { error = problem });
            }
            string path = PathOf(name);
            if (!File.Exists(path))
            {
                return Results.BadRequest(new { error = "找不到模板：" + name.Trim() });
            }
            return Results.File(path, "application/json", name.Trim() + ".json");
        }

        /// <summary>
        /// 模板 vs 当前配置的差异：相机清单（新增/缺少/方位变了）+ 触发模式 + 存图策略逐项。
        /// 界面上"套用前先看一眼会改什么"就靠它。
        /// </summary>
        public object Diff(string name)
        {
            string problem = ValidateName(name);
            if (problem != null)
            {
                throw new InvalidOperationException(problem);
            }
            ConfigTemplate template = Load(PathOf(name));
            if (template == null)
            {
                throw new InvalidOperationException("找不到模板：" + name.Trim());
            }

            ConfigTemplate current = Snapshot();
            List<object> changes = new List<object>();

            Dictionary<string, string> templateCams = SplitCameras(template.cameras);
            Dictionary<string, string> currentCams = SplitCameras(current.cameras);

            foreach (KeyValuePair<string, string> pair in templateCams)
            {
                string pos;
                if (!currentCams.TryGetValue(pair.Key, out pos))
                {
                    changes.Add(new { area = "相机清单", kind = "缺少", item = pair.Key, template = Describe(pair.Value), current = "（当前没有这台）" });
                }
                else if (!string.Equals(pos, pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add(new { area = "相机清单", kind = "方位不同", item = pair.Key, template = Describe(pair.Value), current = Describe(pos) });
                }
            }
            foreach (KeyValuePair<string, string> pair in currentCams)
            {
                if (!templateCams.ContainsKey(pair.Key))
                {
                    changes.Add(new { area = "相机清单", kind = "多出", item = pair.Key, template = "（模板里没有）", current = Describe(pair.Value) });
                }
            }

            if (!string.Equals(template.triggerMode, current.triggerMode, StringComparison.Ordinal))
            {
                changes.Add(new
                {
                    area = "触发模式",
                    kind = "不同",
                    item = "triggerMode",
                    template = TriggerName(template.triggerMode) + "（" + template.triggerMode + "）",
                    current = TriggerName(current.triggerMode) + "（" + current.triggerMode + "）"
                });
            }

            changes.AddRange(DiffStorage(template.storage, current.storage));

            return new
            {
                name = template.name,
                templateCreatedAt = template.createdAt,
                templateSource = template.source,
                template = new { triggerMode = template.triggerMode, cameras = template.cameras, cameraCount = templateCams.Count },
                current = new { triggerMode = current.triggerMode, cameras = current.cameras, cameraCount = currentCams.Count },
                changeCount = changes.Count,
                same = changes.Count == 0,
                changes,
                note = changes.Count == 0 ? "当前配置和模板一致，不用套用" : "下面是套用模板后会发生的改动"
            };
        }

        private static List<object> DiffStorage(ConfigStore.StorageOptions template, ConfigStore.StorageOptions current)
        {
            List<object> changes = new List<object>();
            if (template == null || current == null)
            {
                return changes;
            }

            AddIfDifferent(changes, "存图策略", "保存原图", template.saveOriginal, current.saveOriginal);
            AddIfDifferent(changes, "存图策略", "保存面单图", template.saveWaybill, current.saveWaybill);
            AddIfDifferent(changes, "存图策略", "保存每台相机的图", template.savePerCamera, current.savePerCamera);
            AddIfDifferent(changes, "存图策略", "回传每台相机码信息", template.attachAllCameraCodeInfo, current.attachAllCameraCodeInfo);
            AddIfDifferent(changes, "存图策略", "图片目录", template.providerImageDir, current.providerImageDir);
            AddIfDifferent(changes, "存图策略", "storage 图片目录", template.imageDir, current.imageDir);
            AddIfDifferent(changes, "存图策略", "图片保存天数", template.retentionDays, current.retentionDays);
            AddIfDifferent(changes, "存图策略", "磁盘水位(%)", template.maxDiskPercent, current.maxDiskPercent);
            AddIfDifferent(changes, "存图策略", "清理间隔(分钟)", template.cleanupIntervalMinutes, current.cleanupIntervalMinutes);
            AddIfDifferent(changes, "存图策略", "启动时清理", template.cleanupOnStart, current.cleanupOnStart);
            AddIfDifferent(changes, "存图策略", "事件文件保留天数", template.spoolRetentionDays, current.spoolRetentionDays);
            return changes;
        }

        private static void AddIfDifferent(List<object> changes, string area, string item, object template, object current)
        {
            string a = template == null ? "" : template.ToString();
            string b = current == null ? "" : current.ToString();
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                changes.Add(new { area, kind = "不同", item, template = a, current = b });
            }
        }

        private static string Describe(string position)
        {
            return string.IsNullOrEmpty(position) ? "未设置方位" : position;
        }

        /// <summary>把 "ip=1.2.3.4,pos=top" 拆成 key=值 → 方位（相机用值做键）。</summary>
        private static Dictionary<string, string> SplitCameras(List<string> lines)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (lines == null)
            {
                return map;
            }
            for (int i = 0; i < lines.Count; i++)
            {
                string line = (lines[i] ?? string.Empty).Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                string position = "";
                int comma = line.IndexOf(',');
                string head = line;
                if (comma > 0)
                {
                    head = line.Substring(0, comma).Trim();
                    string tail = line.Substring(comma + 1);
                    int posIndex = tail.IndexOf("pos=", StringComparison.OrdinalIgnoreCase);
                    if (posIndex >= 0)
                    {
                        position = tail.Substring(posIndex + 4).Trim();
                    }
                }
                int eq = head.IndexOf('=');
                string key = eq > 0 ? head.Substring(eq + 1).Trim() : head;
                map[key] = position;
            }
            return map;
        }

        /// <summary>
        /// 套用模板：相机清单/触发模式走"一键应用"（写 cfg → 启动校验 → 失败自动回滚），
        /// 存图策略写 gateway.ini（各自都会自动备份），最后把三部分的结果一起返回。
        /// </summary>
        public object Apply(TemplateApplyRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }
            string problem = ValidateName(request.name);
            if (problem != null)
            {
                throw new InvalidOperationException(problem);
            }
            ConfigTemplate template = Load(PathOf(request.name));
            if (template == null)
            {
                throw new InvalidOperationException("找不到模板：" + request.name.Trim());
            }
            if (!request.applyCameras && !request.applyTrigger && !request.applyStorage)
            {
                throw new InvalidOperationException("至少要勾一项要套用的内容");
            }

            object applyResult = null;
            object storageResult = null;

            string triggerMode = null;
            List<string> cameras = null;
            if (request.applyTrigger && !string.IsNullOrEmpty(template.triggerMode))
            {
                triggerMode = template.triggerMode == "2" ? "soft" : (template.triggerMode == "1" ? "hard" : "free");
            }
            if (request.applyCameras && template.cameras != null && template.cameras.Count > 0)
            {
                cameras = template.cameras;
            }

            if (triggerMode != null || cameras != null)
            {
                ConfigApplyRequest body = new ConfigApplyRequest();
                body.triggerMode = triggerMode;
                body.cameras = cameras;
                body.skipVerify = request.skipVerify;
                body.stopHost = request.stopHost;
                body.restartHost = request.restartHost;

                // 走的是和界面"一键应用"完全相同的路径（写 cfg → 启动校验 → 失败自动回滚），
                // 只是这里拿强类型结果，好和存图策略的结果拼在一起返回
                applyResult = _config.ApplyTyped(body);
            }

            if (request.applyStorage && template.storage != null)
            {
                storageResult = _config.SaveStorage(template.storage);
            }

            _logger.LogInformation("已套用配置模板：{0}（相机={1} 触发={2} 存图={3}）",
                template.name, request.applyCameras, request.applyTrigger, request.applyStorage);

            return new
            {
                ok = true,
                name = template.name,
                applied = new
                {
                    cameras = request.applyCameras && cameras != null ? cameras.Count : 0,
                    triggerMode = triggerMode == null ? null : template.triggerMode,
                    storage = request.applyStorage && template.storage != null
                },
                apply = applyResult,
                storage = storageResult,
                note = "相机清单/触发模式走一键应用（含校验与回滚）；存图策略写 gateway.ini 后需要重启采集宿主生效"
            };
        }

    }
}
