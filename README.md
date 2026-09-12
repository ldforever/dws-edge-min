# DWS Edge Min —— 方案 B 的采集宿主最小模型

基于大华 DWS SDK 的最小可运行工程：**连一台相机、拿到条码和图片、把结果翻译成厂商无关的规范事件**。
它是方案 B 里 `DwsEdge`（采集宿主）这一层的骨架，业务逻辑、下游对接、Web 前端都不在这里。

## 1. 它做了什么，没做什么

做了：

- 加载大华 SDK（`LogisticsBase64.dll` + `LogisticsBaseCSharp.dll`），按 `Cfg\LogisticsBase.cfg` 初始化；
- 订阅回调，拿到包裹条码 / 原图 / 面单抠图（可选：每台相机各自的图）；
- 图片异步落盘到 `images\yyyyMMdd\<相机>\`；
- 把大华的结果翻译成规范事件（`ParcelEvent` / `CameraReadEvent` / `CameraStatusEvent`）；
- 事件写到 `spool\events-yyyyMMdd.jsonl`（将来把这里换成 gRPC 推给业务层 / ARM 盒子即可）；
- 相机上线/掉线、启动失败（含返回码翻译）都有日志。

没做（刻意不做）：

- 没有业务规则、去重、下游协议（TCP/HTTP/PLC）、数据库、界面；
- 没有本地解码（解码在智能相机端）；
- 没有把图片字节塞进事件——事件里只有路径。

## 2. 目录结构

```
dws-edge-min/
├─ build.ps1                     编译脚本（只用 .NET Framework 自带的 csc.exe，不需要 VS）
├─ run.ps1                       运行脚本
├─ DwsEdge.sln                   VS 工程（可选，命令行用 build.ps1 即可）
├─ tools/
│  └─ set-trigger-mode.ps1       切换大华 cfg 的触发模式（soft / hard / free，自动备份）
├─ config/
│  └─ gateway.ini                宿主配置：选哪个 provider + provider 的参数
├─ src/
│  ├─ DwsEdge.Core/              厂商无关：规范模型 + provider 接口（不引用任何 SDK）
│  │  ├─ Model/                  CodeItem / ImageRef / ParcelEvent / CameraReadEvent / CameraStatusEvent
│  │  └─ Abstractions/           IAcquisitionProvider / IAcquisitionProviderFactory / ITriggerControl
│  │                              / IEventSink / ProviderCapabilities / ProviderSettings
│  ├─ DwsEdge.Providers.Dahua/   唯一引用大华 SDK 的工程
│  │  ├─ DahuaDwsProvider.cs     SDK 适配：生命周期 + 回调 + 队列 + 事件翻译
│  │  ├─ CapturedImage.cs        SDK 非托管图像 → 可跨线程使用的深拷贝
│  │  ├─ ImageWriter.cs          落盘（JPEG 直存 / 原始图写 BMP，无第三方依赖）
│  │  ├─ DahuaErrorCodes.cs      返回码翻译（2200 没加密狗 / 3000 相机没连上 …）
│  │  └─ DahuaDwsProviderFactory.cs  插件入口
│  ├─ DwsEdge.Providers.Simulator/   测试用模拟 provider（不需要相机和加密狗）
│  │  ├─ SimulatorProvider.cs    软触发一次 → 生成假条码 + 假图片 + 两条事件
│  │  └─ SimulatorProviderFactory.cs
│  └─ DwsEdge.Host/              宿主 exe：加载插件、消费事件、写 spool
│     ├─ Program.cs              启动流程 + 错误处理 + --duration 冒烟模式
│     ├─ ProviderRegistry.cs     扫描 runtime\providers\*.dll，反射加载 provider
│     ├─ SimpleConfig.cs         极简 INI 解析
│     ├─ TestConsole.cs          软触发测试开关（启动触发 / 定时触发 / 交互触发 / 补码）
│     └─ HostEventSink.cs        控制台 / 文件日志 / JSONL spool
└─ runtime/                      运行时目录（大华 SDK 的所有 DLL、Cfg、3DCfg 都在这里）
   ├─ Cfg\LogisticsBase.cfg      已改成"单相机"模式（见第 4 步）
   ├─ providers\                 provider 插件 DLL
   ├─ images\  spool\  logs\     运行产物
   └─ Log\                       大华 SDK 自己的日志（default.log 里能看到底层原因）
```

`runtime\` 目录里已经放好了大华 SDK 的运行时（约 59MB，来自 `DWS_Demo_C#_Win64_V3.8.006...zip` 的 `bin\Release\x64`，去掉了 pdb、示例 exe、Documents 和 118MB 的 SmartMattingPlugin）。

## 3. 前置条件

- Windows x64；
- .NET Framework 4.x（Win10/11 自带，`build.ps1` 用的就是它自带的 `csc.exe`）；
- 大华 USB 加密狗（`SoftDogControl` 未开也要，`Start()` 返回 2200 就是没插）；
- 一台大华智能相机 / 读码器，和本机在同一网段；
- 关掉 MVViewer / EasyID 等会独占相机的工具。

## 4. 快速开始

**第 1 步：改相机 IP。** 编辑 `runtime\Cfg\LogisticsBase.cfg`，找到 `<ImageAcqCfg>` 里这两处（已经是单相机模板，只要把 IP 换成现场相机）：

```xml
<ImageAcq mode="2" num="1" ... />
<Camera ip="192.168.1.10" enable="1" />
```

- `mode="2"` = 按 IP/Key 指定相机；`num` 必须等于 `enable="1"` 的相机数量；
- 换 Key 就写 `<Camera key="序列号" enable="1" />`，换 Id 就写 `<Camera id="厂商:序列号" enable="1" />`；
- 想恢复默认配置，用 `runtime\OriCfg\LogisticsBase.cfg` 覆盖回去；
- 这个文件是 **GB2312 编码**，用支持编码的编辑器改（VS Code 右下角切 GB2312，或记事本另存 ANSI）。

**第 2 步：编译。**

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

**第 3 步：运行。**

```powershell
powershell -ExecutionPolicy Bypass -File .\run.ps1                        # 常驻，Ctrl+C 停止
powershell -ExecutionPolicy Bypass -File .\run.ps1 -Duration 30           # 跑 30 秒自动退出（冒烟）
powershell -ExecutionPolicy Bypass -File .\run.ps1 -TriggerOnce -Duration 8   # 启动后软触发一次
powershell -ExecutionPolicy Bypass -File .\run.ps1 -TriggerInterval 3000     # 每 3 秒软触发一次
```

## 5. 跑起来你会看到什么

正常启动：

```
[10:45:26.310][info] 已加载 provider 插件：dahua-dws（DwsEdge.Providers.Dahua.dll）
[10:45:26.318][info] Initialization(...\runtime\Cfg\LogisticsBase.cfg)
[10:45:26.373][info] Start() —— 底层开始初始化相机/称重/体积等模块
[10:45:26.5xx][info] 相机[1] ID=... Model=... SN=... Vendor=... FW=... Extra=...
[10:45:26.5xx][info] 工作相机数量：1
[10:45:26.5xx][info] 采集已启动，图片目录：...\runtime\images
[10:45:31.120][parcel] 条码  相机=192.168.1.10  条码数=1  [fr:YT1234567890]  图=2  累计包裹=1  累计NOREAD=0
```

同一条包裹信息也会写进 `spool\events-<日期>.jsonl`：

```json
{"schemaVersion":1,"type":"parcel","eventId":2,"providerId":"dahua-dws","deviceId":"192.168.1.10",
 "stage":"enriched","capturedAtMs":1757650000000,"receivedAtMs":1757650000123,"traceId":"...",
 "weightGrams":-1,"lengthMm":0,"widthMm":0,"heightMm":0,"volumeMm3":0,
 "codes":[{"value":"YT1234567890","kind":"1d","position":"fr"}],
 "images":[{"kind":"original","format":"jpg","width":2448,"height":2048,"bytes":332211,"path":"...\\images\\20260912\\192.168.1.10\\....jpg"}]}
```

没有相机时（本机验证就是这样）：

```
[host] provider 启动失败：Start 失败：返回 3000；相机数与配置不符 / 没有相机连上：确认相机 IP 与
Cfg 中 <Camera ... enable="1"> 一致，<ImageAcq num> 与实际在线数一致，本机与相机同网段
```

## 6. 软触发与测试开关

### 6.1 真机软触发（大华 SDK）

大华支持三种触发方式，由 cfg 的 `<ReadCodeMode triggerMode="...">` 决定：

| triggerMode | 含义 | 触发源 |
|---|---|---|
| 0 | 自由拉流（狂扫） | 不需要触发，连续出流 |
| 1 | 硬触发（默认） | 光电传感器接相机 IO |
| 2 | 软触发 | 软件调用 `CameraSoftTrigger()`（原生 `vslbSoftTrigger`） |

要测软触发**必须先把 cfg 改成 2**，否则相机在等光电信号，软触发命令可能没反应。项目里带了个小工具：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\set-trigger-mode.ps1 -Mode soft   # 软触发
powershell -ExecutionPolicy Bypass -File .\tools\set-trigger-mode.ps1 -Mode hard   # 硬触发（默认）
powershell -ExecutionPolicy Bypass -File .\tools\set-trigger-mode.ps1 -Mode free   # 自由拉流
```

工具会自动备份 cfg，并且只改 `triggerMode` 那一个字节。启动时宿主会打印当前模式（`ReadCodeMode.triggerMode=2（软触发模式）`；不是 2 会给 WARN 提示）。**改完要重启 `DwsEdge.Host`**，因为 SDK 只在初始化时读配置。

调用链：`ITriggerControl.SoftTrigger()`（Core 接口）→ `DahuaDwsProvider.SoftTrigger()` → `LogisticsWrapper.CameraSoftTrigger()`；同一个接口上还有 `ComplementCode(code, timeMs)`，对应大华的补码接口。

### 6.2 测试开关

`config\gateway.ini` 的 `[test]` 段（默认全关）：

```ini
[test]
enableSoftTrigger=false   # 总开关，false 时下面的触发都不会执行
triggerOnStart=false      # 启动后自动触发一次
triggerDelayMs=1500       # 启动触发的延迟（毫秒）
triggerIntervalMs=0       # 每隔 N 毫秒自动触发一次（模拟连续过包），0=关闭
```

命令行可以覆盖，不用改配置：

| 参数 | 作用 |
|---|---|
| `--trigger-once` | 启动后软触发一次 |
| `--trigger-interval 3000` | 每 3 秒触发一次，模拟连续过包 |
| `--trigger-delay 800` | 启动触发的延迟毫秒 |

运行时还能在控制台交互：

| 输入 | 作用 |
|---|---|
| 回车 或 `t` | 软触发一次 |
| `c <条码>` | 补码一次 |
| `q` | 退出程序 |

开关关闭且不带命令行参数时，程序不会发送任何触发命令，行为和之前完全一致。

### 6.3 无相机也能测：模拟 provider

把 `config\gateway.ini` 的 provider 换成 `simulator`，不需要相机和加密狗，就能把"触发 → 事件 → 落盘 → spool"整条链路跑通：

```ini
[runtime]
provider=simulator
```

```powershell
powershell -ExecutionPolicy Bypass -File .\run.ps1 -TriggerOnce -Duration 6
```

每次触发它会：生成一个 `TEST131419001` 这样的测试条码 → 生成一张 320×240 的 BMP 假图落到 `runtime\images\<日期>\simulator\` → 按大华的节奏发两条事件（`detected` 只有条码，`enriched` 带重量体积），两条 `traceId` 相同。实测输出：

```
[13:14:19.613][parcel] 条码  相机=simulator-cam  条码数=1  [top:TEST131419001]  图=1  累计包裹=1  累计NOREAD=0
[13:14:19.617][parcel] 条码+重量体积  相机=simulator-cam  条码数=1  [top:TEST131419001]  重量=501g  体积=9000000mm3  图=0  累计包裹=2  累计NOREAD=0
[13:14:19.617][info] [test] 软触发（启动自动触发）返回 0
```

测完把 provider 改回 `dahua-dws`。

### 6.4 注意事项

- `CameraSoftTrigger()` **没有参数**，是"全局触发一次"，不能指定只触发某一台相机；
- 它要求 `Start()` 已经成功（provider 内部会判断，未启动时返回 -1 并打 WARN）；
- 生产方案通常是硬触发或狂扫，软触发更多用于调试、无光电工位、人工补拍；
- 这个接口没被《DWS SDK C# 接口文档》收录（文档只有生命周期和回调），依赖的是 `LogisticsBaseCSharp.dll` 里的实际方法，升级 SDK 时要回归验证。

## 7. 代码怎么读

### 6.1 分层（这套结构就是方案 B 的骨架）

```
DwsEdge.Host（宿主 exe）
   │  只认 IEventSink 和 provider 插件，不引用任何厂商程序集
   ▼
DwsEdge.Core（厂商无关）
   │  规范模型 + 接口：ParcelEvent / CameraReadEvent / CameraStatusEvent / IAcquisitionProvider
   ▲
DwsEdge.Providers.Dahua（插件 DLL，唯一碰 SDK 的地方）
      LogisticsWrapper → 回调 → 深拷贝图像 → 有界队列 → 落盘 + 发事件
```

### 6.2 启动流程（`Program.cs` → `DahuaDwsProvider.Start()`）

1. `Program` 读 `config\gateway.ini`，得到 `provider=dahua-dws`；
2. `ProviderRegistry.LoadFromDirectory` 扫描 `runtime\providers\*.dll`，反射找到 `IAcquisitionProviderFactory` 并实例化——宿主编译期不认识大华，**换相机只是往这个目录丢一个新 DLL**；
3. `DahuaDwsProvider.Start()`：
   - `LogisticsWrapper.Instance.Initialization(cfgPath)`；
   - `AttachCameraDisconnectCB()` / `AttachAllCameraCodeinfoCB()`（可选）；
   - `Start()`；
   - 注册 `CodeHandle` / `AllCameraCodeInfoEventHandler` / `CameraDisconnectEventHandler`；
   - 打印 `GetWorkCameraInfo()` 的相机清单（这就是"连上了"的证据）。
4. `Program` 等待 Ctrl+C（或 `--duration` 到点），然后 `Stop()`：先摘回调 → `StopApp()` → 把队列里剩下的图写完 → 退出。

### 6.3 回调里只做两件事（`OnCodeHandle`）

```csharp
// 1) 翻译成规范事件（只读字段，不碰磁盘）
ParcelEvent evt = new ParcelEvent { ... };
FillCodes(evt, e);            // CodeList + CodesInfo，过滤 "noread"
FillWeightAndVolume(evt, e);  // OutputResult==0 时没有重量体积

// 2) 深拷贝图像 + 入队（Clone 一份非托管内存，底层返回后原内存会失效）
item.Images.Add(CapturedImage.From(e.OriginalImage));
Enqueue(item);                // 有界队列，满了丢事件并告警，绝不阻塞 SDK
```

为什么必须这样：大华 SDK 的日志里明确写了，**上层回调耗时过长会阻塞下一个包裹的处理**。所以回调线程只做微秒级的工作，写文件交给 `WorkerLoop` 线程。

`ParcelEvent.Stage` 把大华的两次回调归一化：`OutputResult == 0` → `Detected`（只有条码），`OutputResult == 1` → `Enriched`（条码 + 重量 + 体积）。两次的 `TraceId` 相同，业务层靠它合并——这是最小模型里唯一"业务味"的设计，但它属于数据模型，不属于业务逻辑。

### 6.4 图片：只传引用，不传字节

- `CapturedImage` 负责把 SDK 的 `VslbImage`（非托管指针 + 宽高 + 类型）深拷贝出来，用完 `Marshal.FreeHGlobal` 释放；
- `ImageWriter` 落盘：如果相机直接出 JPEG（cfg 里 `outImgType="1"`），原样写文件；否则按灰度/24bpp BGR 自己写 BMP（`ImageWriter` 是纯 BCL 实现，方便调试，不引入任何图像库）；
- 事件里只放 `ImageRef { Kind, DeviceId, Path, Format, Width, Height, Bytes }`。

生产环境建议把 `ImageWriter` 换成 SDK 自带的 `TurboJpegWrapper` 或 SkiaSharp：JPEG 压缩率更高，17 台相机时能省一半以上的磁盘和上行带宽。

### 6.5 错误码：用真实枚举，不用文档里的 -1 ~ -10

官方 C# 文档写的是 `-1 ~ -10`，但实际返回的是 `LogisticsAPIStruct.ERunStatus`（0 / 1000 / 2200 / 3000 / 5000 …）。
`DahuaErrorCodes.Describe()` 直接按真实枚举翻译，现场最常撞到的是：

- `2200` 没加密狗；
- `3000` 相机数与配置不符（多半是没连上、IP 不对、`num` 不匹配）；
- `3001` 相机被 MVViewer / EasyID 占用。

更细的底层原因永远在 `runtime\Log\default.log` 里，例如：

```
[ERROR] [initialize] no camera can be connected.
[INFO ] [vslbRun] ... ret info:[ErrorCode: [3000], Init camera mode failed, Camera num not match ...]
```

## 8. 换成多台相机怎么改

只改 `runtime\Cfg\LogisticsBase.cfg`，代码不用动：

```xml
<!-- 方式一：自动发现所有相机，num 写实际数量 -->
<ImageAcq mode="1" num="6" randWorkMode="1" ... />

<!-- 方式二：指定相机（生产建议） -->
<ImageAcq mode="2" num="3" ... />
<Camera ip="192.168.1.10" enable="1" />
<Camera ip="192.168.1.11" enable="1" />
<Camera ip="192.168.1.12" enable="1" />
```

`randWorkMode="1"` 表示"任意一台相机工作就能启动"，现场抗单点故障；`num` 必须等于 `enable="1"` 的数量（mode=2 时）。

## 9. 加一个新相机品牌（这才是这套结构的目的）

1. 新建类库 `src\DwsEdge.Providers.XXX`，引用 `DwsEdge.Core` 和该厂商的 SDK；
2. 实现 `IAcquisitionProvider`：`Start()` 里连接相机、挂回调；把结果翻译成 `ParcelEvent`（如果厂商不下发包裹级结果，就发 `CameraReadEvent`，由平台侧聚合器归并）；
3. 实现 `IAcquisitionProviderFactory`，`ProviderId` 返回 `"xxx"`；
4. 编译输出到 `runtime\providers\`；
5. 改 `config\gateway.ini` 的 `[runtime] provider=xxx`，并加一段 `[xxx]` 配置。

宿主、`DwsEdge.Core`、将来的业务层/前端/下游对接**一行都不用改**。

```csharp
public sealed class XxxProvider : IAcquisitionProvider
{
    public string ProviderId { get { return "xxx"; } }
    public ProviderCapabilities Capabilities { get { return ProviderCapabilities.None; } }
    public void Start() { /* 连接相机、订阅回调、翻译成 CameraReadEvent/ParcelEvent */ }
    public void Stop() { /* 断开 */ }
    public void Dispose() { Stop(); }
}
```

## 10. 已知限制 / 下一步

- 单相机最小模型：多相机只需改 cfg（见第 8 节），多相机去重/汇总由大华 SDK 内部完成；
- 只有读码 + 存图，没有重量/体积（`WeightMode=0`、`Volume enable=0`），但事件模型里已经留了字段；
- 没有业务/下游/历史库/前端——那是方案 B 的另一半（`DwsPlatform`），它只消费 `spool` 里的规范事件；
- `spool` 目前是本机 JSONL，接口就一个 `IEventSink`，将来换成 gRPC/命名管道即可；
- 命令通道目前是命令行/控制台（`ITriggerControl`）。前端按钮要接的话，把这个接口挂到本地 HTTP/命名管道上即可，provider 不用改；
- 编译用 .NET Framework 自带的 csc（C# 5）。想用现代 C#，可在 VS 里打开 `DwsEdge.sln`（工程文件已按 v4.8/x64 配好，输出目录就是 `runtime\`）；
- 往 ARM 迁移时：`DwsEdge.Core` 和 `DwsEdge.Host` 不含 Windows 专有 API，可以改目标框架到 `net8.0` 跑在 Linux ARM64 上；需要丢弃的只有 `DwsEdge.Providers.Dahua` 这一个插件。

## 11. 常见问题

| 现象 | 原因 / 处理 |
|---|---|
| `找不到 csc.exe` | 系统缺 .NET Framework 4.x |
| 启动返回 `2200` | 没插加密狗 |
| 启动返回 `3000` | 相机没连上：核对 cfg 里的 IP、`num`、`enable`，以及本机网段/防火墙 |
| 启动返回 `3001` | 相机被 MVViewer/EasyID 等占用，关掉再试 |
| 日志一堆 `Cfg file get <xxx> failed` | 大华 SDK 版本比随包 cfg 新，缺的节点会走默认值，不影响最小模型；正式项目按 V3.8 配置文档补齐 |
| 图片是 `.bmp` 而不是 `.jpg` | 相机没出 JPEG。把 cfg 的 `outImgType` 设为 `1`，或把 `ImageWriter` 换成 TurboJpeg |
| 收不到包裹事件 | 触发方式问题：硬触发要有光电信号；软触发要先看启动日志的 `triggerMode` 是不是 2（用 `tools\set-trigger-mode.ps1 -Mode soft`），并且别用 `--trigger-once` 之外的方式 |
| 软触发返回非 0 | 看 `logs\host-*.log`；常见是 `triggerMode` 不是 2、相机没连上、或 SDK 尚未 Start 成功 |
| 没有相机也想跑通全流程 | `config\gateway.ini` 的 provider 改成 `simulator`，然后 `.\run.ps1 -TriggerOnce -Duration 6` |
