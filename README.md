# DWS Edge Min —— 一个解决方案，两个进程

基于大华 DWS SDK 的最小可运行工程，按方案 B 拆成两个进程：

```
相机（大华智能相机）
   │
   ▼
[1] 采集宿主  DwsEdge.Host.exe         net48 / x64
    加载大华 SDK、挂回调、收码收图、图片落盘、事件写 spool
    唯一接触厂商 SDK 和加密狗的进程
   │  spool\events-*.jsonl（V1 用文件，V2 换 gRPC/命名管道）
   ▼
[2] 业务平台  DwsEdge.Platform.exe     net10.0 / ASP.NET Core
    消费事件、合并包裹、提供 API 与实时推送、托管前端页面
   │
   ▼
浏览器（局域网任意 PC）
```

两个进程之间只通过**厂商无关的规范事件**通信（见 `DwsEdge.Core`），因此以后可以换相机品牌、
把平台搬到服务器、甚至整体迁到 Linux/ARM，业务代码都不用重写。

## 一、目录结构

```
dws-edge-min/
├─ DwsEdge.sln                   一个解决方案，5 个工程
├─ build.ps1                     编译全部并拷贝产物到 runtime（依赖 .NET SDK 10）
├─ run.ps1                       运行采集宿主 / 业务平台
├─ tools/set-trigger-mode.ps1    切换大华 cfg 的触发模式（软/硬/狂扫）
├─ tools/make-camera-cfg.ps1     生成相机清单（cfg + 方位映射，支持任意台数）
├─ tools/check-traceids.ps1      扫描 spool，检查追踪号合并与疑似冲突
├─ config/gateway.ini            采集宿主配置（选 provider + provider 参数）
├─ src/
│  ├─ DwsEdge.Core/              契约与模型（net48 + net10.0 双目标，两边共用）
│  │  ├─ Model/                  CodeItem / ImageRef / ParcelEvent / CameraReadEvent / CameraStatusEvent
│  │  └─ Abstractions/           IAcquisitionProvider / IAcquisitionProviderFactory / ITriggerControl
│  │                              / IEventSink / ProviderCapabilities / ProviderSettings
│  ├─ DwsEdge.Providers.Dahua/   大华 SDK 适配插件（net48/x64，唯一引用厂商 DLL）
│  ├─ DwsEdge.Providers.Simulator/ 测试用模拟相机（不需要相机和加密狗）
│  ├─ DwsEdge.Host/              采集宿主 exe（net48/x64）
│  └─ DwsEdge.Platform/          业务平台 exe（net10.0）
│     ├─ Program.cs              最小 API：健康/统计/包裹/相机/图片/SSE
│     ├─ SpoolTailer.cs          消费 spool 事件（V2 换 gRPC 只改这里）
│     ├─ SpoolStore.cs           包裹合并、统计、实时推送、图片按需读取
│     ├─ SpoolModels.cs          事件与输出模型
│     ├─ appsettings.json        端口、spool 目录、图片根目录
│     └─ wwwroot/index.html      实时监控页（暗色，SSE 推送）
└─ runtime/                      运行时目录（大华 SDK 全部 DLL、Cfg、图片、spool、日志）
   ├─ DwsEdge.Host.exe
   ├─ DwsEdge.Core.dll           （net48 版本）
   ├─ providers/                 插件 DLL
   ├─ platform/                  业务平台（含 net10 版 Core、wwwroot）
   ├─ config/gateway.ini
   └─ images/  spool/  logs/  Log/
```

## 二、依赖

- **.NET SDK 10**（`dotnet build`，开发机上已有）；
- **.NET Framework 4.8 目标包**（编译 net48 工程）；
- **.NET 10 运行时**（运行平台；盒子部署时需要安装，或后续做成自包含发布）；
- 大华 **USB 加密狗**（真机采集才需要）；
- 大华 SDK 运行时文件已放在 `runtime\`（59MB，来自 `DWS_Demo_C#_Win64_V3.8.006...zip` 的 `bin\Release\x64`）。

## 三、快速开始

```powershell
cd C:\Users\Administrator\Desktop\lightcookr\dws-edge-min

# 1) 编译全部（会输出到 runtime\ 与 runtime\platform\）
powershell -ExecutionPolicy Bypass -File .\build.ps1
#    无网络环境加 -Offline

# 2) 无相机时用模拟相机跑通链路
#    把 config\gateway.ini 的 provider 改成 simulator，然后：
powershell -ExecutionPolicy Bypass -File .\run.ps1 -TriggerOnce -Duration 8
powershell -ExecutionPolicy Bypass -File .\run.ps1 -Platform     # 浏览器打开 http://本机IP:8090

# 3) 两个一起跑（平台在后台）
powershell -ExecutionPolicy Bypass -File .\run.ps1 -Both -TriggerOnce

# 4) 接真机：provider 改回 dahua-dws，改好相机 IP，然后
powershell -ExecutionPolicy Bypass -File .\tools\set-trigger-mode.ps1 -Mode soft   # 需要软触发时
powershell -ExecutionPolicy Bypass -File .\run.ps1 -TriggerOnce -Duration 8
```

### 接多台相机（相机数量不写死）

> 相机数量由你清单里的行数决定，软件里没有写死：单面 / 双面 / 三面 / 五面 / 六面扫都适用，
> 常见配置有 6 台、12 台、17 台。`num` 会自动等于清单行数（大华 SDK 文档标注上限 20 台，
> 超过只会给提醒，不会拦截启动）。

相机清单由 `runtime\Cfg\LogisticsBase.cfg` 的 `<Camera .../>` 声明决定，不要手改，用工具生成：

```powershell
# 1) 照着 config\cameras-17.example.txt 改成现场实际的 17 个 IP / Key / Id
#    每行一台：ip=172.20.10.11 或 key=序列号 或 id=厂商:序列号
#    可选加方位：ip=172.20.10.11,pos=top（top/bottom/left/right/front/rear）

# 2) 生成配置（自动备份 cfg，mode 自动改成 2，num 自动等于台数）
powershell -ExecutionPolicy Bypass -File .\tools\make-camera-cfg.ps1 -CameraList .\config\cameras-17.example.txt

# 只看结果、不写文件
powershell -ExecutionPolicy Bypass -File .\tools\make-camera-cfg.ps1 -CameraList .\config\cameras-17.example.txt -Preview

# 3) 重启采集宿主，启动时会自动做相机配置自检
powershell -ExecutionPolicy Bypass -File .\run.ps1 -Duration 10
```

**启动前自检**（不通过直接拒绝启动，不用等 SDK 报 3000）：

- `mode=2` 时 `num` 必须等于 `enable="1"` 的相机数量；
- `num` 必须在 1-20 之间；
- 不允许重复声明或 ip/key/id 为空的声明；
- 启动日志会先打印完整清单：`cfg 相机计划：mode=2 num=17 randWorkMode=1 启用相机=17/17`，随后逐条列出每台相机。

**启动快照**：采集宿主启动成功后会为每台相机写一条 `camera-status` 快照事件（`isSnapshot=true`，带型号/序列号/厂商/固件），
业务平台的"相机状态"区立刻就有数据，不用等第一次掉线。

**条码方位兜底**：大华回调里的 `CodesInfo.Position` 常常为空。清单里写了 `pos=` 时，生成工具会额外写出
`runtime\config\camera-positions.ini`（相机 IP / 序列号 / 完整 id → 方位），采集宿主启动时加载它，
回调没给方位就自动补上。所以只要清单里标了方位，条码的方位字段就是可靠的。

### 相机掉线 / 重连统计（A2）

采集宿主按相机累计 **掉线次数 / 恢复次数 / 最近离线时长**，随每次状态事件上报
（字段 `offlineCount`、`reconnectCount`、`lastOfflineDurationMs`）。平台侧取历史最大值，
所以**宿主重启不会让计数回落**。日志长这样：

```
[WARN] 相机 Huaray Technology:BK27440AAK00036 第 1 次掉线
[INFO] 相机 Huaray Technology:BK27440AAK00036 已恢复（第 1 次恢复，本次离线 3.2 秒）
```

相机状态墙列：相机 / 状态 / 型号 / 序列号 / **掉线次数 / 恢复次数 / 最近离线 / 出码数** / 最近变化，
离线的相机会整行标红。平台还会按相机累计"出码包裹数"（`codeCount`）与最近出码时间。

**现场验收步骤（拔网线）**：

1. 拔掉某台相机网线，记录**上报离线耗时**（目标 ≤5 秒；若明显偏慢，需调 SDK 的 `cameraTimeout` 等检测参数）；
2. 看日志与界面：该相机变为"离线"，掉线次数 +1；
3. 插回网线：确认自动恢复、恢复次数 +1、"最近离线"显示本次离线时长、状态变回"在线"；
4. 让包裹过包，确认该相机重新出码（出码数增加）；
5. 连续做 3 次，确认计数累加正确、没有重复计数。

### 存图保留策略（保存天数 + 磁盘水位）

采集宿主内置一个与厂商无关的清理服务，只处理图片根目录下**名字是 8 位日期**的目录，绝不碰其他文件。
配置在 `config\gateway.ini` 的 `[storage]` 段：

```ini
[storage]
imageDir=              # 留空=沿用 provider 段里的 imageDir（默认 images）
retentionDays=7        # 超过 7 天的日期目录整目录删除；0=永久保留
maxDiskPercent=85      # 磁盘占用达到 85% 就从最旧的日期目录开始删；0=不启用
cleanupIntervalMinutes=30   # 清理间隔
cleanupOnStart=true    # 启动时先清理一次
```

启动日志会打印策略与清理结果：

```
[info] 存图保留策略：目录 ...\images；保存 7 天；磁盘水位 85%；每 30 分钟检查（启动时先清理一次）
[info] 存图清理：删除 2 个日期目录、2 个文件，释放 6 KB；当前磁盘占用 93%（保存天数：7 天，水位上限：0%）
```

平台 `/api/stats` 也会给出图片与磁盘占用（后台每 5 分钟探测一次，不拖慢接口）：

```json
{ "imageFileCount": 9, "imageDiskBytes": 1844658,
  "diskTotalBytes": 120026746880, "diskFreeBytes": 7980036096, "diskUsedPercent": 93 }
```

### 追踪号、包裹完整性与查重

- **追踪号规则统一在 Core**（`ParcelTrace`）：`providerId|deviceId|capturedAtMs|code1,code2`。
  同一包裹的多次回调（先条码、后重量体积）得到相同追踪号，平台靠它合并成一条记录；所有 provider 都走这一个函数，不再各写一份。
- **完整性判定**：provider 通过能力位 `StagedParcelResult` 声明"分阶段上报"（大华是）。
  采集宿主把该标记写进事件的 `stagedResult` 字段；平台据此判定：
  - 分阶段 provider：看到 `enriched` 才 `complete = true`，只收到条码的记录标记为 **待补全**（页面上会显示）；
  - 单次上报的 provider：第一个事件即 `complete = true`。
- **兜底与告警**：事件没带追踪号时，平台用 `fallback|相机|时间戳` 兜底，并累计 `missingTraceId`、每 60 秒告警一次。
- **冲突探测**：同一个追踪号下，如果后到事件的条码与已有条码**完全不相交**，判为疑似冲突（很可能是两个包裹被并成一条），累计 `traceIdConflicts` 并打日志。
- **批量查重工具**（压测/验收用）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\check-traceids.ps1
```

输出示例：

```
追踪号查重报告（5 个文件）
  包裹事件总数    : 25
  唯一追踪号（包裹）: 14
  平均每包裹事件数: 1.79（大华分两次上报时正常值≈2）
  缺追踪号事件    : 1
  疑似追踪号冲突  : 1
```

> 注意：如果 Visual Studio 正打开这个解决方案并在后台构建，`obj\bin` 会被 MSBuild/VBCSCompiler 占用，
> `build.ps1` 会报“拒绝访问”。关掉 VS 再编译，或直接在 VS 里生成。

## 四、运行后能看到什么

**采集宿主（控制台）**

```
[14:42:52.805][info] 已加载 provider 插件：simulator（DwsEdge.Providers.Simulator.dll）
[14:42:54.327][parcel] 条码  相机=simulator-cam  条码数=1  [top:TEST144254001]  图=1  累计包裹=1
[14:42:54.330][parcel] 条码+重量体积  相机=simulator-cam  条码数=1  [top:TEST144254001]  重量=501g  体积=9000000mm3
```

**事件文件**：`runtime\spool\events-<日期>.jsonl`（一行一个事件，先落盘再推送，业务端重启不丢）

**业务平台（浏览器 http://本机IP:8090）**：包裹总数、读码率、无码数、相机在线数，以及实时过包列表（SSE 推送）。

**平台 API**（实测返回）：

| 接口 | 说明 |
|---|---|
| `GET /api/health` | 健康检查（含图片根目录） |
| `GET /api/stats` | 事件数、包裹数、无码数、读码率、相机在线数、解析失败数，以及 `pendingParcels`（待补全包裹）、`missingTraceId`（缺追踪号事件）、`traceIdConflicts`（疑似追踪号冲突）、图片数与磁盘占用 |
| `GET /api/parcels?limit=50` | 最新包裹（两次回调已合并成一条）；`codes` 是条码值数组，`codeDetails` 带每个码的类型（1d/2d）与方位 |
| `GET /api/cameras` | 相机在线状态 |
| `GET /api/images?path=<绝对路径>` | 按需读取图片（只允许图片根目录内的文件，越权返回 400） |
| `GET /api/stream` | SSE 实时推送（包裹与统计） |

实测结果示例：`{"events":4,"parcels":2,"noread":0,"images":2,"readRate":1,"parseErrors":0}` —— 两次触发共 4 条事件（detected + enriched），
被平台合并成 2 个包裹，读码率 100%，图片按需可读。

## 五、两个进程的边界

| | 采集宿主（Edge） | 业务平台（Platform） |
|---|---|---|
| 运行时 | .NET Framework 4.8 / x64 | .NET 10 + ASP.NET Core |
| 依赖 | 大华 SDK、原生 DLL、加密狗 | 无厂商依赖 |
| 职责 | 相机、触发、收码、收图、落盘、写 spool | 合并、统计、API、实时推送、前端托管 |
| 崩溃影响 | 采集中断，重启后自愈 | 只影响界面与统计，采集继续 |
| 升级频率 | 低（跟着 SDK 走） | 高（业务、界面天天改） |

通信：V1 用文件 spool（`SpoolTailer` 增量读取，只处理完整行）；V2 换成 gRPC/命名管道时只需替换 `SpoolTailer`，
`SpoolStore` 与平台 API 不动。

## 六、代码导读

**采集侧（net48）**

- `DwsEdge.Host/Program.cs`：入口，读 `config\gateway.ini`、扫 `runtime\providers\*.dll` 反射加载插件、启动/停止、命令行参数（`--duration` / `--trigger-once` / `--trigger-interval`）。
- `DwsEdge.Host/HostEventSink.cs`：控制台 + `logs\host-*.log` + `spool\events-*.jsonl`（手写 JSON，图像只写路径）。
- `DwsEdge.Host/TestConsole.cs`：测试开关（启动触发、定时触发、回车手动触发、补码）。
- `DwsEdge.Providers.Dahua/DahuaDwsProvider.cs`：SDK 生命周期、回调只入队、工作线程落盘、软触发/补码、读 `triggerMode` 给出提示。
- `DwsEdge.Providers.Dahua/CapturedImage.cs`：非托管图像深拷贝与释放；`ImageWriter.cs`：JPEG 直存 / 原始图写 BMP。
- `DwsEdge.Providers.Simulator/SimulatorProvider.cs`：假相机，一次触发生成条码 + BMP + 两条事件。

**平台侧（net10）**

- `DwsEdge.Platform/SpoolTailer.cs`：增量读取 spool，按字节偏移记录位置，只处理以换行结尾的完整行。
- `DwsEdge.Platform/SpoolStore.cs`：按 `traceId` 合并两次回调（`updates` 计数），维护最近 N 条、统计、SSE 订阅者、图片按需读取（带目录白名单校验）。
- `DwsEdge.Platform/wwwroot/index.html`：实时监控页（SSE + 统计卡片 + 图片链接）。

## 七、常见问题

| 现象 | 处理 |
|---|---|
| `build.ps1` 报“拒绝访问” | VS 正在占用 `obj\bin`，关闭 VS 后重试，或直接在 VS 里生成 |
| 平台启动即崩、报事件日志无写权限 | 已在 `Program.cs` 关闭默认日志提供程序（只留控制台）；若自行加日志，注意别依赖 Windows 事件日志 |
| 页面 404 或样式丢失 | `runtime\platform\wwwroot` 缺失；`build.ps1` 会单独拷贝 wwwroot，重新编译即可 |
| 采集宿主返回 3000 | 相机数与配置不符（没连上）：核对 `runtime\Cfg\LogisticsBase.cfg` 的 `num` 与 `<Camera ... enable="1">` |
| 采集宿主返回 2200 | 没插加密狗 |
| 软触发没反应 | `triggerMode` 不是 2；用 `tools\set-trigger-mode.ps1 -Mode soft` 改好并重启采集宿主 |
| 平台没有数据 | 确认 Edge 已产生 `runtime\spool\events-*.jsonl`；平台 `appsettings.json` 的 `Spool:Directory` 默认是 `../spool` |

## 八、下一步（V1 完整版）

平台骨架已通，接着按 V1 需求清单补齐：

1. SQLite 持久化（替换内存态，历史可查）；
2. 条码过滤规则（长度、前后缀、正则）；
3. 下游对接（TCP 客户端/服务端、HTTP，带重传与幂等）；
4. 配置页（相机清单、存图策略、输出参数）；
5. 把 `SpoolTailer` 换成 gRPC/命名管道，降低延迟。
