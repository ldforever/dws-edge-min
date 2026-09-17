using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using DwsEdge.Core.Abstractions;
using DwsEdge.Core.Config;
using DwsEdge.Core.Model;

namespace DwsEdge.Providers.Simulator
{
    /// <summary>
    /// 测试用模拟 provider：没有相机、没有加密狗也能把整条链路跑通。
    ///
    /// 调用 SoftTrigger() 就"模拟一次过包"：
    ///   1. 生成一个测试条码；
    ///   2. 生成一张小 BMP 图片（模拟原图），落盘并挂到事件上；
    ///   3. 按大华的节奏发两条事件：先 Detected（只有条码），再 Enriched（带重量体积）；
    ///   4. 事件走同一个 IEventSink（控制台 + 日志 + spool）。
    ///
    /// 它存在的意义：
    ///   - 现场没有设备时，验证 DwsEdge.Host / HostEventSink / spool / 将来的业务层；
    ///   - 给 --trigger-once / 交互触发 / 定时触发提供一个不依赖硬件的"靶子"。
    /// </summary>
    public sealed class SimulatorProvider : IAcquisitionProvider, ITriggerControl, IConfigVerification
    {
        private readonly IEventSink _sink;
        private readonly ProviderSettings _settings;
        private readonly string _imageRoot;
        private readonly string _codePrefix;
        private readonly bool _emitEnriched;
        private readonly int _width;
        private readonly int _height;

        private volatile bool _running;
        private long _seq;

        public SimulatorProvider(ProviderSettings settings, IEventSink sink)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }
            if (sink == null)
            {
                throw new ArgumentNullException("sink");
            }

            _sink = sink;
            _settings = settings;
            _imageRoot = settings.ResolvePath(settings.Get("imageDir", "images"));
            _codePrefix = settings.Get("codePrefix", "TEST");
            _emitEnriched = settings.GetBool("emitEnriched", true);
            _width = Math.Max(32, settings.GetInt("imageWidth", 320));
            _height = Math.Max(32, settings.GetInt("imageHeight", 240));
        }

        public string ProviderId
        {
            get { return "simulator"; }
        }

        public ProviderCapabilities Capabilities
        {
            get { return ProviderCapabilities.SoftTrigger | ProviderCapabilities.ComplementCode; }
        }

        public void Start()
        {
            _running = true;
            _sink.Log(LogLevel.Info, "[simulator] 已启动（不需要相机/加密狗）。触发一次即可模拟一个包裹过包。");
            EmitCameraSnapshot();
        }

        /// <summary>
        /// 按 cfg 里的相机清单上报一条"快照"，让设备信息页在没有真机时也有完整的相机列表。
        /// 型号/序列号是假数据（SIM- 前缀），但 ip/key/id、方位、在线状态与清单完全一致。
        /// [simulator] offlineCameras=3,5 可以让第 3、5 台显示成离线（用来验证界面的离线行）。
        /// </summary>
        private void EmitCameraSnapshot()
        {
            try
            {
                string cfgPath = _settings.ResolvePath(_settings.Get("cfgPath", @"Cfg\LogisticsBase.cfg"));
                CameraPlan plan = CameraPlan.Read(cfgPath);
                CameraPositions positions = CameraPositions.Load(
                    _settings.ResolvePath(_settings.Get("cameraPositionsFile", CameraPositions.DefaultRelativePath)));

                List<CameraPlanEntry> declared = CameraIdentity.EnabledEntries(plan);
                if (declared.Count == 0)
                {
                    _sink.Log(LogLevel.Info, "[simulator] cfg 里没有启用中的相机声明，跳过相机快照");
                    return;
                }

                HashSet<string> offline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string offlineSetting = _settings.Get("offlineCameras", null);
                if (!string.IsNullOrEmpty(offlineSetting))
                {
                    string[] parts = offlineSetting.Split(',');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string item = parts[i].Trim();
                        if (item.Length > 0)
                        {
                            offline.Add(item);
                        }
                    }
                }

                CameraRuntimeTracker tracker = new CameraRuntimeTracker();
                for (int i = 0; i < declared.Count; i++)
                {
                    CameraPlanEntry entry = declared[i];
                    string index = (i + 1).ToString(CultureInfo.InvariantCulture);
                    bool isOffline = offline.Contains(index) || offline.Contains(entry.Value);

                    CameraStatusEvent evt = new CameraStatusEvent();
                    evt.ProviderId = ProviderId;
                    evt.DeviceId = entry.Value;
                    evt.UserId = entry.Value;
                    evt.Online = !isOffline;
                    evt.Discovered = !isOffline;
                    evt.IsSnapshot = true;
                    evt.AtMs = NowMs();
                    evt.DeclaredKind = entry.Kind;
                    evt.DeclaredValue = entry.Value;
                    evt.Position = positions.Resolve(entry.Value);
                    evt.Model = "SIM-CAM";
                    evt.SerialNumber = "SIM" + index.PadLeft(3, '0');
                    evt.Vendor = "DwsEdge Simulator";
                    evt.Firmware = "sim-1.0";

                    tracker.Apply(evt, true);
                    _sink.OnCameraStatus(evt);
                }

                _sink.Log(LogLevel.Info, "[simulator] 已上报相机快照 " + declared.Count + " 台（来自 cfg 相机清单）");
            }
            catch (Exception ex)
            {
                _sink.Log(LogLevel.Warn, "[simulator] 上报相机快照失败：" + ex.Message);
            }
        }

        public void Stop()
        {
            _running = false;
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>模拟一次过包（等同于真实相机的软触发）。</summary>
        public int SoftTrigger()
        {
            if (!_running)
            {
                _sink.Log(LogLevel.Warn, "[simulator] 尚未启动");
                return -1;
            }

            long seq = Interlocked.Increment(ref _seq);
            long capturedAt = NowMs();
            string code = _codePrefix + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture)
                        + seq.ToString("D3", CultureInfo.InvariantCulture);

            // 第一次回调：只有条码（对齐大华 OutputResult = 0）
            ParcelEvent detected = BuildParcel(seq, capturedAt, code, ParcelStage.Detected);
            SaveFakeImage(detected, seq, "ori");
            _sink.OnParcel(detected);

            // 第二次回调：条码 + 重量 + 体积（对齐大华 OutputResult = 1）
            if (_emitEnriched)
            {
                ParcelEvent enriched = BuildParcel(seq, capturedAt, code, ParcelStage.Enriched);
                enriched.WeightGrams = 500 + (int)(seq % 1000);
                enriched.LengthMm = 300;
                enriched.WidthMm = 200;
                enriched.HeightMm = 150;
                enriched.VolumeMm3 = enriched.LengthMm * enriched.WidthMm * enriched.HeightMm;
                _sink.OnParcel(enriched);
            }

            _sink.Log(LogLevel.Info, "[simulator] 已模拟一次过包：条码=" + code + "（第 " + seq + " 次触发）");
            return 0;
        }

        public int ComplementCode(string code, long timeMs)
        {
            _sink.Log(LogLevel.Info, "[simulator] 收到补码请求：" + code
                + "（时间戳 " + (timeMs > 0 ? timeMs : NowMs()).ToString(CultureInfo.InvariantCulture) + "）");
            return 0;
        }

        /// <summary>
        /// 模拟器的配置校验：只校验配置文件本身（不连接相机，因此不校验工作相机数量）。
        /// 真机的"参数是否生效"校验请用 provider=dahua-dws。
        ///
        /// 测试注入：[simulator] rejectTriggerMode=soft|hard|free|0|1|2，
        /// 让模拟器"拒绝"某个触发模式，用来验证 apply-config.ps1 的"校验失败自动回滚"。
        /// </summary>
        public ConfigVerificationReport VerifyConfig()
        {
            ConfigVerificationReport report = new ConfigVerificationReport();

            string cfgPath = _settings.ResolvePath(_settings.Get("cfgPath", @"Cfg\LogisticsBase.cfg"));
            if (!File.Exists(cfgPath))
            {
                report.Problems.Add("找不到配置文件：" + cfgPath);
                report.Success = false;
                return report;
            }

            CameraPlan plan = CameraPlan.Read(cfgPath);
            report.Details.Add("配置回读：" + plan.Describe());
            report.Details.Add("模拟器不连接相机，工作相机数量不参与校验（真机请用 provider=dahua-dws）");
            report.Problems.AddRange(plan.Errors());

            string reject = NormalizeTriggerMode(_settings.Get("rejectTriggerMode", null));
            if (!string.IsNullOrEmpty(reject))
            {
                if (reject == plan.TriggerMode)
                {
                    report.Problems.Add("测试注入：模拟相机拒绝 triggerMode=" + plan.TriggerMode
                        + "（[simulator] rejectTriggerMode，仅用于验证自动回滚）");
                }
                else
                {
                    report.Details.Add("测试注入：rejectTriggerMode=" + reject
                        + "，当前 triggerMode=" + plan.TriggerMode + " 未被拒绝");
                }
            }

            report.Success = report.Problems.Count == 0;
            return report;
        }

        /// <summary>把 soft/hard/free 或 0/1/2 归一成 cfg 里的取值；识别不了就返回空串。</summary>
        private static string NormalizeTriggerMode(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            string text = value.Trim().ToLowerInvariant();
            if (text == "soft" || text == "2")
            {
                return "2";
            }
            if (text == "hard" || text == "1")
            {
                return "1";
            }
            if (text == "free" || text == "0")
            {
                return "0";
            }
            return string.Empty;
        }

        private ParcelEvent BuildParcel(long seq, long capturedAt, string code, ParcelStage stage)
        {
            ParcelEvent evt = new ParcelEvent();
            evt.EventId = seq * 10 + (stage == ParcelStage.Detected ? 0 : 1);
            evt.ProviderId = ProviderId;
            evt.DeviceId = "simulator-cam";
            evt.Stage = stage;
            evt.CapturedAtMs = capturedAt;
            evt.ReceivedAtMs = NowMs();
            evt.Codes.Add(new CodeItem(code, CodeKind.OneD, "top"));
            // 两次回调共享同一个 TraceId，业务层靠它合并
            evt.TraceId = ProviderId + "|simulator-cam|" + capturedAt.ToString(CultureInfo.InvariantCulture) + "|" + code;
            return evt;
        }

        private void SaveFakeImage(ParcelEvent evt, long seq, string suffix)
        {
            try
            {
                string dir = Path.Combine(_imageRoot, DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), "simulator");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir,
                    evt.CapturedAtMs.ToString(CultureInfo.InvariantCulture) + "_"
                    + seq.ToString("D3", CultureInfo.InvariantCulture) + "_" + suffix + ".bmp");

                WriteBmp(path, _width, _height);

                ImageRef image = new ImageRef();
                image.Kind = ImageKind.Original;
                image.DeviceId = evt.DeviceId;
                image.Path = path;
                image.Format = "bmp";
                image.Width = _width;
                image.Height = _height;
                image.Bytes = (int)new FileInfo(path).Length;

                evt.Images.Add(image);
                _sink.OnImageSaved(image);
            }
            catch (Exception ex)
            {
                _sink.LogError("[simulator] 生成测试图片失败", ex);
            }
        }

        /// <summary>生成一张带边框和斜线的 24bpp BMP，纯 BCL 实现，只为"有张图"。</summary>
        private static void WriteBmp(string path, int width, int height)
        {
            int rowBytes = width * 3;
            int padding = (4 - (rowBytes % 4)) % 4;
            int stride = rowBytes + padding;
            int pixelBytes = stride * height;

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((byte)'B');
                w.Write((byte)'M');
                w.Write(54 + pixelBytes);
                w.Write((short)0);
                w.Write((short)0);
                w.Write(54);

                w.Write(40);
                w.Write(width);
                w.Write(height);
                w.Write((short)1);
                w.Write((short)24);
                w.Write(0);
                w.Write(pixelBytes);
                w.Write(2835);
                w.Write(2835);
                w.Write(0);
                w.Write(0);

                byte[] row = new byte[stride];
                for (int y = height - 1; y >= 0; y--)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int offset = x * 3;
                        bool border = x < 4 || y < 4 || x >= width - 4 || y >= height - 4;
                        bool diagonal = Math.Abs((long)x * height - (long)y * width) < width * 3L;

                        if (border)
                        {
                            row[offset] = 235;
                            row[offset + 1] = 235;
                            row[offset + 2] = 235;
                        }
                        else if (diagonal)
                        {
                            row[offset] = 40;
                            row[offset + 1] = 200;
                            row[offset + 2] = 240;
                        }
                        else
                        {
                            row[offset] = 120;      // B
                            row[offset + 1] = 90;   // G
                            row[offset + 2] = 70;   // R
                        }
                    }

                    for (int p = rowBytes; p < stride; p++)
                    {
                        row[p] = 0;
                    }
                    w.Write(row);
                }
            }
        }

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }
    }
}
