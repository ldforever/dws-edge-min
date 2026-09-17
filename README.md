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
├─ tools/apply-config.ps1        一键应用配置：写配置 → 重启 SDK 校验 → 失败自动回滚
├─ config/gateway.ini            采集宿主配置（选 provider + provider 参数）
├─ frontend/                     前端 TypeScript 工程（无框架、无打包器）
│  ├─ src/                       api / sse / dom / realtime / devices / config / types
│  └─ build.mjs                  tsc 编译 → wwwroot\js，并拷贝样式
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

* **后端**：.NET SDK 10（net48 目标包 + ASP.NET Core），离线可用 `build.ps1 -Offline`；
* **前端**：Node ≥ 18 + TypeScript（**可选**）—— 只在改界面时需要，现场部署用仓库里已编译好的 `wwwroot`。

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

### 一键应用配置：写配置 → 重启校验 → 失败自动回滚（A8-1 / A8-2）

现场改配置最怕两件事：**改完没生效**、**改错了软件起不来又没人会改回去**。
`tools\apply-config.ps1` 把「写配置 + 重启 SDK 校验 + 失败回滚」做成了一条命令：

```powershell
# 只改触发模式（soft=软触发 / hard=光电硬触发 / free=自由拉流）
powershell -ExecutionPolicy Bypass -File .\tools\apply-config.ps1 -TriggerMode hard

# 只改相机清单（清单文件写法见上一节）
powershell -ExecutionPolicy Bypass -File .\tools\apply-config.ps1 -CameraList .\config\cameras-17.example.txt

# 两个一起改（现场最常用）
powershell -ExecutionPolicy Bypass -File .\tools\apply-config.ps1 -TriggerMode hard -CameraList .\config\cameras-17.example.txt

# 采集宿主正在跑：让它自动停掉再校验（-RestartHost 还会在通过后重新拉起来）
powershell -ExecutionPolicy Bypass -File .\tools\apply-config.ps1 -TriggerMode soft -StopHost -RestartHost

# 只写配置、不校验（离线改文件时用）
... -SkipVerify
```

**它做了什么**

1. 把当前 cfg 复制成回滚点 `LogisticsBase.cfg.rollback-<时间戳>`（改坏了一键回到这里）；
2. 调 `set-trigger-mode.ps1` / `make-camera-cfg.ps1` 实际改写 cfg（这两个各自也会写 `.bak-<时间戳>`）；
3. 打印**改前 → 改后**摘要（`mode` / `num` / `enable 相机` / `triggerMode`），一眼看出参数有没有真的变；
4. 启动 `DwsEdge.Host.exe --verify-config`：宿主按新配置**真的启动一次 SDK**，再把配置回读一遍，
   大华 provider 还会用 `GetWorkCameraCount()` 核对「SDK 实际工作的相机数 ≥ cfg 里的 num」；
5. 校验不过 → 自动把回滚点写回，**再校验一次**确认回滚后能不能正常起来。

**退出码**（供脚本/界面调用）

| 退出码 | 含义 | 配置状态 |
|---|---|---|
| `0` | 校验通过，配置已生效 | 新配置已写入 |
| `2` | 新配置校验失败，**已自动回滚**到应用前，回滚后校验通过 | 等于没改过 |
| `3` | 新配置校验失败，回滚后**仍然起不来**（设备/狗/原生 DLL 的问题，不是配置的问题） | 已回滚，需人工检查 |
| `1` | 应用这一步就失败（清单格式错、cfg 结构不认识、宿主在跑没加 `-StopHost`） | 已回滚 |

**实测记录**（本机 `provider=simulator`，2026-09-17）：

| 场景 | 结果 |
|---|---|
| `-TriggerMode soft`（1→2） | `[verify] PASS`，退出码 0，摘要 `triggerMode : 1 → 2` |
| 再次 `-TriggerMode soft` | 提示"当前已经是 soft，无需修改"，仍校验 PASS（幂等，不会写坏文件） |
| 相机清单 1 台改 6 台 + 软触发 | `num : 1 → 6`、`enable 相机 : 1 → 6`、方位映射 6 条，`[verify] PASS`，退出码 0 |
| 注入"相机拒绝软触发"后 `-TriggerMode soft` | 校验 `[verify] FAIL` → **自动回滚** → 回滚后 `[verify] PASS`，退出码 2，cfg 回到 `triggerMode=1` |
| 清单故意写错（`switch=...`） | 应用失败即回滚，退出码 1，cfg 内容未变 |
| 宿主在跑、没加 `-StopHost` | 直接拒绝并给出 PID，退出码 1 |
| 宿主在跑、加 `-StopHost -RestartHost` | 自动停掉 → 校验 PASS → 自动重启宿主，退出码 0 |
| 真机 provider 起不来（本机无相机/原生库） | `[verify] FAIL` → 回滚 → 回滚后仍失败 → 退出码 3，cfg 恢复到改前值 |

校验用的注入开关在 `config\gateway.ini` 的 `[simulator]` 段：`rejectTriggerMode=soft|hard|free`（留空=不注入），
只是用来验证"失败自动回滚"这条链路，真机不需要它。

> 现场注意：校验会**真的启动一次 SDK**，所以校验期间不能有第二个宿主进程在跑（脚本会检测并拦下来），
> 大华 SDK 也需要加密狗在位——没有狗时校验会失败，回滚仍是安全的，但退出码会是 3。

### 界面上一键应用（平台"配置"页）

同一套逻辑也能在平台界面上点：打开 `http://本机IP:8090` → 顶部 **配置** 页签。

页面上有三块：

1. **当前 SDK 配置**：provider、`mode/num/randWorkMode`、启用相机数、触发模式、方位映射条数、
   一键应用脚本路径（找不到脚本会红字提示"先跑一次 build.ps1"），以及配置自检的报错/提醒；
2. **一键应用配置**：触发模式下拉（硬触发 / 软触发 / 自由拉流 / 不改）、相机清单文本框
   （每行 `ip=...` / `key=...` / `id=...`，可带 `,pos=top`）、三个选项
   （校验前停止采集宿主 / 通过后自动重启采集宿主 / 只写配置不校验），点 **一键应用**；
3. **执行结果**：结论徽标（退出码 + 文案）+ 完整脚本输出（原样回显 `[verify] PASS/FAIL`、变更摘要、回滚过程）。

界面和命令行**走的是同一个脚本**，所以"现场怎么修、界面就怎么改"，不会出现两套行为。

> 注意：平台要能调起 PowerShell 并重启采集宿主。如果采集宿主是用 Windows 服务/计划任务托管的，
> 请把"通过后自动重启采集宿主"的勾去掉，由服务管理器负责重启（V1 还没做服务托管）。

### 设备信息与相机清单（A9）

**设备信息** 页签一屏看清现场实况：

| 列 | 来源 | 说明 |
|---|---|---|
| 方位 | `config\camera-positions.ini` | 下拉可改，点"保存方位"写文件（自动备份），顶面/底面/左侧/右侧/前侧/后侧 |
| 状态 | 采集宿主上报 | 在线 / 离线 / **未发现**（cfg 里声明了 `enable="1"` 但 SDK 没报——没上电、没接网或被占用） |
| 清单标识（IP / Key） | `Cfg\LogisticsBase.cfg` 的 `<Camera>` 声明 | 就是你在清单里写的 `ip=172.20.10.11`，和实际接线一一对应 |
| 相机标识（SDK） | SDK 回调 | 大华上报的 key / 完整 id |
| 型号 / 序列号 / 厂商 / 固件 | SDK `CameraInfo` | **和真实设备一致**，不是手填的 |
| 掉线 / 恢复 / 出码数 / 最近变化 | 采集宿主累计 | 平台侧取历史最大值，宿主重启不会让计数回落 |

页面顶部还有：相机总数、在线、离线、**清单里声明但未发现**、**未标方位** 五个计数，以及
六面（顶/底/左/右/前/后）的台数与在线情况——方位标错了一眼就能看出来。右上角"导出 CSV"可直接出对账表。

两个实现细节：

* **清单是完整的**：cfg 里声明了 17 台，界面就有 17 行；某台没连上会显示"未发现"，而不是整行消失；
* **换清单不会串**：采集宿主每次启动带一个"会话号"，平台收到新会话的快照后会清掉上一轮的相机，
  所以改了相机清单重启后，设备列表里只有新清单的相机。

大华 SDK 不回报相机 IP（`CameraInfo` 只有 ID / 型号 / 序列号 / 厂商 / 固件 / ExtraInfo），
所以"清单里的 IP ↔ 实际相机"是靠这些标识去匹配的：先精确匹配，再退化成包含匹配，
**看起来像 IPv4 的声明值只做精确匹配**（避免 `100.100.100.1` 错配到 `100.100.100.11`）。
匹配不上时不会瞎猜，而是显示成"清单里声明了但没发现"，宁可提示也不要错的对应关系。

> 改完方位要**重启采集宿主**才影响条码上的方位字段（采集宿主启动时才加载映射表）；
> 界面会立刻按文件里的新方位显示，并标一个"待重启生效"。平台重启时会从 spool 回放相机状态，
> 所以只重启平台也能看到完整的相机清单。

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

### spool 事件文件保留策略（A：带消费保护）

`config\gateway.ini` 的 `[storage]` 段新增：

```ini
spoolRetentionDays=7      # spool 事件文件保存天数；0=永久保留
```

删除条件**必须同时满足**：

1. 文件日期早于"今天 − spoolRetentionDays"；
2. 业务平台已经写过消费标记 `spool\.consumed`，且该文件日期 **早于或等于**标记日期。

也就是说：**平台没消费过的数据绝不会被删**。平台没运行时（没有标记），清理会打印
`业务平台还没有写过消费标记…本次不删除任何事件文件，避免丢数据` 然后跳过。

实测日志：

```
[info] spool 保留策略：目录 ...\runtime\spool；保存 7 天；每 30 分钟检查（启动时先清理一次）（仅在业务平台消费后删除）
[info] spool 清理：删除 3 个事件文件，释放 2 KB（保存天数：7 天，已消费到：2026-09-17）
```

### 历史持久化与消费位点（B：重启不再全量重放）

平台把数据落到 `runtime\platform\..\data\`（默认 `runtime\data\`）：

```
data\parcels-yyyyMMdd.jsonl   包裹快照：每次事件更新写一行完整记录（逐行落盘）
data\offsets.json             spool 消费位点：进程重启后只读新增事件
spool\.consumed               平台写出的"已消费到哪一天"标记（供采集宿主清理判断）
```

启动行为：

1. 先从历史文件恢复最近 `History:LoadDays`（默认 2 天）的包裹 → 界面立刻有数据；
2. 再从 `offsets.json` 记录的位置继续读 spool 新增事件，**不再全量重放**。

实测（同一个 spool，两次启动）：

```
第一次（无位点）： events=33  parcels=18
第二次（有位点）： events=0   parcels=18   ← 不重放，数据来自历史
```

历史查询接口：

| 接口 | 说明 |
|---|---|
| `GET /api/history?from=2026-09-16&to=2026-09-17&code=YT&deviceId=cam6-top&noread=false&limit=200` | 按时间范围 + 条码/相机/无码过滤查询历史包裹（返回字段与 `/api/parcels` 一致，含 `codeDetails`、`complete`、长宽高） |

> 说明：当前历史库用按天 JSONL 文件实现，方法集中在 `HistoryStore`（Append / LoadRecent / Query / LoadOffsets / SaveOffsets）。
> 之所以没用 SQLite：当前开发环境无法离线获取 `Microsoft.Data.Sqlite` 包。等能装包时，只需用同样的方法签名替换这一个类，
> 平台其余代码不用改。

> 注意：如果 Visual Studio 正打开这个解决方案并在后台构建，`obj\bin` 会被 MSBuild/VBCSCompiler 占用，
> `build.ps1` 会报“拒绝访问”。关掉 VS 再编译，或直接在 VS 里生成。

## 四、前端工程（TypeScript，无框架）

界面不再是「一个 index.html 里塞 500 行 JS」，而是一个独立的小工程：

```
frontend/
├─ package.json          devDependencies 只有 typescript，没有打包器
├─ tsconfig.json         类型检查用（strict）
├─ tsconfig.build.json   编译输出用（→ src\DwsEdge.Platform\wwwroot\js）
├─ build.mjs             构建脚本：tsc + 拷贝样式（--watch 可监听）
└─ src/
   ├─ types.ts           /api/* 的 DTO 类型，和后端 DTO 一一对应
   ├─ api.ts             所有接口调用（返回 { status, data }，不抛 HTTP 异常）
   ├─ sse.ts             EventSource 封装 + 断线重连
   ├─ dom.ts             $ / cell / 方位中文名 / CSV / 下载 / 提示
   ├─ realtime.ts        实时监控页（KPI + 过包表 + 相机表）
   ├─ devices.ts         设备信息页（方位编辑、保存、六面概览、导出 CSV）
   ├─ config.ts          配置页（一键应用 + 结果回显）
   ├─ main.ts            入口：页签切换 + 装配
   └─ styles.css         全部样式
```

编译产物（**提交进仓库**，现场机器不需要 node）：

```
src\DwsEdge.Platform\wwwroot\
├─ index.html   只剩骨架 + 文案
├─ app.css      ← src/styles.css
└─ js\*.js      ← src/*.ts 编译出的原生 ES Module（浏览器直接加载）
```

**现场升级不会被浏览器缓存坑**：构建时会按 js/css 的内容算一个 8 位指纹，写进引用里 ——

```html
<link rel="stylesheet" href="./app.css?v=7fa00a80" />
<script type="module" src="./js/main.js?v=7fa00a80"></script>
```

模块之间的 `import` 也会带上同一个版本号（`import { api } from "./api.js?v=7fa00a80"`）——
原生 ES Module 的 import 是独立 URL，只给入口加版本号的话，`api.js` / `dom.js` 这些子模块仍可能被缓存住。

两个特性很重要：

* **内容没变，版本号就不变** —— 重复构建不会产生无意义的 git 改动，也不会白刷客户浏览器缓存；
* **内容一变，所有 URL 都变** —— 现场换一份前端，客户普通刷新（F5）就能拿到新版，不需要教他们按 Ctrl+F5。

配套地，平台只给 `.html` 加 `Cache-Control: no-cache`（每次都回源校验）：
否则浏览器可能继续用旧的 `index.html`，里面那个"新版本号"根本到不了客户端。
js/css 不加缓存头，失效完全交给版本号。

**为什么不用打包器**（esbuild / webpack）：这个前端只有五六个模块、零第三方运行时依赖，
浏览器原生 ES Module 就够了。好处是离线现场零工具链、DevTools 里看到的就是真实源文件名、
改一行样式不用等打包。以后真要做 SPA 再上 Vue/React + Vite 也不冲突——后端接口一个字都不用改。

开发循环：

```powershell
cd frontend
npm install      # 只装 typescript（可选；不装也会用全局 tsc）
npm run check    # 类型检查（strict，不产出文件）
npm run watch    # 改 .ts / .css 自动重新编译
npm run build    # 一次性构建
```

`build.ps1` 会自动跑一次前端构建：**检测到 node 才跑**，没有 node 就直接用仓库里已提交的产物
（现场部署因此不依赖 node 工具链）。

**类型是干什么用的**：`types.ts` 里的 interface 和后端 `SpoolModels.cs` / `ConfigStore.cs` 的 DTO 一一对应。
A9 那一次一口气加了 `declaredKind / declaredValue / position / discovered / sessionId / positionPending`
这些字段，字段名写错在 `npm run check` 阶段就会报错，不用等界面上出现一片空白单元格。

## 五、运行后能看到什么

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
| `GET /api/dispatch/pending?limit=` | **B1** 待下发的包裹（一个 traceId 只会出现一次；下游模块从这里取） |
| `POST /api/dispatch/ack` | **B1** 下游回报下发结果 `{traceId, success, error}`；幂等，重复 ack 不会重复计数 |
| `GET /api/devices` | 设备信息页数据：相机清单（方位/清单标识/型号/序列号/在线/未发现）+ 汇总 + 六面聚合（A9） |
| `GET /api/camera-positions` | 相机方位映射的当前内容（A9） |
| `POST /api/camera-positions` | 保存方位映射（写 `config\camera-positions.ini`，自动备份） |
| `GET /api/config` | 当前 SDK 配置摘要（provider、mode/num、触发模式、相机清单、方位条数、脚本是否就绪、最近一次应用结果） |
| `POST /api/config/apply` | 一键应用配置：调 `tools\apply-config.ps1` 做 写配置 → 重启校验 → 失败回滚，返回退出码与完整输出（A8） |
| `GET /api/history?from=&to=&code=&deviceId=&noread=&limit=` | 历史查询（读历史文件，支持时间范围、条码、相机、无码过滤） |
| `GET /api/images?path=<绝对路径>` | 按需读取图片（只允许图片根目录内的文件，越权返回 400） |
| `GET /api/stream` | SSE 实时推送（包裹与统计） |

实测结果示例：`{"events":4,"parcels":2,"noread":0,"images":2,"readRate":1,"parseErrors":0}` —— 两次触发共 4 条事件（detected + enriched），
被平台合并成 2 个包裹，读码率 100%，图片按需可读。

## 六、包裹合并与去重（B1）

这一节说明**包裹是怎么被合并成一条、重复上报是怎么被丢掉的**。

### 合并规则

一个包裹在采集侧会回调两次（先只有条码，再补重量体积）：

| 事件 | 平台的处理 |
|---|---|
| 第一次回调（`detected`，只有条码） | 新建一条记录，`dispatchState=pending`（**只在这里置一次**） |
| 第二次回调（`enriched`，同 traceId） | 合并进同一条：补重量/体积、`updates` 1→2、标记 `complete=true` |
| 同一包裹又来一条**同样内容**的事件 | 判为重复：**不计数、不写历史、不推送、不重新入队下发** |

判定重复用的是"内容指纹"：`阶段 + 条码集合 + 重量 + 体积尺寸 + 图片`。
刻意不含时间戳、事件号这类"每次送达都会变"的字段 —— 所以第二次回调不会被误判成重复，
而"同一条回调又送一遍"会被准确识别。

指纹是**落盘**的（`data/applied-yyyyMMdd.jsonl`）。这一点很重要：平台异常退出后 spool 被整段重读时
（位点回退、offsets.json 丢失），一样能把重复事件丢掉 —— 这是"重复上报不重复计数"最容易翻车的场景。

### 幂等下发

`dispatchState` 只在创建记录时置成 `pending`，之后无论合并多少次回调都不会再改变它的"入队资格"，
所以同一个 traceId 在 `/api/dispatch/pending` 里只会出现一次。下游（B4/B5/B6）处理完回报
`POST /api/dispatch/ack`，重复 ack 会返回 `alreadySent`，不重复计数；失败会记 `failed` 等重试。

### 界面与自测

实时页底栏显示 `重复事件 / 合并包裹 / 待下发`；过包表只在"已下发 / 下发失败"时加标签
（全员都"待下发"就不显示，避免噪音）。

离线回归测试（不需要相机和加密狗，自己造事件、自己起平台、跑完自动停）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-b1-dedup.ps1
```

脚本造 9 条事件（3 个包裹，含 3 条重复、1 组 eventId 重号），分三轮共 27 项断言：
正常消费 → 下发幂等 → 模拟异常退出后重读整个 spool。

## 七、两个进程的边界

| | 采集宿主（Edge） | 业务平台（Platform） |
|---|---|---|
| 运行时 | .NET Framework 4.8 / x64 | .NET 10 + ASP.NET Core |
| 依赖 | 大华 SDK、原生 DLL、加密狗 | 无厂商依赖 |
| 职责 | 相机、触发、收码、收图、落盘、写 spool | 合并、统计、API、实时推送、前端托管 |
| 崩溃影响 | 采集中断，重启后自愈 | 只影响界面与统计，采集继续 |
| 升级频率 | 低（跟着 SDK 走） | 高（业务、界面天天改） |

通信：V1 用文件 spool（`SpoolTailer` 增量读取，只处理完整行）；V2 换成 gRPC/命名管道时只需替换 `SpoolTailer`，
`SpoolStore` 与平台 API 不动。

## 八、代码导读

**采集侧（net48）**

- `DwsEdge.Host/Program.cs`：入口，读 `config\gateway.ini`、扫 `runtime\providers\*.dll` 反射加载插件、启动/停止、命令行参数（`--duration` / `--trigger-once` / `--trigger-interval`）。
- `DwsEdge.Host/HostEventSink.cs`：控制台 + `logs\host-*.log` + `spool\events-*.jsonl`（手写 JSON，图像只写路径）。
- `DwsEdge.Host/TestConsole.cs`：测试开关（启动触发、定时触发、回车手动触发、补码）。
- `DwsEdge.Providers.Dahua/DahuaDwsProvider.cs`：SDK 生命周期、回调只入队、工作线程落盘、软触发/补码、读 `triggerMode` 给出提示。
- `DwsEdge.Providers.Dahua/CapturedImage.cs`：非托管图像深拷贝与释放；`ImageWriter.cs`：JPEG 直存 / 原始图写 BMP。
- `DwsEdge.Providers.Simulator/SimulatorProvider.cs`：假相机，一次触发生成条码 + BMP + 两条事件。
- `DwsEdge.Core/Config/CameraPlan.cs`：读 cfg（mode/num/triggerMode + 相机声明），启动自检与配置校验共用。
- `DwsEdge.Core/Config/CameraPositions.cs`：相机方位表的读写（界面与采集宿主共用一份实现）。
- `DwsEdge.Core/Config/CameraIdentity.cs`：把 cfg 里的 `ip=/key=/id=` 和 SDK 上报的相机标识对上号。

**平台侧（net10）**

- `DwsEdge.Platform/SpoolTailer.cs`：增量读取 spool，按字节偏移记录位置，只处理以换行结尾的完整行。
- `DwsEdge.Platform/SpoolStore.cs`：按 `traceId` 合并两次回调（`updates` 计数），维护最近 N 条、统计、SSE 订阅者、图片按需读取（带目录白名单校验）。
- `DwsEdge.Platform/ConfigStore.cs`：配置页与一键应用（调 `tools\apply-config.ps1`）、相机方位映射的读写。
- `DwsEdge.Platform/wwwroot/index.html`：只剩骨架与文案；样式和逻辑分别来自 `frontend/src/styles.css` 与 `frontend/src/*.ts` 的编译产物。

**前端（TypeScript，见第四节）**

- `frontend/src/main.ts`：入口，装配页签与实时推送。
- `frontend/src/api.ts` + `types.ts`：接口层与 DTO 类型（与后端一一对应）。
- `frontend/src/realtime.ts` / `devices.ts` / `config.ts`：三个页签各自的渲染逻辑。
- `frontend/src/sse.ts` / `dom.ts`：实时推送封装与 DOM 小工具。

## 九、常见问题

| 现象 | 处理 |
|---|---|
| `build.ps1` 报“拒绝访问” | VS 正在占用 `obj\bin`，关闭 VS 后重试，或直接在 VS 里生成 |
| 平台启动即崩、报事件日志无写权限 | 已在 `Program.cs` 关闭默认日志提供程序（只留控制台）；若自行加日志，注意别依赖 Windows 事件日志 |
| 页面 404 或样式丢失 | `runtime\platform\wwwroot` 缺失；`build.ps1` 会单独拷贝 wwwroot，重新编译即可 |
| 页面白屏、控制台报 `app.js/js 404` | 前端没编译：`cd frontend && npm run build`（或直接跑 `build.ps1`），产物要落在 `wwwroot\js` 与 `wwwroot\app.css` |
| `npm run check` 报某个字段不存在 | 后端 DTO 改了、`frontend/src/types.ts` 没跟着改 —— 这正是上 TypeScript 想要的提示 |
| 升级前端后客户还看到旧界面 | 正常构建会在 `index.html` 引用上写 `?v=<内容指纹>`，普通刷新即可生效；如果手工拷文件忘了跑构建，指纹不会变，浏览器就会继续用旧的 |
| 采集宿主返回 3000 | 相机数与配置不符（没连上）：核对 `runtime\Cfg\LogisticsBase.cfg` 的 `num` 与 `<Camera ... enable="1">` |
| 采集宿主返回 2200 | 没插加密狗 |
| 软触发没反应 | `triggerMode` 不是 2；用 `tools\set-trigger-mode.ps1 -Mode soft` 改好并重启采集宿主 |
| 平台没有数据 | 确认 Edge 已产生 `runtime\spool\events-*.jsonl`；平台 `appsettings.json` 的 `Spool:Directory` 默认是 `../spool` |
| `apply-config.ps1` 报"检测到采集宿主正在运行" | 校验要独占 SDK（再起一个实例会和正在跑的抢相机）；加 `-StopHost` 让脚本先停掉，或自己先停 |
| `apply-config.ps1` 退出码 3 | 回滚后仍起不来 → 大概率是设备侧问题（加密狗/相机网段/原生 DLL），不是配置问题 |
| 编译后 `runtime\config\gateway.ini` 没变 | 正常：现场配置不会被编译覆盖（要强制覆盖加 `-ForceConfig`），避免把现场 provider 冲掉 |
| 设备信息页一直是空的 | 采集宿主没上报过相机快照：确认宿主已启动（模拟器 provider 也会上报 cfg 里的清单）；平台启动时会从 spool 回放最近两个事件文件 |
| 界面改了方位，条码方位还是旧的 | 采集宿主只在启动时读方位表；重启宿主（配置页勾"通过后自动重启采集宿主"也可以） |
| 配置页红字"找不到 apply-config.ps1" | 跑一次 `build.ps1`（会把 `tools\*.ps1` 拷到 `runtime\tools`），或手工把 tools 目录放到 runtime 旁边 |
| 相机显示"未发现" | cfg 里 `enable="1"` 但 SDK 没报；查上电、网线、网段，或该相机被别的软件占用 |

## 十、下一步（V1 完整版）

采集侧 A1-A9 已落地（A5 里的"面单抠图"按你的要求不做），平台侧包裹合并 / 历史库 / 存图访问 /
统计与实时推送 / 设备信息 / 一键应用配置也都打通了。接着按 V1 需求清单排：

1. **A8-3 配置模板**：把当前 cfg 存成模板、按模板生成/对比（界面上"另存为模板/套用模板"）；
2. 条码过滤规则（长度、前后缀、正则与黑白名单）；
3. 下游对接（TCP 客户端 / 服务端、HTTP，带重传与幂等）；
4. 配置页补齐：存图策略、输出参数（相机清单与触发模式已能改）；
5. 把 `SpoolTailer` 换成 gRPC / 命名管道，降低延迟（为 ARM 全栈铺路）；
6. 采集宿主做成 Windows 服务 / 看门狗，配置页的"重启采集宿主"改成调服务管理器（现在是从平台直接拉进程）。
