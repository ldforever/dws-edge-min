using System;
using System.Globalization;
using System.IO;
using System.Text;
using DwsEdge.Core.Abstractions;
using DwsEdge.Core.Model;

namespace DwsEdge.Host
{
    /// <summary>
    /// 宿主的事件出口：控制台 + 文件日志 + JSONL spool。
    ///
    /// spool 就是方案 B 里"交给业务层"的那条缝：
    ///   今天写本地 JSONL 文件；
    ///   明天换成 gRPC/命名管道推给业务进程（或 ARM 盒子），provider 一行都不用改。
    /// </summary>
    internal sealed class HostEventSink : IEventSink, IDisposable
    {
        private readonly object _sync = new object();
        private readonly string _spoolDir;
        private readonly bool _spoolEnabled;
        private StreamWriter _logWriter;
        private StreamWriter _spoolWriter;
        private string _spoolDate;
        private bool _disposed;

        private long _parcelCount;
        private long _noreadCount;
        private long _imageCount;
        private long _cameraReadCount;

        /// <summary>当前 provider 是否分阶段上报包裹结果（宿主创建 provider 后设置，写进事件的 stagedResult 字段）。</summary>
        public bool StagedParcelResult { get; set; }

        public HostEventSink(string runtimeDirectory, bool spoolEnabled)
        {
            string logDir = Path.Combine(runtimeDirectory, "logs");
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "host-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            _logWriter = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            _logWriter.AutoFlush = true;

            _spoolEnabled = spoolEnabled;
            _spoolDir = Path.Combine(runtimeDirectory, "spool");
            if (_spoolEnabled)
            {
                Directory.CreateDirectory(_spoolDir);
                EnsureSpoolWriter();
            }
        }

        /// <summary>包裹结果：先打印条码，再把规范事件写进 spool。</summary>
        public void OnParcel(ParcelEvent parcel)
        {
            if (parcel == null)
            {
                return;
            }

            lock (_sync)
            {
                _parcelCount++;
                if (parcel.Codes.Count == 0)
                {
                    _noreadCount++;
                }

                StringBuilder summary = new StringBuilder();
                summary.Append(parcel.Stage == ParcelStage.Detected ? "条码" : "条码+重量体积");
                summary.Append("  相机=").Append(parcel.DeviceId);
                summary.Append("  条码数=").Append(parcel.Codes.Count);
                summary.Append("  [");
                for (int i = 0; i < parcel.Codes.Count; i++)
                {
                    if (i > 0)
                    {
                        summary.Append(", ");
                    }
                    summary.Append(parcel.Codes[i].ToString());
                }
                summary.Append(']');
                if (parcel.WeightGrams > 0)
                {
                    summary.Append("  重量=").Append(parcel.WeightGrams).Append('g');
                }
                if (parcel.VolumeMm3 > 0)
                {
                    summary.Append("  体积=").Append(Math.Round(parcel.VolumeMm3, 0)).Append("mm3");
                }
                summary.Append("  图=").Append(parcel.Images.Count);
                summary.Append("  累计包裹=").Append(_parcelCount);
                summary.Append("  累计NOREAD=").Append(_noreadCount);

                WriteConsole("parcel", summary.ToString());
                WriteLog(LogLevel.Info, "parcel " + summary);
                WriteSpool(JsonParcel(parcel));
            }
        }

        /// <summary>单相机读码事件（可选能力，开了才有）。</summary>
        public void OnCameraRead(CameraReadEvent cameraRead)
        {
            if (cameraRead == null)
            {
                return;
            }

            lock (_sync)
            {
                _cameraReadCount++;
                WriteLog(LogLevel.Debug, string.Format(CultureInfo.InvariantCulture,
                    "camera-read 相机={0} IP={1} 条码数={2}", cameraRead.DeviceId, cameraRead.CameraIp, cameraRead.Codes.Count));
                WriteSpool(JsonCameraRead(cameraRead));
            }
        }

        public void OnCameraStatus(CameraStatusEvent status)
        {
            if (status == null)
            {
                return;
            }

            string text = string.Format(CultureInfo.InvariantCulture,
                "相机 {0}（UserID={1}）{2}", status.DeviceId, status.UserId, status.Online ? "上线" : "离线");

            if (!status.Online)
            {
                text += "，第 " + status.OfflineCount + " 次掉线";
            }
            else if (status.ReconnectCount > 0)
            {
                text += "，第 " + status.ReconnectCount + " 次恢复（上次离线 "
                     + (status.LastOfflineDurationMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " 秒）";
            }

            lock (_sync)
            {
                WriteConsole(status.Online ? "cam-up" : "cam-down", text);
                WriteLog(status.Online ? LogLevel.Info : LogLevel.Warn, text);
                WriteSpool(JsonCameraStatus(status));
            }
        }

        public void OnImageSaved(ImageRef image)
        {
            if (image == null)
            {
                return;
            }

            lock (_sync)
            {
                _imageCount++;
                WriteLog(LogLevel.Debug, string.Format(CultureInfo.InvariantCulture,
                    "图片已保存 {0} {1}x{2} {3}KB {4}",
                    image.Kind, image.Width, image.Height, image.Bytes / 1024, image.Path));
            }
        }

        public void Log(LogLevel level, string message)
        {
            lock (_sync)
            {
                WriteLog(level, message);
                if (level != LogLevel.Debug)
                {
                    WriteConsole(level.ToString().ToLowerInvariant(), message);
                }
            }
        }

        public void LogError(string message, Exception ex)
        {
            string text = ex == null ? message : message + "：" + ex;
            lock (_sync)
            {
                WriteLog(LogLevel.Error, text);
                WriteConsole("error", text);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            lock (_sync)
            {
                if (_logWriter != null)
                {
                    _logWriter.Flush();
                    _logWriter.Dispose();
                    _logWriter = null;
                }
                if (_spoolWriter != null)
                {
                    _spoolWriter.Flush();
                    _spoolWriter.Dispose();
                    _spoolWriter = null;
                }
            }

            _disposed = true;
        }

        #region 输出辅助

        private void WriteConsole(string tag, string message)
        {
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[{0}][{1}] {2}", DateTime.Now.ToString("HH:mm:ss.fff"), tag, message));
        }

        private void WriteLog(LogLevel level, string message)
        {
            if (_logWriter == null)
            {
                return;
            }
            _logWriter.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0} [{1}] {2}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), level.ToString().ToUpperInvariant(), message));
        }

        private void EnsureSpoolWriter()
        {
            string today = DateTime.Now.ToString("yyyyMMdd");
            if (_spoolWriter != null && _spoolDate == today)
            {
                return;
            }
            if (_spoolWriter != null)
            {
                _spoolWriter.Flush();
                _spoolWriter.Dispose();
            }
            _spoolDate = today;
            string path = Path.Combine(_spoolDir, "events-" + today + ".jsonl");
            _spoolWriter = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            _spoolWriter.AutoFlush = true;
        }

        private void WriteSpool(string json)
        {
            if (!_spoolEnabled || json == null || _spoolWriter == null)
            {
                return;
            }
            EnsureSpoolWriter();
            _spoolWriter.WriteLine(json);
        }

        #endregion

        #region JSON（手写，避免引入序列化库；正式版建议换 protobuf/MessagePack）

        private string JsonParcel(ParcelEvent p)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"schemaVersion\":").Append(p.SchemaVersion);
            sb.Append(",\"type\":\"parcel\"");
            sb.Append(",\"eventId\":").Append(p.EventId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"providerId\":").Append(Quote(p.ProviderId));
            sb.Append(",\"deviceId\":").Append(Quote(p.DeviceId));
            sb.Append(",\"stage\":").Append(Quote(p.Stage == ParcelStage.Detected ? "detected" : "enriched"));
            sb.Append(",\"capturedAtMs\":").Append(p.CapturedAtMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"receivedAtMs\":").Append(p.ReceivedAtMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"traceId\":").Append(Quote(p.TraceId));
            sb.Append(",\"stagedResult\":").Append(StagedParcelResult ? "true" : "false");
            sb.Append(",\"weightGrams\":").Append(p.WeightGrams.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"lengthMm\":").Append(Number(p.LengthMm));
            sb.Append(",\"widthMm\":").Append(Number(p.WidthMm));
            sb.Append(",\"heightMm\":").Append(Number(p.HeightMm));
            sb.Append(",\"volumeMm3\":").Append(Number(p.VolumeMm3));
            sb.Append(",\"codes\":[");
            for (int i = 0; i < p.Codes.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append("{\"value\":").Append(Quote(p.Codes[i].Value))
                  .Append(",\"kind\":").Append(Quote(KindText(p.Codes[i].Kind)))
                  .Append(",\"position\":").Append(Quote(p.Codes[i].Position)).Append('}');
            }
            sb.Append("],\"images\":[");
            AppendImages(sb, p.Images);
            sb.Append("]}");
            return sb.ToString();
        }

        private static string JsonCameraRead(CameraReadEvent c)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"schemaVersion\":").Append(c.SchemaVersion);
            sb.Append(",\"type\":\"camera-read\"");
            sb.Append(",\"providerId\":").Append(Quote(c.ProviderId));
            sb.Append(",\"deviceId\":").Append(Quote(c.DeviceId));
            sb.Append(",\"cameraIp\":").Append(Quote(c.CameraIp));
            sb.Append(",\"capturedAtMs\":").Append(c.CapturedAtMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"receivedAtMs\":").Append(c.ReceivedAtMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"codes\":[");
            for (int i = 0; i < c.Codes.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append("{\"value\":").Append(Quote(c.Codes[i].Value))
                  .Append(",\"kind\":").Append(Quote(KindText(c.Codes[i].Kind))).Append('}');
            }
            sb.Append("],\"images\":[");
            AppendImages(sb, c.Images);
            sb.Append("]}");
            return sb.ToString();
        }

        private static string JsonCameraStatus(CameraStatusEvent s)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"type\":\"camera-status\"");
            sb.Append(",\"providerId\":").Append(Quote(s.ProviderId));
            sb.Append(",\"deviceId\":").Append(Quote(s.DeviceId));
            sb.Append(",\"userId\":").Append(Quote(s.UserId));
            sb.Append(",\"online\":").Append(s.Online ? "true" : "false");
            sb.Append(",\"atMs\":").Append(s.AtMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"isSnapshot\":").Append(s.IsSnapshot ? "true" : "false");
            sb.Append(",\"model\":").Append(Quote(s.Model));
            sb.Append(",\"serialNumber\":").Append(Quote(s.SerialNumber));
            sb.Append(",\"vendor\":").Append(Quote(s.Vendor));
            sb.Append(",\"firmware\":").Append(Quote(s.Firmware));
            sb.Append(",\"offlineCount\":").Append(s.OfflineCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"reconnectCount\":").Append(s.ReconnectCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"lastOfflineAtMs\":").Append(s.LastOfflineAtMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"lastOfflineDurationMs\":").Append(s.LastOfflineDurationMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"firstSeenAtMs\":").Append(s.FirstSeenAtMs.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendImages(StringBuilder sb, System.Collections.Generic.List<ImageRef> images)
        {
            for (int i = 0; i < images.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                ImageRef img = images[i];
                sb.Append("{\"kind\":").Append(Quote(img.Kind.ToString().ToLowerInvariant()))
                  .Append(",\"deviceId\":").Append(Quote(img.DeviceId))
                  .Append(",\"format\":").Append(Quote(img.Format))
                  .Append(",\"width\":").Append(img.Width.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"height\":").Append(img.Height.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"bytes\":").Append(img.Bytes.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"path\":").Append(Quote(img.Path)).Append('}');
            }
        }

        private static string KindText(CodeKind kind)
        {
            if (kind == CodeKind.OneD)
            {
                return "1d";
            }
            if (kind == CodeKind.TwoD)
            {
                return "2d";
            }
            return "unknown";
        }

        private static string Number(double value)
        {
            return Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
        }

        private static string Quote(string value)
        {
            if (value == null)
            {
                return "null";
            }

            StringBuilder sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        #endregion
    }
}
