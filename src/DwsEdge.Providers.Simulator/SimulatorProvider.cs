using System;
using System.Globalization;
using System.IO;
using System.Threading;
using DwsEdge.Core.Abstractions;
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
    public sealed class SimulatorProvider : IAcquisitionProvider, ITriggerControl
    {
        private readonly IEventSink _sink;
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
