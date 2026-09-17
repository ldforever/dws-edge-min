using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DwsEdge.Core.Abstractions;
using DwsEdge.Core.Config;
using DwsEdge.Core.Model;
using LogisticsBaseCSharp;

namespace DwsEdge.Providers.Dahua
{
    /// <summary>
    /// 大华 DWS SDK 适配器（方案 B 里的"采集宿主"核心）。
    ///
    /// 职责边界（很重要）：
    ///   1. 只做"设备域"：加载 SDK、挂回调、把图落盘、把结果翻译成规范事件；
    ///   2. 回调线程只做深拷贝 + 入队，绝不写业务、绝不写磁盘；
    ///   3. 所有厂商概念（LogisticsWrapper、OutputResult、VslbImage...）都被关在本文件内，
    ///      外面看到的是 ParcelEvent / CameraReadEvent / CameraStatusEvent。
    /// </summary>
    public sealed class DahuaDwsProvider : IAcquisitionProvider, ITriggerControl, IConfigVerification
    {
        private const string ProviderName = "dahua-dws";
        private const string NoRead = "noread";

        private readonly IEventSink _sink;
        private readonly ProviderSettings _settings;
        private readonly string _cfgPath;
        private readonly string _imageRoot;
        private readonly bool _saveOriginal;
        private readonly bool _saveWaybill;
        private readonly bool _savePerCamera;
        private readonly bool _attachAllCameraCodeInfo;
        private readonly int _queueCapacity;

        private BlockingCollection<WorkItem> _queue;
        private Thread _worker;
        private LogisticsWrapper _dws;
        private volatile bool _running;
        private volatile bool _started;
        private bool _stopped;
        private long _eventSeq;

        private readonly CameraRuntimeTracker _cameraTracker = new CameraRuntimeTracker();
        private CameraPositionMap _cameraPositions;

        private bool _cameraDisconnectCbAttached;
        private bool _allCameraCbAttached;
        private bool _statusHandlerAttached;
        private bool _codeHandlerAttached;
        private bool _allCameraHandlerAttached;

        #region 内部工作项

        private sealed class PendingImage
        {
            public CapturedImage Image;
            public ImageKind Kind;
            public string DeviceId;
            public string Suffix;
        }

        /// <summary>一个待处理的工作项：要么是包裹事件，要么是单相机读码事件。</summary>
        private sealed class WorkItem
        {
            public ParcelEvent Parcel;
            public CameraReadEvent CameraRead;
            public List<PendingImage> Images = new List<PendingImage>();

            public void ReleaseImages()
            {
                for (int i = 0; i < Images.Count; i++)
                {
                    if (Images[i].Image != null)
                    {
                        Images[i].Image.Dispose();
                    }
                }
                Images.Clear();
            }
        }

        #endregion

        public DahuaDwsProvider(ProviderSettings settings, IEventSink sink)
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
            _cfgPath = settings.ResolvePath(settings.Get("cfgPath", @"Cfg\LogisticsBase.cfg"));
            _imageRoot = settings.ResolvePath(settings.Get("imageDir", "images"));
            _saveOriginal = settings.GetBool("saveOriginal", true);
            _saveWaybill = settings.GetBool("saveWaybill", true);
            _savePerCamera = settings.GetBool("savePerCamera", false);
            _attachAllCameraCodeInfo = settings.GetBool("attachAllCameraCodeInfo", _savePerCamera);
            _queueCapacity = Math.Max(8, settings.GetInt("queueCapacity", 256));

            // 相机掉线/恢复的统计日志直接进宿主日志
            _cameraTracker.OnLog = delegate(string message, bool warning)
            {
                _sink.Log(warning ? LogLevel.Warn : LogLevel.Info, message);
            };
        }

        public string ProviderId
        {
            get { return ProviderName; }
        }

        public ProviderCapabilities Capabilities
        {
            get
            {
                return ProviderCapabilities.ParcelAggregation
                     | ProviderCapabilities.CrossCameraDedup
                     | ProviderCapabilities.WaybillCrop
                     | ProviderCapabilities.PerCameraImage
                     | ProviderCapabilities.Weight
                     | ProviderCapabilities.Volume
                     | ProviderCapabilities.SoftTrigger
                     | ProviderCapabilities.ComplementCode
                     | ProviderCapabilities.ConfigWrite
                     | ProviderCapabilities.StagedParcelResult
                     | ProviderCapabilities.RequiresDongle;
            }
        }

        #region 生命周期

        public void Start()
        {
            if (!File.Exists(_cfgPath))
            {
                throw new ProviderException("找不到 SDK 配置文件：" + _cfgPath);
            }

            // 先读 cfg 判断触发模式，这样即使后面相机没连上，也能看到"软触发能不能用"的提示
            WarnIfNotSoftTriggerMode();

            // 启动前自检相机声明（num 与 enable 数量、重复声明等），避免等到 SDK 报 3000
            ValidateCameraPlan();

            // 相机方位映射（回调不带方位时用它兜底）
            LoadCameraPositions();

            _queue = new BlockingCollection<WorkItem>(_queueCapacity);
            _running = true;
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "dahua-image-worker";
            _worker.Start();

            _dws = LogisticsWrapper.Instance;

            _sink.Log(LogLevel.Info, "Initialization(" + _cfgPath + ")");
            int status = _dws.Initialization(_cfgPath);
            if (status != 0)
            {
                throw new ProviderException("Initialization 失败：返回 " + status + "；" + DahuaErrorCodes.Describe(status));
            }

            // 先打开底层回调开关，再注册托管事件（顺序与官方 Demo 一致）
            _cameraDisconnectCbAttached = _dws.AttachCameraDisconnectCB();
            if (_attachAllCameraCodeInfo)
            {
                _allCameraCbAttached = _dws.AttachAllCameraCodeinfoCB();
            }

            _dws.CameraDisconnectEventHandler += OnCameraDisconnect;
            _statusHandlerAttached = true;

            _sink.Log(LogLevel.Info, "Start() —— 底层开始初始化相机/称重/体积等模块");
            status = _dws.Start();
            if (status != 0)
            {
                DetachCallbacks();
                throw new ProviderException("Start 失败：返回 " + status + "；" + DahuaErrorCodes.Describe(status));
            }

            _dws.CodeHandle += OnCodeHandle;
            _codeHandlerAttached = true;

            if (_allCameraCbAttached)
            {
                _dws.AllCameraCodeInfoEventHandler += OnAllCameraCodeInfo;
                _allCameraHandlerAttached = true;
            }

            LogCameraInventory();
            EmitCameraSnapshot();
            _started = true;
            _sink.Log(LogLevel.Info, "采集已启动，图片目录：" + _imageRoot);
        }

        public void Stop()
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;

            _running = false;
            _started = false;

            // 先摘回调，保证不再有新数据进入队列
            DetachCallbacks();

            if (_dws != null)
            {
                try
                {
                    _sink.Log(LogLevel.Info, "StopApp()");
                    bool ok = _dws.StopApp();
                    _sink.Log(LogLevel.Info, "StopApp -> " + ok);
                }
                catch (Exception ex)
                {
                    _sink.LogError("StopApp 异常", ex);
                }
            }

            // 让工作线程把队列里剩下的图片写完再退出
            if (_queue != null)
            {
                try
                {
                    _queue.CompleteAdding();
                }
                catch (Exception)
                {
                }
            }

            if (_worker != null && _worker.IsAlive)
            {
                _worker.Join(10000);
            }
        }

        public void Dispose()
        {
            Stop();
        }

        #endregion

        #region SDK 回调

        /// <summary>
        /// 包裹结果回调（一个包裹来两次：先条码，再条码+重量+体积）。
        /// 这里只做最轻的事：规范化 + 深拷贝图像 + 入队，绝不阻塞。
        /// </summary>
        private void OnCodeHandle(object sender, LogisticsCodeEventArgs e)
        {
            try
            {
                ParcelEvent evt = new ParcelEvent();
                evt.EventId = Interlocked.Increment(ref _eventSeq);
                evt.ProviderId = ProviderName;
                evt.DeviceId = e.CameraID;
                evt.Stage = e.OutputResult == 0 ? ParcelStage.Detected : ParcelStage.Enriched;
                evt.CapturedAtMs = e.CodeTimeStamp;
                evt.ReceivedAtMs = NowMs();

                FillCodes(evt, e);
                ApplyPositionFallback(evt.Codes, e.CameraID);
                FillWeightAndVolume(evt, e);
                evt.TraceId = BuildTraceId(evt);

                WorkItem item = new WorkItem();
                item.Parcel = evt;

                if (_saveOriginal)
                {
                    CapturedImage original = CapturedImage.From(e.OriginalImage);
                    if (original != null)
                    {
                        PendingImage pi = new PendingImage();
                        pi.Image = original;
                        pi.Kind = ImageKind.Original;
                        pi.DeviceId = e.CameraID;
                        pi.Suffix = "ori";
                        item.Images.Add(pi);
                    }
                }

                if (_saveWaybill)
                {
                    CapturedImage waybill = CapturedImage.From(e.WaybillImage);
                    if (waybill != null)
                    {
                        PendingImage pi = new PendingImage();
                        pi.Image = waybill;
                        pi.Kind = ImageKind.Waybill;
                        pi.DeviceId = e.CameraID;
                        pi.Suffix = "way";
                        item.Images.Add(pi);
                    }
                }

                Enqueue(item);
            }
            catch (Exception ex)
            {
                _sink.LogError("OnCodeHandle 异常", ex);
            }
        }

        /// <summary>所有相机的读码信息回调（可选）。</summary>
        private void OnAllCameraCodeInfo(object sender, AllCameraCodeInfoArgs e)
        {
            try
            {
                if (e == null || e.SingleCameraCodeInfoList == null)
                {
                    return;
                }

                foreach (SingleCameraCodeInfo info in e.SingleCameraCodeInfoList)
                {
                    CameraReadEvent read = new CameraReadEvent();
                    read.ProviderId = ProviderName;
                    read.DeviceId = info.Key;
                    read.CameraIp = info.CameraIP;
                    read.CapturedAtMs = info.CodeTimeStamp;
                    read.ReceivedAtMs = NowMs();

                    if (info.CodeList != null)
                    {
                        for (int i = 0; i < info.CodeList.Count; i++)
                        {
                            string value = info.CodeList[i];
                            if (!IsNoRead(value))
                            {
                                read.Codes.Add(new CodeItem(value, CodeKind.Unknown, null));
                            }
                        }
                    }

                    WorkItem item = new WorkItem();
                    item.CameraRead = read;

                    ApplyPositionFallback(read.Codes, info.Key);

                    if (_savePerCamera)
                    {
                        CapturedImage image = CapturedImage.From(info.OriginalImage);
                        if (image != null)
                        {
                            PendingImage pi = new PendingImage();
                            pi.Image = image;
                            pi.Kind = ImageKind.PerCamera;
                            pi.DeviceId = info.Key;
                            pi.Suffix = "cam";
                            item.Images.Add(pi);
                        }
                    }

                    Enqueue(item);
                }
            }
            catch (Exception ex)
            {
                _sink.LogError("OnAllCameraCodeInfo 异常", ex);
            }
        }

        /// <summary>相机上下线回调。</summary>
        private void OnCameraDisconnect(object sender, CameraStatusArgs e)
        {
            try
            {
                CameraStatusEvent status = new CameraStatusEvent();
                status.ProviderId = ProviderName;
                status.DeviceId = e.CameraKey;
                status.UserId = e.CameraUserID;
                status.Online = e.IsOnline;
                status.AtMs = NowMs();

                _cameraTracker.Apply(status, false);
                _sink.OnCameraStatus(status);
            }
            catch (Exception ex)
            {
                _sink.LogError("OnCameraDisconnect 异常", ex);
            }
        }

        #endregion

        #region 命令接口（软触发 / 补码）

        /// <summary>
        /// 软件触发一次相机拍照/拉流（对应大华 CameraSoftTrigger / 原生 vslbSoftTrigger）。
        ///
        /// 前提：cfg 里 &lt;ReadCodeMode triggerMode="2"&gt;（软触发模式）。
        /// 如果还是 1（硬触发）或 0（自由拉流），本命令可能不生效——启动时会打 WARN 提示。
        /// </summary>
        public int SoftTrigger()
        {
            if (!_started || _dws == null)
            {
                _sink.Log(LogLevel.Warn, "软触发失败：采集尚未启动（先 Start 成功再触发）");
                return -1;
            }

            try
            {
                int ret = _dws.CameraSoftTrigger();
                if (ret == 0)
                {
                    _sink.Log(LogLevel.Info, "软触发成功：CameraSoftTrigger() -> 0");
                }
                else
                {
                    _sink.Log(LogLevel.Warn, "软触发失败：CameraSoftTrigger() -> " + ret + "；" + DahuaErrorCodes.Describe(ret));
                }
                return ret;
            }
            catch (Exception ex)
            {
                _sink.LogError("软触发异常", ex);
                return -1;
            }
        }

        /// <summary>
        /// 人工补码（对应大华 ComplementCode）。
        /// </summary>
        public int ComplementCode(string code, long timeMs)
        {
            if (!_started || _dws == null)
            {
                _sink.Log(LogLevel.Warn, "补码失败：采集尚未启动");
                return -1;
            }
            if (string.IsNullOrEmpty(code))
            {
                _sink.Log(LogLevel.Warn, "补码失败：条码为空");
                return -1;
            }

            try
            {
                ComplementInfo info = new ComplementInfo();
                info.Code = code;
                info.time = timeMs > 0 ? timeMs : NowMs();

                int ret = _dws.ComplementCode(info);
                if (ret == 0)
                {
                    _sink.Log(LogLevel.Info, "补码成功：" + code + "（时间戳 " + info.time + "）");
                }
                else
                {
                    _sink.Log(LogLevel.Warn, "补码失败：" + code + " -> " + ret + "；" + DahuaErrorCodes.Describe(ret));
                }
                return ret;
            }
            catch (Exception ex)
            {
                _sink.LogError("补码异常", ex);
                return -1;
            }
        }

        #endregion

        #region 工作线程

        private void Enqueue(WorkItem item)
        {
            bool added = false;
            try
            {
                added = _queue.TryAdd(item);
            }
            catch (Exception ex)
            {
                _sink.LogError("入队异常", ex);
            }

            if (!added)
            {
                // 队列满：宁可丢事件也不反压 SDK 回调（丢多少必须有告警，方便现场发现）
                _sink.Log(LogLevel.Warn, "采集队列已满（容量 " + _queueCapacity + "），丢弃一个事件");
                item.ReleaseImages();
            }
        }

        private void WorkerLoop()
        {
            while (true)
            {
                WorkItem item = null;
                try
                {
                    if (!_queue.TryTake(out item, 200))
                    {
                        if (!_running && _queue.IsCompleted)
                        {
                            break;
                        }
                        continue;
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                try
                {
                    ProcessOne(item);
                }
                catch (Exception ex)
                {
                    _sink.LogError("处理事件异常", ex);
                }
                finally
                {
                    item.ReleaseImages();
                }
            }
        }

        private void ProcessOne(WorkItem item)
        {
            if (item.Parcel != null)
            {
                string directory = Path.Combine(_imageRoot, DateTime.Now.ToString("yyyyMMdd"), SafeName(item.Parcel.DeviceId));
                for (int i = 0; i < item.Images.Count; i++)
                {
                    SaveOne(item.Images[i], item.Parcel, null, directory);
                }
                _sink.OnParcel(item.Parcel);
                return;
            }

            if (item.CameraRead != null)
            {
                string directory = Path.Combine(_imageRoot, DateTime.Now.ToString("yyyyMMdd"), SafeName(item.CameraRead.DeviceId));
                for (int i = 0; i < item.Images.Count; i++)
                {
                    SaveOne(item.Images[i], null, item.CameraRead, directory);
                }
                _sink.OnCameraRead(item.CameraRead);
            }
        }

        private void SaveOne(PendingImage pending, ParcelEvent parcel, CameraReadEvent cameraRead, string directory)
        {
            if (pending == null || pending.Image == null)
            {
                return;
            }

            string baseName = BuildFileBaseName(parcel, cameraRead, pending.Suffix);

            try
            {
                bool isJpeg = pending.Image.Type == (int)LogisticsAPIStruct.EImageType.eImageTypeJpeg;
                int channels = pending.Image.Type == (int)LogisticsAPIStruct.EImageType.eImageTypeBGR ? 3 : 1;

                ImageRef imageRef = ImageWriter.Write(pending.Image, isJpeg, channels, directory, baseName, pending.Kind, pending.DeviceId);
                if (imageRef == null)
                {
                    return;
                }

                if (parcel != null)
                {
                    parcel.Images.Add(imageRef);
                }
                if (cameraRead != null)
                {
                    cameraRead.Images.Add(imageRef);
                }

                _sink.OnImageSaved(imageRef);
            }
            catch (Exception ex)
            {
                _sink.LogError("保存图片失败：" + baseName, ex);
            }
        }

        #endregion

        #region 辅助

        private void DetachCallbacks()
        {
            if (_dws == null)
            {
                return;
            }

            try
            {
                if (_codeHandlerAttached)
                {
                    _dws.CodeHandle -= OnCodeHandle;
                    _codeHandlerAttached = false;
                }
                if (_allCameraHandlerAttached)
                {
                    _dws.AllCameraCodeInfoEventHandler -= OnAllCameraCodeInfo;
                    _allCameraHandlerAttached = false;
                }
                if (_statusHandlerAttached)
                {
                    _dws.CameraDisconnectEventHandler -= OnCameraDisconnect;
                    _statusHandlerAttached = false;
                }
                if (_allCameraCbAttached)
                {
                    _dws.DetachAllCameraCodeinfoCB();
                    _allCameraCbAttached = false;
                }
                if (_cameraDisconnectCbAttached)
                {
                    _dws.DetachCameraDisconnectCB();
                    _cameraDisconnectCbAttached = false;
                }
            }
            catch (Exception ex)
            {
                _sink.LogError("卸载回调异常", ex);
            }
        }

        private void LogCameraInventory()
        {
            try
            {
                int count = 0;
                IEnumerable<CameraInfo> infos = _dws.GetWorkCameraInfo();
                if (infos != null)
                {
                    foreach (CameraInfo info in infos)
                    {
                        count++;
                        _sink.Log(LogLevel.Info, string.Format(CultureInfo.InvariantCulture,
                            "相机[{0}] ID={1} Model={2} SN={3} Vendor={4} FW={5} Extra={6}",
                            count, info.camDevID, info.camDevModelName, info.camDevSerialNumber,
                            info.camDevVendor, info.camDevFirewareVersion, info.camDevExtraInfo));
                    }
                }
                _sink.Log(LogLevel.Info, "工作相机数量：" + count);

                IEnumerable<CameraTags> statusList = _dws.GetCamerasStatus();
                if (statusList != null)
                {
                    foreach (CameraTags st in statusList)
                    {
                        _sink.Log(LogLevel.Info, string.Format(CultureInfo.InvariantCulture,
                            "相机状态 Key={0} UserID={1} Online={2}", st.key, st.deviceUserID, st.isOnline));
                    }
                }
            }
            catch (Exception ex)
            {
                _sink.LogError("读取相机信息失败", ex);
            }
        }

        /// <summary>
        /// 启动前自检 cfg 里的相机声明：
        ///   mode=2 时 num 必须等于 enable="1" 的数量；num 至少为 1（超过 20 只提醒、不拦截）；不能有重复/空声明。
        /// 不通过就直接抛错，并把问题一次列清楚，省得现场等 SDK 报 3000。
        /// </summary>
        private void ValidateCameraPlan()
        {
            CameraPlan plan = CameraPlan.Read(_cfgPath);

            _sink.Log(LogLevel.Info, "cfg 相机计划：" + plan.Describe());
            for (int i = 0; i < plan.Cameras.Count; i++)
            {
                _sink.Log(LogLevel.Info, "  声明 " + (i + 1) + "：" + plan.Cameras[i]);
            }

            List<string> warnings = plan.Warnings();
            for (int i = 0; i < warnings.Count; i++)
            {
                _sink.Log(LogLevel.Warn, "相机配置提醒：" + warnings[i]);
            }

            List<string> errors = plan.Errors();
            if (errors.Count == 0)
            {
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("cfg 相机配置自检未通过（").Append(_cfgPath).Append("）：");
            for (int i = 0; i < errors.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append("；");
                }
                sb.Append(errors[i]);
            }
            sb.Append("。可用 tools\\make-camera-cfg.ps1 重新生成相机清单。");
            throw new ProviderException(sb.ToString());
        }

        /// <summary>
        /// 应用配置后的回读校验：cfg 是否合规、SDK 是否真的按配置工作（工作相机数量）。
        /// 供宿主 --verify-config 使用；失败时返回 Problems，宿主据此决定是否回滚配置。
        /// </summary>
        public ConfigVerificationReport VerifyConfig()
        {
            ConfigVerificationReport report = new ConfigVerificationReport();

            try
            {
                CameraPlan plan = CameraPlan.Read(_cfgPath);
                report.Details.Add("配置回读：" + plan.Describe());
                for (int i = 0; i < plan.Cameras.Count; i++)
                {
                    report.Details.Add("  声明 " + (i + 1) + "：" + plan.Cameras[i]);
                }

                report.Problems.AddRange(plan.Errors());

                int cameraCount = -1;
                if (_dws != null)
                {
                    try
                    {
                        cameraCount = _dws.GetWorkCameraCount();
                    }
                    catch (Exception ex)
                    {
                        report.Problems.Add("读取工作相机数量失败：" + ex.Message);
                    }
                }

                if (cameraCount >= 0)
                {
                    report.Details.Add("SDK 上报工作相机数量：" + cameraCount);
                    int num = plan.NumValue;
                    if (num > 0 && cameraCount < num)
                    {
                        report.Problems.Add("SDK 实际工作相机 " + cameraCount + " 台，少于配置的 num=" + num);
                    }
                }
                else
                {
                    report.Problems.Add("未能获取工作相机数量（SDK 未启动或接口不可用）");
                }
            }
            catch (Exception ex)
            {
                report.Problems.Add("校验异常：" + ex.Message);
            }

            report.Success = report.Problems.Count == 0;
            return report;
        }

        /// <summary>
        /// 加载"相机 → 方位"映射文件（默认 runtime\config\camera-positions.ini）。
        /// 大华回调里的 CodesInfo.Position 常常为空，这张表用来兜底。
        /// </summary>
        private void LoadCameraPositions()
        {
            string path = _settings.ResolvePath(_settings.Get("cameraPositionsFile", @"config\camera-positions.ini"));
            _cameraPositions = CameraPositionMap.Load(path);

            if (_cameraPositions.Count > 0)
            {
                _sink.Log(LogLevel.Info, "已加载相机方位映射 " + _cameraPositions.Count + " 条：" + path);
            }
            else
            {
                _sink.Log(LogLevel.Info, "未配置相机方位映射（" + path
                    + "）；若相机回调不带方位，条码方位会为空。可用 tools\\make-camera-cfg.ps1 的 pos= 生成");
            }
        }

        /// <summary>回调没给方位时，用相机清单里的方位映射补上。</summary>
        private void ApplyPositionFallback(List<CodeItem> codes, string cameraId)
        {
            if (_cameraPositions == null || codes == null || codes.Count == 0)
            {
                return;
            }

            bool needsFallback = false;
            for (int i = 0; i < codes.Count; i++)
            {
                if (string.IsNullOrEmpty(codes[i].Position))
                {
                    needsFallback = true;
                    break;
                }
            }
            if (!needsFallback)
            {
                return;
            }

            string position = _cameraPositions.Resolve(cameraId);
            if (string.IsNullOrEmpty(position))
            {
                return;
            }

            for (int i = 0; i < codes.Count; i++)
            {
                if (string.IsNullOrEmpty(codes[i].Position))
                {
                    codes[i].Position = position;
                }
            }
        }

        /// <summary>
        /// 启动时把相机清单作为"快照事件"推给业务平台（IsSnapshot=true），
        /// 这样平台一启动就能显示完整的相机状态墙，而不是等到第一次掉线才有数据。
        /// </summary>
        private void EmitCameraSnapshot()
        {
            try
            {
                Dictionary<string, CameraInfo> infoByKey =
                    new Dictionary<string, CameraInfo>(StringComparer.OrdinalIgnoreCase);
                IEnumerable<CameraInfo> infos = _dws.GetWorkCameraInfo();
                if (infos != null)
                {
                    foreach (CameraInfo info in infos)
                    {
                        string key = !string.IsNullOrEmpty(info.camDevExtraInfo) ? info.camDevExtraInfo : info.camDevID;
                        if (!string.IsNullOrEmpty(key) && !infoByKey.ContainsKey(key))
                        {
                            infoByKey[key] = info;
                        }
                    }
                }

                HashSet<string> sentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int sent = 0;

                IEnumerable<CameraTags> statusList = _dws.GetCamerasStatus();
                if (statusList != null)
                {
                    foreach (CameraTags tag in statusList)
                    {
                        CameraStatusEvent evt = new CameraStatusEvent();
                        evt.ProviderId = ProviderName;
                        evt.DeviceId = tag.key;
                        evt.UserId = tag.deviceUserID;
                        evt.Online = tag.isOnline;
                        evt.AtMs = NowMs();
                        evt.IsSnapshot = true;

                        CameraInfo info;
                        if (!string.IsNullOrEmpty(tag.key) && infoByKey.TryGetValue(tag.key, out info))
                        {
                            evt.Model = info.camDevModelName;
                            evt.SerialNumber = info.camDevSerialNumber;
                            evt.Vendor = info.camDevVendor;
                            evt.Firmware = info.camDevFirewareVersion;
                        }

                        _cameraTracker.Apply(evt, true);
                        _sink.OnCameraStatus(evt);
                        if (!string.IsNullOrEmpty(tag.key))
                        {
                            sentKeys.Add(tag.key);
                        }
                        sent++;
                    }
                }

                // 工作相机清单里没被状态列表覆盖的，也补一条（视为在线）
                foreach (KeyValuePair<string, CameraInfo> pair in infoByKey)
                {
                    if (sentKeys.Contains(pair.Key))
                    {
                        continue;
                    }

                    CameraStatusEvent evt = new CameraStatusEvent();
                    evt.ProviderId = ProviderName;
                    evt.DeviceId = pair.Key;
                    evt.UserId = pair.Value.camDevID;
                    evt.Online = true;
                    evt.AtMs = NowMs();
                    evt.IsSnapshot = true;
                    evt.Model = pair.Value.camDevModelName;
                    evt.SerialNumber = pair.Value.camDevSerialNumber;
                    evt.Vendor = pair.Value.camDevVendor;
                    evt.Firmware = pair.Value.camDevFirewareVersion;

                    _cameraTracker.Apply(evt, true);
                    _sink.OnCameraStatus(evt);
                    sent++;
                }

                _sink.Log(LogLevel.Info, "已上报相机快照 " + sent + " 台");
            }
            catch (Exception ex)
            {
                _sink.LogError("上报相机快照失败", ex);
            }
        }

        private static void FillCodes(ParcelEvent evt, LogisticsCodeEventArgs e)
        {
            // CodesInfo 带方位和类型，优先用它
            if (e.CodesInfo != null && e.CodesInfo.Length > 0)
            {
                for (int i = 0; i < e.CodesInfo.Length; i++)
                {
                    SingleCodeInfo info = e.CodesInfo[i];
                    if (info == null || IsNoRead(info.Code))
                    {
                        continue;
                    }

                    CodeKind kind = info.CodeTypeP == SingleCodeInfo.CodeType.Barcode ? CodeKind.OneD : CodeKind.TwoD;
                    evt.Codes.Add(new CodeItem(info.Code, kind, info.Position));
                }
                return;
            }

            if (e.CodeList != null)
            {
                for (int i = 0; i < e.CodeList.Count; i++)
                {
                    string value = e.CodeList[i];
                    if (!IsNoRead(value))
                    {
                        evt.Codes.Add(new CodeItem(value, CodeKind.Unknown, null));
                    }
                }
            }
        }

        private static void FillWeightAndVolume(ParcelEvent evt, LogisticsCodeEventArgs e)
        {
            if (e.OutputResult == 0)
            {
                return;
            }

            if (e.Weight > 0)
            {
                evt.WeightGrams = e.Weight;
            }

            try
            {
                evt.LengthMm = e.VolumeInfo.length;
                evt.WidthMm = e.VolumeInfo.width;
                evt.HeightMm = e.VolumeInfo.height;
                evt.VolumeMm3 = e.VolumeInfo.volume;
            }
            catch (Exception)
            {
                // 没有体积模块时忽略
            }
        }

        private static string BuildTraceId(ParcelEvent evt)
        {
            // 统一走 Core 的规则，保证所有 provider 生成的追踪号格式一致
            return ParcelTrace.Build(evt.ProviderId, evt.DeviceId, evt.CapturedAtMs, evt.Codes);
        }

        private static string BuildFileBaseName(ParcelEvent parcel, CameraReadEvent cameraRead, string suffix)
        {
            StringBuilder sb = new StringBuilder();
            if (parcel != null)
            {
                sb.Append(parcel.CapturedAtMs.ToString(CultureInfo.InvariantCulture));
                if (parcel.Codes.Count > 0)
                {
                    sb.Append('_');
                    int max = Math.Min(3, parcel.Codes.Count);
                    for (int i = 0; i < max; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append('-');
                        }
                        sb.Append(parcel.Codes[i].Value);
                    }
                }
            }
            else if (cameraRead != null)
            {
                sb.Append(cameraRead.CapturedAtMs.ToString(CultureInfo.InvariantCulture));
                if (cameraRead.Codes.Count > 0)
                {
                    sb.Append('_').Append(cameraRead.Codes[0].Value);
                }
            }

            sb.Append('_').Append(suffix);
            string name = SafeName(sb.ToString());
            if (name.Length > 120)
            {
                name = name.Substring(0, 120);
            }
            return name;
        }

        private static string SafeName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "unknown";
            }

            StringBuilder sb = new StringBuilder(value.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool bad = false;
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (c == invalid[j])
                    {
                        bad = true;
                        break;
                    }
                }
                sb.Append(bad ? '_' : c);
            }

            string result = sb.ToString().Trim();
            return result.Length == 0 ? "unknown" : result;
        }

        private static bool IsNoRead(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Trim().Equals(NoRead, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 启动时检查 cfg 的 ReadCodeMode.triggerMode：
        ///   0 = 自由拉流，1 = 硬触发（默认），2 = 软触发。
        /// 不是 2 就提示一次，避免现场"软触发没反应"。
        /// </summary>
        private void WarnIfNotSoftTriggerMode()
        {
            string mode = TryReadTriggerMode(_cfgPath);
            if (mode == null)
            {
                return;
            }

            if (mode == "2")
            {
                _sink.Log(LogLevel.Info, "ReadCodeMode.triggerMode=2（软触发模式），可以使用 CameraSoftTrigger()");
            }
            else
            {
                _sink.Log(LogLevel.Warn, "ReadCodeMode.triggerMode=" + mode
                    + "（0=自由拉流，1=硬触发，2=软触发）；当前不是软触发模式，CameraSoftTrigger() 可能不生效。"
                    + "要测软触发请把 Cfg\\LogisticsBase.cfg 改成 triggerMode=\"2\" 后重启采集"
                    + "（也可用 tools\\set-trigger-mode.ps1 -Mode soft）");
            }
        }

        /// <summary>
        /// 从 GB2312 编码的 cfg 里读出 ReadCodeMode 的 triggerMode 值。
        /// 这里按字节找 ASCII 片段，完全绕开编码问题。
        /// </summary>
        private static string TryReadTriggerMode(string cfgPath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(cfgPath);
                byte[] anchor = Encoding.ASCII.GetBytes("<ReadCodeMode");
                int anchorIndex = IndexOf(bytes, anchor, 0);
                if (anchorIndex < 0)
                {
                    return null;
                }

                byte[] needle = Encoding.ASCII.GetBytes("triggerMode=\"");
                int index = IndexOf(bytes, needle, anchorIndex);
                if (index < 0)
                {
                    return null;
                }

                int start = index + needle.Length;
                int end = start;
                while (end < bytes.Length && bytes[end] != (byte)'"')
                {
                    end++;
                }
                if (end <= start)
                {
                    return null;
                }

                return Encoding.ASCII.GetString(bytes, start, end - start);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            int limit = haystack.Length - needle.Length;
            for (int i = Math.Max(0, start); i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return i;
                }
            }
            return -1;
        }

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        #endregion
    }
}
