# DWS Edge Min —— 一个解决方案，两个进程

> **现场交付/调试请用单独的操作手册：[docs/操作手册.md](docs/操作手册.md)**
> （准备 → 部署 → 配置 → 日常操作 → 现场动作 → 故障速查 → 交付验收清单）。
> 本文偏架构与实现细节，适合开发与二次开发。

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
├─ tools/host-command.ps1        A4 命令通道：-Status / -SoftTrigger / -Recode（退出码翻译成人话）
├─ tools/test-a4-command.ps1     A4 回归（软触发 / 补码 / 模式校验 / 退出码）
├─ tools/self-check.ps1          现场一键自检（环境 / 触发 / 平台 / 磁盘 / 相机 / 下游 + 该做什么）
├─ tools/test-a8-template.ps1    A8-3 配置模板回归（另存 / 差异 / 套用）
├─ tools/make-camera-cfg.ps1     生成相机清单（cfg + 方位映射，支持任意台数）
├─ tools/check-traceids.ps1      扫描 spool，检查追踪号合并与疑似冲突
├─ tools/apply-config.ps1        一键应用配置：写配置 → 重启 SDK 校验 → 失败自动回滚
├─ tools/test-c1-cards.ps1       实时过包卡片墙自检（造数据 + 起平台 + 校验，可用浏览器看效果）
├─ tools/test-c2-wall.ps1        相机状态墙回归（含 SSE 实时性校验，-KeepRunning 可留平台看）
├─ tools/test-c5-config.ps1      配置页回归（存图策略 / 相机清单 / 备份回滚）
├─ tools/test-c4-stats.ps1       统计看板回归（总览 / 按相机 / 按班次 / 与库对账）
├─ tools/test-c6-diag.ps1        诊断日志回归（来源 / 范围 / 打包 zip 真解压）
├─ config/gateway.ini            采集宿主配置（选 provider + provider 参数）
├─ frontend/                     前端 TypeScript 工程（无框架、无打包器）
│  ├─ src/                       api / sse / dom / auth / realtime（C1+C2 两面墙）/ stats（C4 看板）/ diag（C6 诊断）/ devices / monitor / config / history / rules / dedup / downstream / types
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
│     ├─ Program.cs              最小 API：健康/统计/包裹/相机/图片/历史/规则/下游/监控/SSE
│     ├─ SpoolTailer.cs          消费 spool 事件（V2 换 gRPC 只改这里）
│     ├─ SpoolStore.cs           包裹合并、统计、实时推送、图片按需读取
│     ├─ HistoryStore.cs         历史库（索引 + 查询 + CSV 导出）
│     ├─ DedupStore.cs           去重指纹归档（按 traceId）
│     ├─ BarcodeRuleStore.cs     条码过滤规则（热加载）
│     ├─ DownstreamSender.cs     下游输出（TCP 客户端/服务端、HTTP）
│     ├─ ThumbnailService.cs     缩略图（纯 C# 处理 BMP）
│     ├─ CameraMonitor.cs        相机状态监控与告警（B8）
│     ├─ AuthStore.cs            账号、会话、服务令牌与接口鉴权（B9）
│     ├─ SpoolModels.cs          事件与输出模型
│     ├─ appsettings.json        端口、spool 目录、图片根目录
│     └─ wwwroot/                index.html 骨架 + 编译产物（app.css / js\*.js）
└─ runtime/                      运行时目录（大华 SDK 全部 DLL、Cfg、图片、spool、日志）
   ├─ DwsEdge.Host.exe
   ├─ DwsEdge.Core.dll           （net48 版本）
   ├─ providers/                 插件 DLL
   ├─ platform/                  业务平台（含 net10 版 Core、wwwroot）
   ├─ config/                    gateway.ini（采集）+ barcode-rules.json / downstream.json / monitor.json / auth.json / users.json（平台，保存自动备份）
   └─ images/  spool/  logs/  Log/
```

### 版本号口径（只有一个来源）

根目录 `VERSION` 文件是唯一来源（如 `V1.0.3`），改版本只改它：

| 位置 | 谁写的 | 形态 |
|---|---|---|
| `VERSION` | 手工维护 | `V1.0.3` |
| exe / dll 程序集版本 | `build.ps1` 编译时注入 `-p:Version` | `1.0.3`（还带 `+<git短号>`） |
| 交付包目录名 / 包内 `VERSION.txt` | `tools/make-package.ps1` | `DWS-Edge-Min_V1.0.3_...` / `V1.0.3` |
| 包内 `VERSION.txt` 的"程序集版本 / 源码提交" | 打包时从包内产物与 git 现读 | `1.0.3` / `caf7a52` |

规则：**程序集版本 = 版本号去掉 `V`**。核验用 `tools/check-package.ps1`（交付包里在 `runtime\tools\`），
它把"包名 ↔ VERSION.txt ↔ exe 属性 ↔ checksums ↔ 仓库 runtime"一次对完，退出码 0 才发。
细节见 `docs/操作手册.md` 附录 E。

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
   ├─ realtime.ts        实时监控页（KPI + **过包卡片墙** + **相机状态墙**，都可切表格）
   ├─ devices.ts         设备信息页（方位编辑、保存、六面概览、导出 CSV）
   ├─ monitor.ts         监控与告警（告警条、在线率/心跳、掉线与告警记录、阈值配置）（B8）
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

**业务平台（浏览器 http://本机IP:8090）**：包裹总数、读码率、无码数、相机在线数、**相机告警条**（B8），
以及**实时过包卡片墙**（C1：缩略图 + 条码 + 时间 + 相机 + 状态，点图看原图，SSE 实时刷新；也可切回表格）；
下面是**相机状态墙**（C2：每台相机一格，在线/掉线次数/出码计数/心跳/在线率/告警，异常一眼看出）；
设备信息页有每台相机的在线率/心跳/掉线记录；**统计页**有总包数/读码率/无码率与按相机、班次、小时的维度看板（C4）。
**诊断页**把采集宿主日志、平台日志、大华 SDK 日志、事件缓冲、审计日志集中起来，能按时间范围查看与**一键打包 zip**（C6）。

**平台 API**（实测返回）：

| 接口 | 说明 |
|---|---|
| `GET /api/health` | 健康检查（含图片根目录） |
| `GET /api/stats` | 事件数、包裹数、无码数、读码率、相机在线数、解析失败数，以及 `pendingParcels`（待补全包裹）、`missingTraceId`（缺追踪号事件）、`traceIdConflicts`（疑似追踪号冲突）、图片数与磁盘占用 |
| `GET /api/parcels?limit=50` | 最新包裹（两次回调已合并成一条）；`codes` 是条码值数组，`codeDetails` 带每个码的类型（1d/2d）与方位 |
| `GET /api/cameras` | 相机在线状态 |
| `GET /api/dispatch/pending?limit=` | **B1** 待下发的包裹（一个 traceId 只会出现一次；下游模块从这里取） |
| `POST /api/dispatch/ack` | **B1** 下游回报下发结果 `{traceId, success, error}`；幂等，重复 ack 不会重复计数 |
| `GET /api/dedup` | **B1** 去重指纹归档状态（保护了多少包裹、索引/WAL 大小、保留期、上次整理） |
| `POST /api/dedup/compact` | **B1** 立即整理归档（合并 WAL、清理过期），返回整理后的状态 |
| `GET /api/history?from=&to=&code=&deviceId=&noread=&dispatchState=&hasImage=&limit=&offset=` | **B3** 历史查询（走按 traceId 收敛的索引，返回 `total`/`elapsedMs`/`fromIndex`） |
| `GET /api/history/export?（同上参数）` | **B3** 导出 CSV（UTF-8 BOM，字段含条码/时间/相机/图片路径/无码/下发状态） |
| `GET /api/downstream` | **B4** 下游输出配置 + 运行状态（连接状态/已发/失败/待发/最近错误/模板问题） |
| `POST /api/downstream` | **B4** 保存下游配置（自动备份、立即生效） |
| `POST /api/downstream/test` | **B4** 测试到下游的 TCP 连接 |
| `POST /api/downstream/preview` | **B4** 用最近一条包裹渲染模板，返回实际报文 |
| `GET /api/downstream/log?limit=` | **B4** 最近下发记录（成功/失败/字节数/错误） |
| `POST /api/downstream`（protocol=tcp-server） | **B5** 平台作为 TCP 服务端监听并广播；状态里带 `listening`/`listenTarget`/`clientCount`/`clients` |
| `POST /api/downstream`（protocol=http） | **B6** HTTP 推送：带 `Idempotency-Key`（值=traceId），非 2xx 自动重试 |
| `GET /api/devices` | 设备信息页数据：相机清单（方位/清单标识/型号/序列号/在线/未发现）+ 汇总 + 六面聚合（A9） |
| `GET /api/camera-positions` | 相机方位映射的当前内容（A9） |
| `POST /api/camera-positions` | 保存方位映射（写 `config\camera-positions.ini`，自动备份） |
| `GET /api/config` | 当前 SDK 配置摘要（provider、mode/num、触发模式、相机清单、方位条数、脚本是否就绪、最近一次应用结果） |
| `POST /api/config/apply` | 一键应用配置：调 `tools\apply-config.ps1` 做 写配置 → 重启校验 → 失败回滚，返回退出码与完整输出（A8） |
| `GET /api/history?from=&to=&code=&deviceId=&noread=&limit=` | 历史查询（读历史文件，支持时间范围、条码、相机、无码过滤） |
| `GET /api/images?path=<绝对路径>` | 按需读取原图（只允许图片根目录内的文件，越权返回 400） |
| `GET /api/images/thumb?path=&w=160` | **B7** 缩略图（BMP 真缩小并缓存；JPEG 回退原图） |
| `GET /api/images/info?path=` | **B7** 图片元信息（字节/时间/后缀/是否支持缩略图） |
| `GET /api/monitor/summary` | **B8** 监控汇总（相机数、在线/离线、平均在线率、活动告警数） |
| `GET /api/monitor/cameras` | **B8** 每台相机的在线率 / 心跳 / 最近出码 / 掉线次数 / 当前状态 / 活动告警 |
| `GET /api/monitor/events?limit=&camera=` | **B8** 掉线、上线、恢复、告警 事件流（可查、可按相机过滤） |
| `GET /api/monitor/alerts?limit=&activeOnly=` | **B8** 告警列表（默认只看活动告警） |
| `GET /api/monitor/config` / `POST /api/monitor/config` | **B8** 告警阈值读写（保存自动备份、立即生效） |
| `GET /api/cameras/counters` | **C2** 相机计数（出码数/掉线次数/方位），相机状态墙首屏用 |
| `GET｜POST /api/config/storage` | **C5** 存图策略读写（校验 + 自动备份；写 gateway.ini，重启采集宿主生效） |
| `GET /api/config/backups` | **C5** 列出 `config\` 与 `Cfg\` 下的配置备份（含生效方式） |
| `POST /api/config/rollback` | **C5** 用某个备份还原（回滚前会把当前内容另存） |
| `GET /api/stats/board?from=&to=&dimension=&deviceId=` | **C4** 统计看板（维度：camera / shift / hour / day），返回总览 + 分组 + 耗时 |
| `GET｜POST /api/stats/shifts` | **C4** 班次配置读写（写要登录；保存自动备份、立即生效） |
| `GET /api/diag/sources` | **C6** 日志来源概览（文件数、大小、最新文件） |
| `GET /api/diag/files?source=&from=&to=` | **C6** 某来源在时间范围内的文件列表 |
| `GET /api/diag/tail?source=&file=&lines=` | **C6** 看某个日志文件的尾部（默认 200 行） |
| `GET /api/diag/download?source=&file=` | **C6** 单文件下载（限该来源目录内） |
| `GET /api/diag/bundle?from=&to=&sources=` | **C6** **一键打包 zip**（按来源分目录 + README，离线可解） |
| `POST /api/auth/login` | **B9** 登录（返回 token + 写 HttpOnly Cookie；密码错返回剩余次数、锁定返回 423） |
| `GET /api/auth/status` / `GET /api/auth/me` / `POST /api/auth/logout` | **B9** 登录态（公开）/ 当前账号 / 退出 |
| `POST /api/auth/password` | **B9** 改密码（带 username = 管理员重置，会自动解锁） |
| `GET｜POST /api/auth/users`（含 `update`/`delete`/`reset-password`） | **B9** 账号管理（admin） |
| `GET｜POST /api/auth/config`、`POST /api/auth/service-key` | **B9** 鉴权策略与令牌轮换（admin） |
| `GET /api/auth/events?limit=` | **B9** 登录/失败/锁定/改密审计（admin） |
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

指纹是**落盘归档**的。这一点很重要：平台异常退出后 spool 被整段重读时（位点回退、offsets.json 丢失），
一样能把重复事件丢掉 —— 这是"重复上报不重复计数"最容易翻车的场景。

### 去重指纹归档（按 traceId）

```
runtime\data\dedup\
├─ applied-index.jsonl        一个 traceId 一行：{"t":"P1","k":["fp:..."],"s":最后出现时间}
├─ wal-<时间戳>.jsonl         运行期只追加写：一条 = 一次已处理事件（顺序写、崩溃安全）
└─ applied-*.jsonl.imported   旧格式（自动导入后改名保留，不删原件）
```

* **运行期**：只往 WAL 追加，一次顺序写，不重写索引（每个包裹事件一次也扛得住）；
* **整理**：平台启动时自动一次，之后每 10 分钟检查、WAL 超过 5000 行时整理 —— 把索引写成新的
  `applied-index.jsonl`（先写 `.tmp` 再原子替换）→ 删掉已合并的 WAL → 按保留期清理；
* **保留期**：默认 30 天。太久没出现过的 traceId 会被清掉，所以**磁盘占用只跟"最近 30 天有多少包裹"有关，
  不随运行时长无限增长**；
* **崩溃安全**：整理过程中断电，最坏情况是 WAL 被重复导入一次 —— 指纹是集合语义，重复导入无害。

实测数字：3 个包裹（各 2 条指纹）= 索引 **380 字节**（约 127 字节/包裹）。
按现场一天 5 万件算，约 6.4 MB/天、30 天约 190 MB；不做保留期的话一年会涨到 2 GB+
而且启动要扫越来越多的文件。

> 为什么不用 SQLite：当前开发/现场环境是离线交付，拿不到 NuGet 包源（`dotnet package search` 直接报"未找到包源"）。
> `DedupStore` 的对外接口只有 `Load / Append / Compact / Stats` 四个方法，以后有包源了换成 SQLite 只改这一个类。

配置在 `runtime\platform\appsettings.json`：

```json
"Dedup": {
  "Directory": "data/dedup",
  "RetentionDays": 30,
  "CompactWhenWalLines": 5000,
  "CompactEveryMinutes": 10
}
```

界面上（配置页 →「去重指纹归档」）能看到保护的包裹数、索引大小、未合并 WAL、保留期、上次整理时间，
还有一个「立即整理」按钮；接口是 `GET /api/dedup`、`POST /api/dedup/compact`。

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

## 七、条码过滤规则（B2）

现场读到的码不都是运单号：有设备码、测试码、误识别的噪声码。规则就是把它们挑出来丢掉，
或者反过来"只认某几类码"。规则文件是 `runtime\config\barcode-rules.json`（UTF-8）：

```json
{
  "defaultAction": "drop",
  "ignoreCase": true,
  "rules": [
    { "name": "拉黑单号", "priority": 8,  "enabled": true, "action": "drop", "blacklist": ["SF0000000000"] },
    { "name": "测试码",   "priority": 5,  "enabled": true, "action": "drop", "prefix": "TEST" },
    { "name": "SF 运单",  "priority": 10, "enabled": true, "action": "keep", "prefix": "SF", "minLength": 12, "maxLength": 14 },
    { "name": "京东单号", "priority": 20, "enabled": true, "action": "keep", "regex": "^JD\\d{10}$" },
    { "name": "噪声码",   "priority": 30, "enabled": true, "action": "drop", "whitelist": ["*NOISE*"] }
  ]
}
```

**匹配语义**

* 一条规则里**填了的条件必须全部满足**（AND）：长度范围、前/后缀、正则、白名单、黑名单；
* 规则之间按 `priority` **从小到大**依次判断，**第一条命中的规则决定结果**（上面例子里"拉黑单号"优先级 8,
  比"SF 运单"的 10 更靠前，所以被拉黑的那个单号先被丢掉）；
* 一条规则都没命中时，按 `defaultAction`（keep / drop）处理；
* 名单支持 `*` 通配：`SF*`、`*0001`、`*JD*`。

**现场安全**

| 保护 | 说明 |
|---|---|
| 正则超时 | 正则匹配限 50ms；写得太复杂只会判成"不命中"并告警，**不会把平台卡住** |
| 非法规则被拒 | 优先级重复、正则语法错、动作写错、最小长度>最大长度 —— 保存时直接返回 400 并说明原因 |
| 自动备份 | 每次保存前把原文件备份成 `barcode-rules.json.bak-<时间戳>` |
| 热加载 | 平台每秒检查一次规则文件，**现场直接改文件就能生效**，不用重启 |
| 零开销 | 没配规则时不做任何额外判断，行为与之前完全一致 |

**能被看到的结果**（这是 B2 好不好用的关键）

* 过包行会直接标出被丢掉的码和命中的规则：`NOREAD 丢弃：TEST0002（规则 测试码）`，
  所以"这单为什么是无码"一眼就有答案；
* 配置页的**规则测试**可以先把一批条码粘进去试跑（用的是界面上**还没保存**的规则），
  逐条给出保留/丢弃、命中的规则名和人话原因；
* 实时页底栏与 `/api/stats` 有 `filteredCodes`（被丢掉的码数）、`filteredToNoread`（因过滤变成无码的包裹数）、
  `ruleCount`（当前启用的规则数）。

**接口**

| 接口 | 说明 |
|---|---|
| `GET /api/rules` | 当前规则（含文件路径、启用条数、默认动作） |
| `POST /api/rules` | 保存规则（自动备份、立即生效；非法规则返回 400） |
| `POST /api/rules/test` | 规则测试：`{codes:[...], ruleset?:{...}}`，不落盘 |
| `GET /api/rules/filtered?limit=` | 本次运行最近被丢弃的条码（用于现场调规则） |

**回归测试**（离线，不需要相机）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-b2-rules.ps1
```

脚本会试跑一份"长度+前缀+正则+黑白名单+优先级"的组合规则、保存后喂事件核对过滤统计、
直接改文件验证热加载、再验证非法规则会被拒绝，共 40 项断言。

## 八、历史库与查询导出（B3）

**存什么**：每个包裹一条最终记录，字段覆盖 B3 要求 —— 条码、时间、相机、图片路径、无码标记、下发状态
（另外还有重量体积、追踪号、下发次数/错误）。

```
runtime\data\
├─ parcels-yyyyMMdd.jsonl        快照：每次事件更新追加一行（崩溃安全，断电不丢）
└─ parcels-yyyyMMdd.index.jsonl  索引：一个 traceId 一行 = 该包裹的最终状态
```

**为什么要索引**：一个包裹会写 2 行快照（先条码后重量体积），一天 5 万件就是 10 万行；
查询时逐行解析既慢又占内存。索引把它收敛成"一个包裹一行"，并且**可以随时从快照重建**
（索引删了、坏了都不影响数据，只是慢一点）。今天正在写的那个文件直接读快照，
过去封盘的日期读索引；平台启动时会自动把过去几天整理一遍。

**实测性能**（10 万条历史、每条模拟两次回调 = 20 万行快照 106 MB → 索引 47 MB）：

| 查询 | 结果 | 服务端耗时 |
|---|---|---|
| 全量（第 1 页 200 条） | 命中 100000 | **757 ms** |
| 仅无码 | 命中 4000 | **725 ms** |
| 按相机 `cam-3` | 命中 16667 | < 1 s |
| 按条码精确匹配 | 命中 1 | < 1 s |
| 只看有图 | 命中 33334 | < 1 s |

验收标准是"10 万条 2 秒内返回"，实测约 **0.7 秒**（返回里带 `elapsedMs`，界面也会显示，现场可自证）。

**查询条件**：时间范围、条码（部分匹配）、相机（部分匹配）、无码（全部/仅无码/仅有码）、
下发状态（待下发/已下发/下发失败）、是否有图、分页（offset+limit，返回 `total` 命中总数）。

**导出 CSV**：字段是 `时间, 追踪号, 条码, 条码数, 无码, 相机, 重量, 长, 宽, 高, 体积, 图片数, 图片路径,
下发状态, 下发时间, 下发尝试, 下发错误, 采集时间戳`，带 UTF-8 BOM（Excel 双击不乱码），
异步流式写出（Kestrel 默认禁止同步写响应体，同步写会直接 500）。

界面上是顶部 **历史查询** 页签：日期范围（含"今天 / 近 7 天"快捷键）、条码、相机、无码、下发状态、
图片、每页条数 → 查询 / 上一页 / 下一页 / 导出 CSV，行里有图片链接与下发状态徽标，
底下一行显示"命中 N 条，显示 a-b · 服务端耗时 x ms（走索引）"。

接口：`GET /api/history?...`、`GET /api/history/export?...`（参数相同，导出不带 limit/offset）。

回归测试（离线，会自己造 10 万条数据）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-b3-history.ps1
```

覆盖：索引收敛、全量/各种过滤/分页、CSV 行数与表头字段、**硬杀进程后重启数据仍在**（断电不丢），
共 27 项断言。

## 九、下游 TCP 输出（B4）

把包裹数据按下游协议发出去：**TCP 客户端 + 可配置报文模板 + 失败重传**。
配置文件 `runtime\config\downstream.json`（首次启动自动生成，默认**不启用**，避免误发）：

```json
{
  "enabled": true,
  "protocol": "tcp-client",
  "host": "192.168.1.50",
  "port": 9000,
  "template": "{code}|{time}|{camera}|{weight}|{volume}|{traceId}\\r\\n",
  "encoding": "utf-8",
  "connectTimeoutMs": 3000,
  "retryIntervalMs": 5000,
  "maxAttempts": 0,
  "sendOnlyComplete": true,
  "sendIntervalMs": 0
}
```

**数据格式模板**：字段名写在花括号里，支持 `\r \n \t \\` 转义，所以一行模板就能写出分隔符和换行。
可用字段：`{traceId} {code} {codes} {codeCount} {noread} {time} {timestamp} {camera} {deviceId}
{weight} {length} {width} {height} {volume} {image} {imageCount} {position} {dispatchAttempts}`。
写错的字段名**不会被静默丢掉** —— 渲染时原样保留，"模板预览"和保存校验都会报出来。

**不丢不重是怎么保证的**：

| 机制 | 说明 |
|---|---|
| 队列在历史库里 | 包裹的 `dispatchState`（待发/已发/失败）持久化，平台重启后队列自动恢复 |
| 发成功才标记 | 先写 socket、再 ack 标记 `sent`；下游断线期间只是排队，重连后每条只发一次 |
| 连接活性检测 | 对端进程被杀时 socket 不会立刻报错，直接写会"假成功"（数据丢进黑洞却标记已发）。所以**写之前先检测对端 FIN/错误**，判定断线就重连、包裹继续排队 |
| 失败重试 | 发送失败记录 `failed` + 原因，按 `retryIntervalMs` 一直重试（`maxAttempts=0` 表示不限次数） |
| 幂等键 | 默认模板带 `{traceId}`；唯一可能重复的窗口是"写成功但进程在 ack 前被杀"，下游按 traceId 去重即可 |

**界面**（配置页 →「下游输出（TCP）」）：启用开关、地址/端口、编码、重试间隔、"只发完整包裹"、
模板编辑框、**测试连接**、**模板预览**（用最近一条包裹渲染，显示出实际报文和字节数）、
状态行（连接状态/已下发/失败/待发/重试/最近错误）、最近下发记录表。

接口：`GET /api/downstream`（配置+状态）、`POST /api/downstream`（保存，自动备份、立即生效）、
`POST /api/downstream/test`（测试连接）、`POST /api/downstream/preview`（模板预览）、
`GET /api/downstream/log?limit=`（最近下发记录）。

回归测试（脚本自己起一个 TCP 服务端，不需要真实下游）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-b4-downstream.ps1
```

覆盖：模板格式与字段、断线期间不丢、恢复后自动补发且**每条只发一次**、换模板立即生效、
模板校验，共 31 项断言。

## 十、下游 TCP 服务端输出（B5）

B4 是"平台连下游"（客户端模式），B5 反过来：**平台监听端口，下游连上来**，
一有包裹就广播给**所有已连接的下游**。配置就是同一份 `downstream.json`，把 `protocol` 换成 `tcp-server`：

```json
{
  "enabled": true,
  "protocol": "tcp-server",
  "host": "0.0.0.0",          // 绑定地址（服务端模式下 host 是"本机要绑哪个网卡"）
  "port": 9000,               // 监听端口
  "template": "{traceId}|{code}|{weight}\r\n",
  "retryIntervalMs": 1000,
  "sendOnlyComplete": true,
  "replayRecentCount": 0      // 新客户端接入时补发最近 N 条（0=不补发）
}
```

**多客户端与断线行为**

| 场景 | 行为 |
|---|---|
| 多个下游同时接入 | 每个客户端一个独立连接，包裹**广播**给所有在线客户端 |
| 某个客户端断开 | 只把这一个客户端移除（写失败或检测到对端 FIN），**不影响采集、也不影响其他客户端** |
| 一个客户端卡住 | 它自己的写失败只影响自己，其他客户端照常收 |
| 没有客户端在线 | 包裹留在待发队列（`dispatchState=pending`），**不算失败、不会丢** |
| 客户端接入 | 立刻开始收新包裹；`replayRecentCount>0` 时先补发最近 N 条（下游重启后能补数据） |
| "已下发"的判定 | 广播那一刻**只要有一个客户端写成功**就算已下发（其余靠 replay 补），符合现场"下游收到就行"的语义 |

界面（配置页 →「下游输出（TCP）」）加了**模式下拉**（客户端 / 服务端），
切到服务端后地址/端口标签自动变成"绑定地址 / 监听端口"，并多出一块
**已接入的下游客户端**列表（编号 / 远端地址 / 接入时间 / 已发条数 / 字节数 / 最近错误）。

回归测试（脚本自己起两个 TCP 客户端）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-b5-tcp-server.ps1
```

覆盖：监听成功、两个客户端同时接入并都收到广播、杀掉一个客户端后另一个不受影响、
采集继续正常入库、客户端重连后恢复接收、**没有客户端时包裹留在队列且客户端接入后自动补发**，
共 20 项断言。

## 十一、HTTP 推送输出（B6）

除了 TCP，还能把包裹数据用 **HTTP POST** 推给下游。同一份 `downstream.json`，`protocol` 换成 `http`：

```json
{
  "enabled": true,
  "protocol": "http",
  "url": "http://192.168.1.50:8080/dws/parcel",
  "template": "{\"code\":\"{code}\",\"time\":\"{time}\",\"weight\":{weight},\"traceId\":\"{traceId}\"}",
  "contentType": "application/json",
  "idempotencyHeader": "Idempotency-Key",
  "headers": ["Authorization: Bearer xxxx"],
  "httpTimeoutMs": 5000,
  "retryIntervalMs": 5000,
  "sendOnlyComplete": true
}
```

**幂等键**：每个请求都会带上 `Idempotency-Key: <traceId>`（头名可配）。重试时会用**同一个键**，
所以下游只要按这个键去重，重复请求就不会产生重复业务 —— 这正是验收里"重复请求不产生重复业务"的做法。

**失败重试**：只有 **2xx** 算成功；其他状态码（含 4xx/5xx）、超时、连不上，都会记成 `failed`
并把状态码与响应片段记下来，然后按 `retryIntervalMs` 一直重试（`maxAttempts=0` 表示不限次数）。
队列在历史库里，所以平台重启、下游长时间不可用都不会丢包。

**实测**（脚本自己起 HTTP 接收端，并对指定订单注入 2 次 500）：

| 场景 | 结果 |
|---|---|
| 推送 3 个包裹 | 接收端收到 3 条，请求体符合模板，每个请求都带 `Idempotency-Key=<traceId>` |
| **注入 500（B6-2 前 2 次）** | 接收端先记 2 条 FAIL、再记 1 条 OK → **自动重试并最终成功** |
| 同一包裹的重试 | 三次请求带的是**同一个幂等键** `B6-2`（下游可按它去重） |
| 接收端不可用时发 2 个包裹 | 留在待发队列（不丢），`lastError` 里有原因 |
| 接收端恢复 | 自动补发，最终 5 个追踪号各自成功一次，队列清空 |

界面（配置页 →「下游输出」）模式里多了一个 **HTTP 推送**：填接口地址、超时、幂等键头名、
额外请求头（如 Authorization），模板照旧；状态行会显示 HTTP 目标与幂等键头名。

回归测试：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-b6-http.ps1
```

共 22 项断言。

## 十二、图片按需访问与缩略图（B7）

| 接口 | 说明 |
|---|---|
| `GET /api/images?path=` | 原图（只允许图片根目录内的文件，越权返回 400） |
| `GET /api/images/thumb?path=&w=160` | **缩略图**：BMP 原图按块平均真缩小，结果缓存；JPEG 回退原图 |
| `GET /api/images/info?path=` | 元信息（名称/字节/修改时间/后缀/是否支持缩略图） |

**为什么不引图像库**：现场是离线交付，拿不到 NuGet 包（`System.Drawing.Common` / `ImageSharp` 都取不到）。
而采集侧落盘的原图本来就是 **BMP**（大华 SDK 的原始图按 BMP 写），BMP 是无压缩位图 ——
读像素、按块平均、再写 BMP，**纯算术就能做，零依赖**。

**实测**（800×600 BMP 原图 1,440,054 字节 → `w=200` 缩略图 90,054 字节，200×150）：
首次生成 156 ms，缓存命中 121 ms（这时耗时其实是 HTTP 传输 90KB 的开销，不是计算）。
判断"命中缓存"不能看耗时，而是看**缓存文件有没有被重写** —— 回归脚本就是按这个断言的。

**不占用采集进程**：图片的缩略、缓存、传输全在平台进程里做，采集宿主只管落盘；
顺带一个约束：接口只读图片根目录内的文件（白名单），越权与不存在的文件分别返回 400 / 404。

**前端**：实时过包页与历史查询页的图片列现在**直接显示缩略图**（`<img loading="lazy">`，
实时页 160px、历史页 120px），点击缩略图看原图 —— 列表加载不会再拉几十张原图。
浏览器里实测：`img.thumb` 的实际解码尺寸 160×120，外层链接指向 `/api/images?path=...`。

**已知取舍**：SDK 直接给 JPEG 时离线没有解码器，`thumb` 会**回退成原图**
（`/api/images/info` 里 `thumbSupported=false` 就是提示）。以后有包源了接上
System.Drawing.Common / ImageSharp 即可覆盖 JPEG，接口与前端都不用改。

回归测试：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-b7-images.ps1
```

覆盖：元信息、缩略图尺寸/体积、缓存不重算、白名单越权、文件不存在、JPEG 回退、
**并发 8 个取图请求**、以及取图期间采集照常入库，共 19 项断言。

## 十三、相机状态监控与告警（B8）

现场最怕的不是"没数据"，而是"某台相机悄悄掉了，三天后才发现"。这一段把相机的**在线率、掉线记录、心跳、告警**
做成可查、可推、可配 —— 判定全在平台侧做，采集宿主一行都不用改。

### 判定口径

| 指标 | 口径 |
|---|---|
| 心跳 | 最近一次收到该相机的**任何**数据：状态事件（上/下线）或出码事件。出码是最强的心跳 —— 它说明"相机 + SDK + 落盘 + spool"整条链都在工作 |
| 在线率 | 累计在线时长 /（在线 + 离线）时长；默认统计窗口 60 分钟。平台重启后从"重启那一刻"重新累计，不拿磁盘里的旧时间戳造数 |
| 掉线记录 | 每一次上线/掉线都写一条事件，落盘 `runtime\data\camera-events-yyyyMMdd.jsonl`（一条一行 JSON，重启后照样能查） |

### 五类告警

| 告警码 | 含义 | 默认阈值 |
|---|---|---|
| `camera-offline` | 相机离线，且持续超过阈值（闪断不刷屏） | 10 秒 |
| `heartbeat-timeout` | 超过阈值没收到任何数据 —— 兜住"掉线压根不上报"的相机（拔网线、断电、交换机端口坏了） | 60 秒 |
| `frequent-offline` | 窗口内掉线次数达到阈值（网线接触不良、供电不稳的典型症状） | 30 分钟内 3 次 |
| `low-online-rate` | 在线率低于阈值 | 95%（窗口 60 分钟） |
| `declared-missing` | cfg 清单里声明了、但 SDK 没发现（没上电/没接网/被别的软件占用） | 立即，critical |

告警**产生与恢复都记事件、都推界面**；条件恢复后自动消除，不需要人工清。

### 接口

| 接口 | 说明 |
|---|---|
| `GET /api/monitor/summary` | 汇总：相机数、在线/离线、平均在线率、活动告警数 |
| `GET /api/monitor/cameras` | 每台相机：在线率、心跳时间与新鲜度、最近出码、掉线次数、当前状态持续时长、活动告警 |
| `GET /api/monitor/events?limit=&camera=` | 掉线/上线/恢复/告警 事件流（可按相机过滤） |
| `GET /api/monitor/alerts?limit=&activeOnly=` | 告警列表（默认只看活动告警） |
| `GET /api/monitor/config` / `POST /api/monitor/config` | 阈值读写（保存自动备份、立即生效） |

SSE 新增两种消息：`type=monitor`（监控快照，按检查间隔周期推 —— 界面上的"心跳 12 秒前""在线率 48.2%"自己会走）
和 `type=alert`（单条告警产生/恢复，用来即时提示）。

### 界面

* 实时监控页顶部**告警条**：正常是绿色"相机状态正常：6 / 6 台在线"；有告警就变黄/红，列出前三条，点一下跳到设备信息页；
* 设备信息页**相机状态监控**面板：在线率 / 最近心跳 / 最近出码 / 掉线次数 / 当前状态持续 / 活动告警 + 掉线告警记录表；
* 配置页**监控与告警阈值**面板：心跳超时、离线告警、检查间隔、频繁掉线次数与窗口、在线率下限与窗口、事件保留天数。

### 配置与落盘

| 文件 | 内容 |
|---|---|
| `runtime\config\monitor.json` | 阈值（首次运行自动生成；支持热加载，平台每秒检查一次文件，改完不用重启） |
| `runtime\data\camera-events-yyyyMMdd.jsonl` | 事件流：一条一行；平台启动时恢复最近 3 天 |

**重启语义**：从磁盘恢复出来的相机，心跳与离线计时都从"平台启动时刻"起算 ——
否则平台一重启就会拿磁盘里的旧时间戳报一堆假警（这是实测踩到的坑，记在回归脚本里）。

回归测试：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-b8-monitor.ps1
```

覆盖：5 台相机（正常 / 清单里没发现 / 沉默 / 频繁闪断 / 在线率过低）→ 在线率与心跳数值、掉线记录条数与按相机过滤、
五类告警、掉线→恢复后告警自动消除并记录离线时长、出码当心跳使告警消除、事件落盘与平台重启后恢复、
阈值读写 / 非法值拒绝 / 旧配置自动备份、以及监控不影响原有链路（包裹照常入库、设备页照常显示、SSE 补发带监控快照），
共 **54 项断言**。

实现位置：`src\DwsEdge.Platform\CameraMonitor.cs`（判定与统计）+ `MonitorWatcher`（定时检查并推快照），
`SpoolStore` 把相机状态与出码事件喂给它；前端在 `frontend\src\monitor.ts`。

## 十四、账号登录与接口鉴权（B9）

目标很明确：**未登录不能碰管理接口；密码错误有次数限制**。做法是最小可用、不引第三方库（现场离线）。

### 首次运行会自动生成什么

| 文件 | 内容 |
|---|---|
| `runtime\config\users.json` | 账号表：用户名、角色、PBKDF2 哈希、失败计数、锁定截止、最近登录 |
| `runtime\config\auth.json` | 策略：失败上限、锁定时长、会话时长、读接口保护开关、**服务令牌** |
| `runtime\config\admin-initial-password.txt` | 首次运行随机生成的 `admin` 初始密码（明文，登录后请改密，文件会自动删除） |

初始密码不写死在代码里，也不打进日志（日志会被拷来拷去）。改过密码后这个文件会被自动删除。

### 密码与失败限制

* 密码只存 **PBKDF2-SHA256**（每个账号独立盐、12 万次迭代、32 字节哈希），校验用固定时间比较；
  账号文件里搜不到任何明文口令（回归脚本专门断言了这一点）。
* 强度要求：至少 8 位、同时含字母和数字。
* 失败限制：默认**连续错 5 次锁定 15 分钟**；每次失败都返回**还剩几次**；到上限返回 `423` 与剩余秒数；
  锁定期间**密码正确也拒绝**（这是回归里专门验证的一条）。
* 失败计数有滑动窗口（默认 10 分钟）：窗口内没再失败就清零，避免"很久以前错两次 + 今天错三次"被误锁。
* 救场手段有两个：管理员在界面上"重置密码"，或者用服务令牌调 `POST /api/auth/users/reset-password`（重置会一并解除锁定）。

### 三种凭据（同一套权限判断）

| 凭据 | 谁用 | 怎么带 |
|---|---|---|
| 会话 Cookie（HttpOnly） | 浏览器界面 | 登录后由浏览器自动带上 |
| Bearer token | 程序 / 上位机 | `Authorization: Bearer <token>`（登录接口会返回） |
| 服务令牌 | 回归脚本、采集侧集成 | `X-Api-Key: <auth.json 里的 serviceKey>`，可一键轮换（旧令牌立刻失效） |

会话默认 480 分钟；平台重启后会话失效（内存态，需要重新登录）。改密码会把该账号的**其它**会话踢掉，当前这次保留。

### 接口保护范围

| 类别 | 接口 | 未登录 |
|---|---|---|
| 永远公开 | `/api/health`、`/api/auth/login`、`/api/auth/status` | 放行（存活检查 + 前端判断登录态） |
| 管理接口 | `/api/config*`、`/api/rules*`、`/api/downstream*`、`/api/camera-positions`、`/api/monitor/config`、`/api/dedup/compact`、`/api/dispatch/ack` | **401** |
| 账号接口 | `/api/auth/users*`、`/api/auth/config`、`/api/auth/events` | **401**；且只有 admin 能用 |
| 读接口 | `/api/stats`、`/api/parcels`、`/api/cameras`、`/api/devices`、`/api/history*`、`/api/images*`、`/api/monitor/summary|cameras|events|alerts`、`/api/stream` | 默认放行；把 `protectRead` 打开就也要登录 |

读接口默认不拦，是因为现场大屏和第三方取数不该被账号卡住；要"全保护"就在界面上勾一下，或者改 `auth.json` 的 `protectRead`。

### 角色

| 角色 | 读数据 | 改配置（规则/下游/监控阈值/一键应用） | 管账号与策略 |
|---|---|---|---|
| admin | ✅ | ✅ | ✅ |
| operator | ✅ | ✅ | ❌ 403 |
| viewer | ✅ | ❌ 403 | ❌ 403 |

两个安全兜底：**不允许把最后一个启用的管理员降级/禁用/删除**，也不允许管理员把自己删没。

### 审计

登录成功、密码错误、账号锁定、退出/会话过期、改密码、账号增删改、策略变更，全部写
`runtime\data\auth-events-yyyyMMdd.jsonl`（一条一行），界面上也能看最近 100 条。

### 接口

| 接口 | 说明 |
|---|---|
| `POST /api/auth/login` | 登录（返回 token 并写 Cookie；失败返回剩余次数 / 锁定秒数） |
| `POST /api/auth/logout` / `GET /api/auth/me` / `GET /api/auth/status` | 退出 / 当前账号 / 登录态（status 公开） |
| `POST /api/auth/password` | 改密码（填 username = 管理员重置） |
| `GET｜POST /api/auth/users`、`/api/auth/users/update｜delete｜reset-password` | 账号管理（admin） |
| `GET｜POST /api/auth/config`、`POST /api/auth/service-key` | 策略读写 / 轮换服务令牌（admin） |
| `GET /api/auth/events?limit=&kind=` | 鉴权审计（admin） |

### 界面

* 未登录弹**登录遮罩**（读接口没保护时还有个"先不登录（只读浏览）"，现场大屏不用账号）；
* 顶栏显示当前账号与角色，带"退出"；
* 配置页 **账号与安全**：改自己的密码、管理员改策略 / 加账号 / 禁用 / 重置密码 / 看登录记录 / 轮换服务令牌；
* 任何管理接口回 401（会话过期、被别人踢下线）→ 自动弹回登录框。

### 回归脚本怎么调管理接口

脚本走"服务令牌"这条路：在第一次 `Start-Platform` 之后调用一次
`tools\b9-auth-helper.ps1` 里的 `Enable-TestAuth`，之后脚本里所有 `Invoke-RestMethod` /
`Invoke-WebRequest` 都会自动带上 `X-Api-Key`（B1-B8 脚本已按这个方式接好）。

回归测试：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-b9-auth.ps1
```

覆盖：首次运行生成账号与初始密码、密码不明文落盘、12 个管理接口未登录全 401、
读接口匿名可用、连续错误返回剩余次数并在第 5 次锁定（锁定期正确密码也进不去）、
服务令牌读写与重置密码、Cookie 与 Bearer 两条会话路径、登出立即失效、
改密后旧密码失效、viewer/operator/admin 权限边界、最后一个管理员不能降级/删除、
策略读写与非法值拒绝、读接口保护开关、服务令牌轮换后旧令牌立刻失效、审计落盘与采集不受影响，
共 **96 项断言**。

**安全边界（要知道自己在哪）**：V1 是**内网 HTTP**，登录口令在网络上是明文传输的 ——
它拦的是"局域网里乱点的人"和"没凭据就改配置的脚本"，不是公网攻击。真要出公网，
下一步要么套 HTTPS 反向代理，要么在采集侧和平台之间加设备证书，这是 C 类里的事。

## 十五、实时过包卡片墙（C1）

实时监控页的"最新过包"默认是**卡片墙**：一张卡 = 一个包裹，主要信息一眼看完，缩略图直接可见。
喜欢表格的现场可以切回表格，选择记在浏览器里（下次打开还是上次那个视图）。

### 一张卡片上有什么

| 位置 | 内容 |
|---|---|
| 左 | 缩略图（`/api/images/thumb?w=320`，平台生成并缓存）；**点击看原图**；没图显示"无图"占位 |
| 右上 | 条码（多个码都列出），每个码后面跟方位/类型标签，例如 `SF700000000001 顶面/1D` |
| 右中 | 时间 · 相机，例如 `2026-09-18 10:32:05.120 · cam-left` |
| 右下 | 状态徽标：`读码 N` / `NOREAD` / `待补全` / `更新 ×N` / `图 N` / `已下发` / `下发失败×N` / `规则丢弃 N` |
| 最下 | 重量与体积（有就显示）：`1.24 kg · 320×210×160 mm · 10752 cm³` |

排序与容量：卡片按**最近一次更新**倒序（补全重量体积的同一条记录会原地更新并挪到最前，带一次高亮动画），
最多保留 **30 张**，超出自动淘汰最旧的。

### 实时刷新是怎么来的

```
采集宿主 → spool\events-*.jsonl → 平台合并 → SSE（type=parcel）→ 卡片墙就地更新（KPI 同步刷新）
```

同一包裹第二次回调（补重量体积）到达时，卡片**原地更新**：条码/状态/尺寸跟着变，缩略图地址没变就不重新请求（不会闪白）。
页面断线后 SSE 自动重连，平台在连接建立时会补发最近 20 条包裹，所以刷新页面也不会空。

### 实测

| 项 | 实测 |
|---|---|
| 4 个包裹（含两次回调合并 / 无码 / 无图） | 卡片墙 4 张，最新一张在最前，`无图`占位 1 个 |
| 缩略图 | 800×600 BMP 原图 → `w=320` 缩略图 **320×240**，体积不到原图 1/5 |
| 实时刷新 | 追加一个包裹后（**页面不刷新**）卡片 4 → 5，新卡片在最前并带高亮；KPI 包裹数同步 4 → 5 |
| 视图切换 | 卡片（grid）/ 表格（5 行）来回切换正常，选择记忆在浏览器 |
| 缩略图是否真加载 | 3 张图的 `naturalWidth×naturalHeight = 320×240`（是缩略图，不是原图占位） |

**延迟参数（重要）**：事件是先落 spool、平台按 `Spool:PollIntervalMs` 去取，所以最坏延迟≈轮询间隔。
默认已调到 **100ms**（实测 38~129ms，满足"回调到界面小于 300 毫秒"）；早先的 500ms 实测最大 483ms、不达标。
现场 CPU 吃紧或 spool 文件特别大时可以把它调回 500。改 `runtime\platform\appsettings.json` 后重启平台生效。

自检脚本（跑完会留一个可以直接用浏览器看的运行时）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-c1-cards.ps1 -KeepRunning
# 然后浏览器打开 http://127.0.0.1:8098 看卡片墙
```

覆盖：卡片渲染需要的字段（条码+方位+类型/时间/相机/更新次数/图片路径）、两次回调合并成一张卡、
NOREAD 与无图两种边界、缩略图接口（BMP/320×240/体积）、追加包裹后的实时可见性、
以及部署产物里确实带卡片墙（`realtime.js` / `index.html`），共 **28 项断言**。

## 十六、相机状态墙（C2）

实时监控页的"相机状态"默认也是**一屏的墙**：每台相机一格，状态、掉线次数、出码计数、心跳、在线率、告警徽标全在一格里，
异常相机靠颜色和徽标一眼挑出来。同样可以切回表格（表格多了型号/序列号/恢复次数/最近变化这些"平时不看、排障要看"的列）。

### 一格上有什么

| 位置 | 内容 |
|---|---|
| 顶行 | 方位（顶面/左侧…）· 相机标识 · 在线 / 离线 / 未发现 |
| 大数字 | **出码**（累计出码包裹数，按 traceId 去重后）+ 掉线次数 + 心跳新鲜度 + 在线率 |
| 下面 | 最近出码时间；型号 · 序列号（有就显示） |
| 徽标 | 该相机当前的活动告警：`清单里没发现` / `相机离线` / `心跳超时` / `频繁掉线` / `在线率过低` |

颜色就是判断：**绿左边框=正常，红=离线，黄=清单里声明了但没发现**，有告警再加一层告警色边框。
点任意一格跳到"设备信息"页看这台相机的完整记录（掉线历史、方位编辑、型号/固件）。

### 数据是怎么拼起来的

一格的数据来自三路，界面按相机 ID 合并：

| 来源 | 负责什么 | 什么时候到 |
|---|---|---|
| `type=camera` / `GET /api/cameras` | 在线状态、方位、型号、序列号、掉线次数 | 状态变化时即时（实测 2 秒内） |
| `type=monitor` / `GET /api/monitor/cameras` | 在线率、心跳、活动告警、**出码数/最近出码** | 每 5 秒（可配 `checkIntervalSeconds`） |
| `type=camera-count` / `GET /api/cameras/counters` | 出码计数、掉线次数、方位（轻量，只推会随手一包就变的部分） | 出码后立即（节流 300ms） |

### 出码计数为什么之前不刷新（这次修掉的）

老实现里"出码数"只在**相机状态事件**到达时才更新，而包裹出码只改平台内存里的计数 ——
结果一台一直在出码的相机，界面上的数字会停在上一次上下线时的值，看起来像"没在干活"。

现在：包裹处理完就推一条轻量的 `camera-count`（合并 300ms 内的多次过包），
B8 的 5 秒监控快照里也带上了权威计数（出码数由 SpoolStore 提供，是**按 traceId 去重后**的数，不是回调次数），
所以"出码数"有两条路保证在 5 秒内一定是最新的。

### 实测

| 项 | 实测 |
|---|---|
| 一屏字段 | 3 台相机（在线 / 离线 / 未发现）各一格，出码·掉线·心跳·在线率·最近出码·告警徽标齐全 |
| 汇总行 | `在线 1 / 3　累计出码 5　异常 2 台`（告警恢复后异常台数自己从 3 变 2） |
| 出码实时性 | 连推 2 个包裹后**页面不刷新**：cam-top 出码 **3 → 5**，最近出码时间更新，心跳变成"8 秒前" |
| 状态实时性 | 推一条掉线事件，2 秒内该格由"在线"变"离线"、掉线次数 0 → 1 |
| 异常识别 | class 分别为 `camcell online` / `camcell offline alarm` / `camcell missing alarm`（绿/红/黄 + 告警边框） |
| 数据一致性 | `/api/cameras/counters`、`/api/monitor/cameras`、SSE 三处的出码数一致 |

回归测试：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-c2-wall.ps1
powershell -ExecutionPolicy Bypass -File .\tools\test-c2-wall.ps1 -KeepRunning   # 留着用浏览器看
```

覆盖：一屏需要的字段（出码数/掉线次数/心跳/在线率/告警/方位/未发现标记）、
**真连一次 SSE 验证建连补发与出码后推送里都带 codeCount**、5 秒内最终值到位（节流兜底）、
掉线后状态与计数同步、以及部署产物里确实带相机墙，共 **29 项断言**。

## 十七、配置页（简版）（C5）

配置页把四块现场最常改的东西做成了图形化，验收要求是三条：**保存即生效、可回滚、非法参数有提示**。

| 配置块 | 在哪 | 生效方式 | 回滚 |
|---|---|---|---|
| 相机清单（接入方式 + 值 + 方位） | 本页表格编辑（文本模式保留） | 一键应用：写 cfg → 启动 SDK 校验 → 不通过自动回滚 | 自动：Cfg 的 `.bak-` 备份 + 失败自动回滚 |
| 存图策略（存哪些图/保几天/磁盘水位） | 本页表单（C5 新增） | 写 `config\gateway.ini`，**重启采集宿主**后生效 | 自动备份 + 本页"备份与回滚"一键还原 |
| 输出对接参数（TCP 客户端/服务端、HTTP） | 下游输出面板（B4/B5/B6） | 保存即生效（发送服务按新配置重连） | 自动备份 + 一键还原 |
| 条码规则（长度/前后缀/正则/黑白名单） | 条码过滤规则面板（B2） | 保存即生效（热加载，最多 1 秒） | 自动备份 + 一键还原 |

### 存图策略（新）

写进 `config\gateway.ini` 的两处：provider 段的"存哪些图"，`[storage]` 段的"留多久"。

| 参数 | 含义 | 取值 |
|---|---|---|
| 保存原图 / 面单图 | 这两类图是否落盘 | 开 / 关 |
| 保存每台相机各自的图 | 需要同时回传每台相机的码信息（否则拿不到相机标识） | 开 / 关（组合校验） |
| 图片保存天数 | 超过天数的日期目录整目录删除 | 0-3650（0 = 永久保留） |
| 磁盘水位(%) | 达到后从最旧的日期目录开始删 | 0 或 10-99（0 = 关闭） |
| 清理间隔(分钟) | 清理线程运行间隔 | 1-1440 |
| 事件文件保留(天) | spool 事件保留天数（平台消费过的才删） | 0-3650 |
| 图片目录 | 相对 runtime 的路径 | 留空 = 沿用 provider；不能含 `..`、不能绝对路径 |

保存时**只改这几个键**：注释、空行、其他段原样保留（现场配置里全是说明性注释，被冲掉就没法排障了）。

### 相机清单表格

原来是一个大文本框，现在主入口是表格：每行 = 接入方式（ip / key / id）+ 值 + 方位下拉（顶面/底面/左侧/右侧/前侧/后侧/线体/备用），
可以增删行、点"校验清单"先看问题。前端校验规则：

* 值不能为空（空行自动忽略）；
* `ip` 必须是合法 IPv4（写错网段是现场"相机连不上"的头号原因，这里直接红字拦下来；要写主机名请改用 id/key）；
* 同一 kind 下不允许重复；
* 台数不在 12-17 时给"提醒"（不拦，狂扫或测试环境可能少）；
* 后端还会再校验一次（格式非法回 400 并指出第几行）。

### 备份与回滚（新）

`GET /api/config/backups` 把 `config\` 与 `Cfg\` 下的 `*.bak-时间戳` 全列出来，每行标注**还原目标**与**生效方式**
（平台配置热加载、采集宿主配置要重启宿主、SDK 配置要重新"一键应用"）。
点"回滚"还原时，**先把当前内容另存一份**（`.before-rollback-时间戳`），点错也能再救回来。
接口只接受文件名（不接受路径），`..`、没有 `.bak-` 标记的都会被 400 拒掉。

### 实测（浏览器里点出来的）

| 项 | 实测 |
|---|---|
| 相机清单表格 | 3 行 `[ip] 172.20.10.11 [顶面]`，可增删改 |
| 清单校验 | 正常：`✓ 清单校验通过（3 台）` + 提醒"3 台不在 12-17"；重复行：`✗ 第 3 行与第 2 行重复`；`ip=abc`：`✗ 不是合法的 IP` |
| 存图策略校验 | 天数 `-1` → `✗ 图片保存天数要在 0-3650 之间`；目录 `..\..\evil` → `✗ 不能包含 ..`；被拒后文件一个字没变 |
| 存图策略保存 | 改成 30 天 → `已保存 1 项`，输入框回读 30，并提示"要重启采集宿主才生效" |
| 备份列表 | 26 份（`LogisticsBase.cfg.bak-*` 与 `gateway.ini.bak-*`），带时间、大小、生效方式 |
| 回滚 | 回滚 gateway.ini 后保存天数从 14 回到 7；回滚前的内容另存为 `.before-rollback-*` |

回归测试：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-c5-config.ps1
powershell -ExecutionPolicy Bypass -File .\tools\test-c5-config.ps1 -KeepRunning   # 留着用浏览器看
```

覆盖：存图策略读取/保存/回读、注释与其他段不被冲掉、非法值逐项被拒且文件不变、
相机清单非法格式（提示到第几行）与合法应用（写进 cfg 并能读回方位）、
备份列表内容、回滚与"回滚前另存"、非法备份名被拒，共 **61 项断言**（含 8 项前端产物检查）。

**顺带修掉的一个真 bug**：存图开关（`saveOriginal` 等）是相机 provider 的参数。
测试环境把 `provider` 改成 `simulator` 后，旧实现会把这些键插进 `[simulator]` 段 ——
文件看着改了，但没有任何代码读它们，现场表现就是"改了没生效"。
现在会先找"已经有这些键的段"（一般是 `[dahua-dws]`），界面也会写明"存图开关写进 [dahua-dws]"。

## 十八、统计看板（简版）（C4）

统计页一屏回答三个问题：**一共多少包、读码率多少、无码率多少**，并且能切四个维度看：
按相机（谁在干活）、按班次（哪个班干得多）、按小时、按日期。数据全部来自历史库，
**口径与"历史查询"完全一致**（同一个索引、同一套过滤），返回里带服务端耗时方便现场核对性能。

### 看板上有什么

| 区域 | 内容 |
|---|---|
| 五个 KPI | 总包数、读码率、无码率、无码包裹数、有图 / 下发成功 |
| 分组表 | 维度值、总数、有码、无码、读码率、无码率、**占比条**、有图、下发成功、该组的时间范围 |
| 尾部 | 范围、维度、合计（有码/无码）、有图、下发成功/失败，以及口径说明 |
| 导出 | "导出统计 CSV"（当前维度 + 合计一起导出，UTF-8 BOM） |

### 班次维度怎么算

班次在页面下方可配（默认白班 08:00-20:00、夜班 20:00-08:00），写 `runtime\config\shifts.json`，保存后立即生效。规则：

* **跨天班次**（如 20:00-08:00）的凌晨时段算**前一天**那一班 —— 符合现场"这是昨晚那一班"的说法；
* 所以同一批凌晨 02:00 的包裹，**按日期**算今天、**按班次**算昨天夜班。回归里专门有一条断言盯这个差异；
* 每个分组用"班次名@班次日期"标识（例如 `夜班@2026-09-17`），组与组之间不重不漏；
* 没匹配到任何班次的包裹会单独列入"未匹配班次"，页面尾部会提示数量（提醒你班次没覆盖全时段）。

### 实测（浏览器 + 300 个包裹的回归数据）

| 项 | 实测 |
|---|---|
| 总览 | 总包数 **300**、读码率 **80.0%**、无码率 **20.0%**、无码 60 |
| 按相机 | cam-a **120**（83.3%）、cam-b **100**（80.0%）、cam-c **80**（75.0%），按包数从多到少 |
| 按班次 | 白班@昨天 100、夜班@昨天 150（含今天凌晨 02:00 的 50 包）、白班@今天 50，**三组之和 = 300** |
| 按小时 | 昨天 10 点那组合并了 100 包（10:00 与 10:30 属同一小时），共 5 组 |
| 按日期 | 昨天 200、今天 100（与"按班次"的口径差异就体现在凌晨那批） |
| 服务端耗时 | 300 条聚合 **12 ms**（走索引，10 万条级别也在秒级） |
| 班次设置 | 表格里可增删改（名称/开始/结束），非法值有提示；带"8 小时（跨天）"这样的时长说明 |

回归测试：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-c4-stats.ps1
powershell -ExecutionPolicy Bypass -File .\tools\test-c4-stats.ps1 -KeepRunning   # 留着用浏览器看
```

覆盖：总览数字、**与历史库对账**（`/api/history` 的 total 与快照文件行数都要对上 300）、
按相机（每台的包数与读码率）、按班次（各組之和 = 总数、跨天归属）、按小时、按日期、
相机过滤、未知维度不报错、班次读写与非法值被拒、**未登录改班次被 401 拦**、
以及部署产物里确实带统计页，共 **59 项断言**。

**顺带修掉的一个真 bug（C4 才暴露出来）**：历史快照原来按**写入时刻**分文件
（`parcels-<今天>.jsonl`），并且把行时间也记成写入时刻。后果是：跨零点、或者平台停机后补投历史数据时，
昨天的事件会落进今天的文件 —— **按日期查询与统计全部失真**。
现在按**事件时间**（`capturedAtMs`）分文件、行时间也记事件时间，
回归里新增两条断言专门盯"昨天的事件进昨天文件、今天的进今天文件"。

## 十九、日志查看与导出（C6）

排障时最烦的是"日志散在好几个目录、还要按时间挑"。诊断页把六类东西集中到一屏：

| 来源 | 目录 | 内容 |
|---|---|---|
| `host` | `runtime\logs\host-*.log` | 采集宿主控制台日志（上电、触发、出码、切图、错误） |
| `platform` | `runtime\logs\*.log`（排除 `host-*`） | 业务平台与运维脚本的日志 |
| `sdk` | `runtime\Log\*.log` | **大华 SDK 自己的日志**（算法/相机/体积/重量/MVP） |
| `spool` | `runtime\spool\events-*.jsonl` | 事件缓冲（A7：先落盘再推送） |
| `camera` | `runtime\data\camera-events-*.jsonl` | 掉线/上线/恢复/告警事件（B8） |
| `auth` | `runtime\data\auth-events-*.jsonl` | 登录成功/失败/锁定/改密审计（B9） |

### 三件事

1. **看**：每个来源一张卡（文件数、总大小、最新文件与时间）；点卡片列出该来源在时间范围内的文件；
   点某个文件的"查看"直接看**尾部 200 行** —— 不用把几百 MB 日志拉到浏览器里。
2. **导出**：单文件"下载"，或者按时间范围勾选来源**一键打包 zip**。
3. **打包内容可离线分析**：zip 里按来源分目录（`host/`、`sdk/`、`spool/`…），并附一份 `README.txt`
   写明生成时间、时间范围、各目录对应关系与排查顺序；标准 zip，系统自带解压即可，无加密。

### 时间范围怎么算

* 文件名里带日期的（`host-20260918.log`、`events-20260918.jsonl`）→ **按文件名里的日期**过滤；
* 不带日期的（`Alg.log`、`camera.log` 这类 SDK 日志）→ **按文件修改时间**过滤；
* 所以"近 3 天"这种范围，两种日志都能正确落到范围里。

### 安全与健壮性

* 文件名只接受**文件名**（不接受路径、不接受 `..`），并且必须落在该来源目录内 —— 越权返回 400；
* 整个 `/api/diag/*` 归**管理接口**：未登录一律 401（日志里可能有现场信息，不能裸奔）；
* 打包时如果某个文件正被写（比如宿主正在写今天的日志），**跳过它而不是让整包失败**，日志里有告警。

### 实测

| 项 | 实测 |
|---|---|
| 来源概览 | 6 个来源 / 8 个文件 / 4.5 KB，宿主机日志"2 个（今天 294 B + 昨天 92 B）" |
| 文件列表 | 只看今天 → 只有 `host-20260918.log`；只看昨天 → 只有 `host-20260917.log` |
| SDK 日志 | 无日期的 `Alg.log` 按修改时间落在"昨天"的范围里，不会被算进今天 |
| 查看尾部 | 点 `host-20260918.log` → 显示尾部 5 行，含 `[error] 相机 cam-left 回调超时` 与 `[warn]` 行 |
| 一键打包 | 浏览器触发下载；zip 解开后有 `README.txt`、`host/host-20260918.log`、`host/host-20260917.log`、`sdk/Alg.log`、`spool/events-*.jsonl`、`auth/auth-events-*.jsonl` |
| 范围与来源筛选 | 只勾 `host` → 包里只有 `host/` 与 README；范围选"今天" → 包里**不含**昨天那份日志 |
| 内容正确性 | 从 zip 里读出来的日志内容与源文件一致（含 ERROR 行） |
| 越权与鉴权 | `..\..\config\gateway.ini` → 400；未登录访问诊断接口 → 401 |

回归测试：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-c6-diag.ps1
powershell -ExecutionPolicy Bypass -File .\tools\test-c6-diag.ps1 -KeepRunning   # 留着用浏览器看
```

覆盖：来源概览、按时间范围列文件（含"无日期文件按修改时间"）、看尾部、越权与不存在文件被拒、
单文件下载内容正确、**真下载 zip 并真解压**校验目录结构与 README 与文件内容、
来源筛选与范围筛选、打包未知来源被拒、未登录被拦、前端产物检查，共 **49 项断言**。

## 二十、软触发与补码命令（A4）

采集宿主的"命令通道"：给脚本、上位机和现场运维用。三条命令，**执行完带着退出码退出**（不会留下常驻进程）：

```
DwsEdge.Host.exe --command-status              看当前 provider、支持哪些命令、当前触发模式
DwsEdge.Host.exe --soft-trigger                软触发一次（要求 triggerMode=2）
DwsEdge.Host.exe --soft-trigger --force        跳过触发模式校验（排查用）
DwsEdge.Host.exe --recode --code SF1234567890  人工补码（可加 --time-ms <Unix 毫秒>）
```

现场更省事的是包装脚本，它把退出码翻译成人话：

```powershell
.\tools\host-command.ps1 -Status
.\tools\host-command.ps1 -SoftTrigger            # 硬触发模式下会给出"去改模式"的提示
.\tools\host-command.ps1 -Recode -Code SF1234567890
```

### 退出码（脚本据此判断成败）

| 退出码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 参数错误（缺 `--code`、未知参数…） |
| 2 | 采集宿主启动失败（加密狗 / 相机 / SDK 配置） |
| 3 | 未处理异常 |
| 4 | 命令执行失败（provider 返回非 0，具体看日志） |
| 5 | **触发模式不允许**：当前不是软触发模式（加 `--force` 可跳过） |
| 6 | 当前 provider 不支持这条命令 |

### 两条验收点怎么落的

**① 命令返回码正确并写入日志**：上面每个退出码都是实测出来的；每条命令都会写
`runtime\logs\host-<日期>.log`，记录形如
`[cmd] 收到命令：soft-trigger（--force：跳过触发模式校验）　provider=simulator` →
`[cmd] 软触发成功（返回 0）　退出码 0`，事后能完整还原"谁在什么时候发了什么命令、结果如何"。

**② 触发模式为软触发时生效**：命令执行前会读 `Cfg\LogisticsBase.cfg` 的 `triggerMode`：

| 当前模式 | `--soft-trigger` 的结果 |
|---|---|
| `2` 软触发 | 退出码 **0**，真的产出包裹事件（检测 + 补全共 2 条） |
| `1` 硬触发 / `0` 自由拉流 | 退出码 **5**，提示去跑 `tools\set-trigger-mode.ps1 -Mode soft`；**一条包裹事件都不产生**（命令真没执行） |
| 读不到（没有 SDK 配置） | 记一条 WARN，继续执行 |

### 与交互式测试开关的关系

原来那套交互式开关保留：`--trigger-once` / `--trigger-interval 3000` / 运行时按 Enter 触发、
输入 `c <条码>` 补码。**一次性命令**（本文这四条）是给"发一条命令、拿一个退出码"的场景用的，
两者互不干扰。

### 顺带修掉的两个真 bug

1. **`triggerIntervalMs` 的默认值**：早先写成 `int triggerIntervalMs = 3000;` 然后用 `if (triggerIntervalMs > 0)` 判断
   "用户是否传了 `--trigger-interval`" —— 这个条件永远成立，等于**任何一次启动都开着"每 3 秒自动软触发"**。
   现场无参数启动采集宿主会凭空产生包裹（实测确实如此：日志里每 3 秒一条 `[test] 软触发…返回 0`）。
   现在只有显式传 `--trigger-interval` 才开，实测无参数启动 `--duration 6` **触发 0 次**。
2. **未知参数被静默忽略**：`--not-a-command` 这类（以 `--` 开头、但不认识）原来会被忽略，宿主持续运行，
   现场以为"命令发出去了"其实什么都没做。现在任何未识别的参数都返回**退出码 1** 并提示 `--help`。

### 回归测试

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-a4-command.ps1
```

用 simulator 跑（不需要相机和加密狗），覆盖：状态命令、软触发成功且真出码、
硬触发/自由拉流下被拒且**不产生任何包裹事件**、`--force` 跳过校验、补码成功与缺 `--code`、
未知参数返回 1、帮助返回 0、每条命令都写日志、包装脚本的退出码翻译，
以及"命令执行完就退出（不误入常驻）"，共 **41 项断言**。

## 二十一、配置模板（A8-3）

A8 的需求原文是"**按模板生成**或改写 SDK 配置文件"——"改写"就是前面的一键应用（写 cfg → 启动校验 → 失败回滚），
这一节补的是"按模板生成"。模板装的是**采集侧配置**：相机清单（含方位）+ 触发模式 + 存图策略。

模板是 `runtime\config\templates\<名称>.json`，**拷到同行的设备上就能用**（纯离线，不依赖网络）。
平台侧的配置（下游、规则、监控阈值、班次）不在模板里 —— 它们各自都有保存与备份，
硬塞进一个模板反而容易互相覆盖（刻意的取舍）。

界面上（配置页 → 配置模板）能做的四件事：

| 操作 | 说明 |
|---|---|
| 另存为模板 | 填名称 + 备注 → 把**当前配置**抓成一份模板 |
| 对比 | 列出逐项差异：相机多出/缺少/方位不同、触发模式、存图各字段 |
| 套用 | 勾选要套用的部分（相机清单 / 触发模式 / 存图策略）→ 写回；相机与触发走"一键应用"（含校验与回滚），存图策略自动备份 |
| 导出 / 删除 | 导出 json 给别的设备；删除模板 |

接口：`GET｜POST /api/config/templates`（列表 / 另存）、`GET .../diff?name=`（差异）、
`POST .../apply`（套用）、`POST .../delete`、`GET .../download?name=`（导出）。
名称会校验（空 / 超长 / 重名 / 带路径都拒绝）。

回归测试：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-a8-template.ps1
```

覆盖：另存（内容正确、文件落盘）、列表字段、**一致时 same=true / 改乱后逐项列出差异**、
套用相机+触发（cfg 真的改回去、触发模式恢复）、套用存图策略（回读生效 + 自动备份）、
套用后与模板完全一致、名称校验、套用不存在的模板、一项都不勾、导出内容可用、删除与重复删除，
共 **49 项断言**。

## 二十二、现场一键自检

装机/交付前跑一条命令，把"该看的东西"过一遍，最后给一张"通过/不通过 + 该做什么"的清单：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\self-check.ps1
powershell -ExecutionPolicy Bypass -File .\tools\self-check.ps1 -RuntimeDir D:\dws\runtime -Port 8090
powershell -ExecutionPolicy Bypass -File .\tools\self-check.ps1 -SkipTrigger   # 不动采集侧
```

检查项：运行时目录完整性（配置 / 宿主 / 平台 / 插件）、采集宿主状态（`--command-status`，**不启动 SDK、秒回**）、
加密狗与相机（真机模式下跑 `--verify-config` 真正启动一次 SDK）、软触发（退出码 0 **且 spool 真多出包裹事件**才算过）、
人工补码、平台健康与统计、磁盘水位、相机在线数、下游输出连通。

两条刻意的原则：**能验就验、不能验就明确说跳过**（没接真机时不假装验过加密狗）；
**只读为主**（唯一的"写"是软触发一次和补一次码）。退出码 0 = 没有失败项，1 = 有失败项。

实测（模拟器环境）：完整性、插件、宿主状态、补码四项 PASS；软触发 **FAIL** ——
因为那台设备的 `triggerMode` 不是软触发，脚本直接给出"用 set-trigger-mode.ps1 -Mode soft 改"的处理建议；
加密狗与相机、平台健康标为跳过。这正是自检该有的样子：说清"这台设备现在能不能交付"，而不是笼统报一句"检测通过"。

## 二十三、两个进程的边界

| | 采集宿主（Edge） | 业务平台（Platform） |
|---|---|---|
| 运行时 | .NET Framework 4.8 / x64 | .NET 10 + ASP.NET Core |
| 依赖 | 大华 SDK、原生 DLL、加密狗 | 无厂商依赖 |
| 职责 | 相机、触发、收码、收图、落盘、写 spool | 合并、统计、API、实时推送、前端托管 |
| 崩溃影响 | 采集中断，重启后自愈 | 只影响界面与统计，采集继续 |
| 升级频率 | 低（跟着 SDK 走） | 高（业务、界面天天改） |

通信：V1 用文件 spool（`SpoolTailer` 增量读取，只处理完整行）；V2 换成 gRPC/命名管道时只需替换 `SpoolTailer`，
`SpoolStore` 与平台 API 不动。

## 二十四、代码导读

**采集侧（net48）**

- `DwsEdge.Host/Program.cs`：入口，读 `config\gateway.ini`、扫 `runtime\providers\*.dll` 反射加载插件、启动/停止、命令行参数（`--duration` / `--trigger-once` / `--trigger-interval`）。
- `DwsEdge.Host/HostEventSink.cs`：控制台 + `logs\host-*.log` + `spool\events-*.jsonl`（手写 JSON，图像只写路径）。
- `DwsEdge.Host/TestConsole.cs`：测试开关（启动触发、定时触发、回车手动触发、补码）。
- `DwsEdge.Host/Program.cs`：命令行入口，含 **A4 一次性命令**（`--soft-trigger` / `--recode` / `--command-status`，
  带触发模式校验与退出码语义）。
- `DwsEdge.Providers.Dahua/DahuaDwsProvider.cs`：SDK 生命周期、回调只入队、工作线程落盘、软触发/补码、读 `triggerMode` 给出提示。
- `DwsEdge.Providers.Dahua/CapturedImage.cs`：非托管图像深拷贝与释放；`ImageWriter.cs`：JPEG 直存 / 原始图写 BMP。
- `DwsEdge.Providers.Simulator/SimulatorProvider.cs`：假相机，一次触发生成条码 + BMP + 两条事件。
- `DwsEdge.Core/Config/CameraPlan.cs`：读 cfg（mode/num/triggerMode + 相机声明），启动自检与配置校验共用。
- `DwsEdge.Core/Config/CameraPositions.cs`：相机方位表的读写（界面与采集宿主共用一份实现）。
- `DwsEdge.Core/Config/CameraIdentity.cs`：把 cfg 里的 `ip=/key=/id=` 和 SDK 上报的相机标识对上号。
- `DwsEdge.Core/Rules/BarcodeRule.cs` + `BarcodeFilter.cs`：条码过滤规则的模型与匹配引擎（纯逻辑，不依赖 IO）。
- `DwsEdge.Platform/BarcodeRuleStore.cs`：规则文件的读写、校验、备份与热加载。
- `DwsEdge.Platform/DedupStore.cs`：去重指纹归档（按 traceId 的索引 + WAL + 定期整理 + 保留期）。
- `DwsEdge.Platform/HistoryStore.cs`：历史库（快照 + 按 traceId 收敛的索引）、查询过滤、CSV 导出。
- `DwsEdge.Platform/DownstreamSender.cs` + `DownstreamStore.cs` + `MessageTemplate.cs`：B4/B5 下游输出（客户端/服务端两种模式、模板、重传、连接活性检测、广播）。
- `DwsEdge.Platform/CameraMonitor.cs`：B8 相机状态监控（在线率/掉线记录/心跳/五类告警、事件落盘与恢复、阈值热加载）+ `MonitorWatcher`（后台定时判定并推快照）。
- `DwsEdge.Platform/AuthStore.cs`：B9 账号与鉴权（PBKDF2 密码、失败锁定、会话、服务令牌、访问规则 `Check()`、审计）+ 请求 DTO。
- `DwsEdge.Platform/ConfigStore.cs`：A8 一键应用 + A9 方位映射 + **C5 存图策略读写、INI 键级改写、配置备份与回滚**。
- `DwsEdge.Platform/HistoryStore.cs`：B3 历史库 + **C4 看板聚合（按相机/班次/小时/日期）**；`ShiftStore.cs`：班次配置。
- `DwsEdge.Platform/LogStore.cs`：**C6 日志来源识别、时间范围过滤、尾部查看、单文件下载、zip 打包**。
- `DwsEdge.Platform/TemplateStore.cs`：**A8-3 配置模板**（另存/列表/差异对比/套用/导出，走 ConfigStore 的一键应用路径）。
- `DwsEdge.Host/Program.cs`：A4 一次性命令（`--soft-trigger` / `--recode` / `--command-status`）。

**平台侧（net10）**

- `DwsEdge.Platform/SpoolTailer.cs`：增量读取 spool，按字节偏移记录位置，只处理以换行结尾的完整行。
- `DwsEdge.Platform/SpoolStore.cs`：按 `traceId` 合并两次回调（`updates` 计数），维护最近 N 条、统计、SSE 订阅者、图片按需读取（带目录白名单校验）。
- `DwsEdge.Platform/ConfigStore.cs`：配置页与一键应用（调 `tools\apply-config.ps1`）、相机方位映射的读写。
- `DwsEdge.Platform/wwwroot/index.html`：只剩骨架与文案；样式和逻辑分别来自 `frontend/src/styles.css` 与 `frontend/src/*.ts` 的编译产物。

**前端（TypeScript，见第四节）**

- `frontend/src/main.ts`：入口，装配页签与实时推送。
- `frontend/src/api.ts` + `types.ts`：接口层与 DTO 类型（与后端一一对应）。
- `frontend/src/realtime.ts` / `devices.ts` / `config.ts`：三个页签各自的渲染逻辑。
- `frontend/src/monitor.ts`：B8 监控与告警（告警条、每台相机指标、掉线与告警记录、阈值配置）。
- `frontend/src/auth.ts`：B9 登录遮罩、顶栏用户区、改密、账号管理、策略与审计（401 自动弹回登录）。
- `frontend/src/config.ts`：配置页（一键应用 + **C5 相机清单表格、存图策略、备份回滚**）。
- `frontend/src/stats.ts`：**C4 统计看板**（四个维度、占比条、导出统计 CSV、班次设置）。
- `frontend/src/diag.ts`：**C6 诊断页**（日志来源卡片、按范围列文件、看尾部、一键打包 zip）。
- `frontend/src/sse.ts` / `dom.ts`：实时推送封装与 DOM 小工具。

## 二十五、常见问题

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
| 想知道软触发到底成没成 | 用 `tools\host-command.ps1 -SoftTrigger`：退出码 0 成功、5 是模式不对、4 是命令失败（看 `logs\host-*.log`） |
| 宿主自己一直在出包 | 老版本的默认值坑：以前没传 `--trigger-interval` 也会每 3 秒自动触发。现在只有显式传才开；需要连续过包就明确写 `--trigger-interval 3000` |
| 补码后没看到效果 | 真机上补码走 SDK 的 `ComplementCode`，由 SDK 回调把码补到包裹上；模拟器只记日志（它没有真实包裹可补） |
| 平台没有数据 | 确认 Edge 已产生 `runtime\spool\events-*.jsonl`；平台 `appsettings.json` 的 `Spool:Directory` 默认是 `../spool` |
| `apply-config.ps1` 报"检测到采集宿主正在运行" | 校验要独占 SDK（再起一个实例会和正在跑的抢相机）；加 `-StopHost` 让脚本先停掉，或自己先停 |
| `apply-config.ps1` 退出码 3 | 回滚后仍起不来 → 大概率是设备侧问题（加密狗/相机网段/原生 DLL），不是配置问题 |
| 编译后 `runtime\config\gateway.ini` 没变 | 正常：现场配置不会被编译覆盖（要强制覆盖加 `-ForceConfig`），避免把现场 provider 冲掉 |
| 设备信息页一直是空的 | 采集宿主没上报过相机快照：确认宿主已启动（模拟器 provider 也会上报 cfg 里的清单）；平台启动时会从 spool 回放最近两个事件文件 |
| 界面改了方位，条码方位还是旧的 | 采集宿主只在启动时读方位表；重启宿主（配置页勾"通过后自动重启采集宿主"也可以） |
| 配置页红字"找不到 apply-config.ps1" | 跑一次 `build.ps1`（会把 `tools\*.ps1` 拷到 `runtime\tools`），或手工把 tools 目录放到 runtime 旁边 |
| 相机显示"未发现" | cfg 里 `enable="1"` 但 SDK 没报；查上电、网线、网段，或该相机被别的软件占用 |
| 相机一直报"心跳超时" | 这台相机既不报状态也不出码：查上电、网线、交换机端口；确认窗口内有包裹经过（没包裹经过时心跳只能靠状态事件） |
| 偶尔报"频繁掉线" | 网线接触不良 / 供电不稳 / 网段内有 IP 冲突的典型症状，看 `camera-events-*.jsonl` 里的掉线时间点找规律 |
| 在线率老是 100% 或一直很低 | 在线率窗口可在"配置 → 监控与告警阈值"里改（默认 60 分钟）；刚上线的相机样本不足时显示 `—` |
| 不想被告警刷屏 | 把 `offlineAlertSeconds` 调大（闪断就不报），或把"启用监控与告警"关掉（数据仍会继续统计） |
| 忘记管理员密码 | 用服务令牌救场：`POST /api/auth/users/reset-password`（header `X-Api-Key`，body `{username,password}`）；令牌在 `runtime\config\auth.json` |
| 账号被锁定了 | 默认锁 15 分钟；管理员在"账号与安全"里点"重置密码"会同时解锁，或改 `auth.json` 的 `lockMinutes` |
| 脚本/上位机调管理接口报 401 | 带上服务令牌头 `X-Api-Key: <auth.json 的 serviceKey>`；回归脚本用 `tools\b9-auth-helper.ps1` 自动带 |
| 第三方只想读数据被 401 | 默认读接口是公开的；如果 401，说明有人开了 `protectRead`，关掉或给对方发一个 viewer 账号/服务令牌 |
| 初始密码文件在哪 | `runtime\config\admin-initial-password.txt`（首次运行生成，改过密码后自动删除）；登录后请尽快改密 |
| 想彻底不要鉴权（只在隔离的调试环境） | 把 `auth.json` 的 `enabled` 改成 `false`；**现场不要这么干** |
| 实时页不显示新包裹卡片 | 先看右上角是不是"重连中…"；再确认采集宿主在写 `runtime\spool\events-*.jsonl`。页面不刷新也会更新（SSE） |
| 卡片上没有缩略图（显示"无图"） | 这条包裹确实没落图（比如无码包裹被策略跳过存图），或图片目录被清理过；点卡片看 `/api/images/info` 的返回更清楚 |
| 卡片墙想一次看更多 | 换"表格"视图（能显示 120 行），或把 `realtime.ts` 里的 `MAX_CARDS` 调大后重新构建前端 |
| 出码数看着不动 | 出码数按"去重后的包裹"算，且只在有包裹经过时才涨；看"最近出码"时间比看数字更直观（时间会跟着走） |
| 相机墙上某台一直是"未发现" | cfg 清单里 `enable="1"` 但 SDK 没报：查上电、网线、网段，或该相机被别的软件占用 |
| 在线率显示 `—` | 刚上线、样本还不够（在线率窗口默认 60 分钟）；等一会儿或调小"在线率窗口" |
| 改了存图策略但没生效 | 存图策略是采集宿主读的：要重启采集宿主。界面保存后会明确提示，别只看文件变了 |
| 相机清单里写 `ip=主机名` 报警 | 选了 `ip` 就要求合法 IPv4；要写主机名/序列号，把接入方式改成 `id` 或 `key` |
| 配置改错了想退回去 | "配置 → 配置备份与回滚"里选对应的 `.bak-` 一键还原；回滚前会自动把当前内容再存一份 |
| 备份目录越堆越多 | 备份就是文本文件（几 KB），需要清理时手工删 `config\*.bak-*` 与 `Cfg\*.bak-*` 即可，平台不自动删 |
| 统计页"按班次"出现"未匹配班次" | 班次没覆盖全时段：在统计页下方把班次改成首尾相接（例如 06:00-14:00 / 14:00-22:00 / 22:00-06:00） |
| 统计页数字和历史查询对不上 | 先确认查询范围一样；注意"按日期"与"按班次"口径不同：跨天夜班的凌晨算前一天 |
| 想按自己的班次统计 | 统计页下方"班次设置"里改，保存立即生效；跨天班次直接写 `22:00` → `06:00` 就行 |
| 现场出问题、要回传日志 | 诊断页选好时间范围 → "一键打包下载 zip"，把 zip 发给我们就行（里面带 README 说明） |
| 诊断页看不到 SDK 日志 | SDK 日志在 `runtime\Log\`（不是 `logs\`）；确认采集宿主跑过（SDK 才写日志），或换个时间范围 |
| 打包时少了某个文件 | 该文件正被进程写（比如今天的宿主日志）：等几秒重新打包，或单独"下载"它 |
| 装机前想确认"这台设备能不能交付" | 跑 `tools\self-check.ps1`：会给一张"通过/不通过 + 该做什么"的清单 |
| 想快速把一台设备的采集配置复制到另一台 | 配置页 → 配置模板：另存为模板 → 拷 `config\templates\*.json` → 另一台点"套用" |
| 套用模板会影响平台侧配置吗 | 不会：模板只含相机清单 + 触发模式 + 存图策略，平台侧（下游/规则/阈值/班次）不被动 |

## 二十六、下一步（V1 完整版）

**24 项需求（A1-A9 / B1-B9 / C1-C6）的功能都已经落地**，每一项都有对应的回归脚本（共 16 个，见 `tools\test-*.ps1`）：
采集侧多相机接入/重连/触发/补码/存图/事件/缓冲/配置应用与模板/设备信息，平台侧合并去重、条码规则、历史库、
三种下游输出、图片服务、相机监控告警、账号鉴权，界面侧过包卡片墙、相机状态墙、历史查询、统计看板、配置页、日志诊断。
唯一的例外是 A5 里的"面单抠图"——按你的要求不做。

接着建议按这个顺序做（前三条是**现场交付**要用的，后两条是**产品化**）：

1. **采集侧服务化 + 开机自启 + 看门狗**：现在宿主和平台都是控制台进程，断电重启要人工拉起；
   做成 Windows 服务（或计划任务）后，才能真正满足 A1 的"连续运行 4 小时不掉线"和无人值守场景。
   注意：大华 SDK 在"服务账户无桌面会话"下的表现需要在现场验证一次（USB 加密狗与相机 SDK 多数没问题，但要实测）。
2. **HTTPS / 设备证书**：B9 现在是内网 HTTP 明文，出公网或跨网段需要上 TLS（自签证书 + Kestrel 配置，或前置反向代理）。
3. **配置变更审计**：现在有登录审计（B9）与配置备份（C5），但没有"谁在什么时候把哪个参数从 X 改成 Y"；
   在 C5 的保存路径上补一条审计即可，正好和诊断页（C6）的日志一起导出。
4. **C3 的两个增强**：包裹多图（现在只存第一张路径 + 图片数）、批量粘贴单号对账。
5. **把 `SpoolTailer` 换成 gRPC / 命名管道**：C1 的 300ms 现在靠把轮询调到 100ms 满足；换成事件推送可以再降一个量级，
   也为 ARM 全栈铺路。
6. **告警外发**：把 B8 的告警接到下游报文或钉钉/企业微信机器人，现场不用盯屏。
