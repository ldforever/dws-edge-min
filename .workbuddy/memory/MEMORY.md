# DWS Edge Min 项目长期要点

> 详细内容见仓库根目录 `HANDOFF.md`（交接入口）→ `README.md`（架构）→ `docs/操作手册.md`（现场操作）。本文件只记跨会话有用的硬约束。

## 架构不变量
- 两进程：**采集宿主** `DwsEdge.Host.exe`（net48/x64，唯一加载大华 SDK 与加密狗）+ **业务平台** `DwsEdge.Platform.exe`（net10，API + SSE + 托管前端）。中间靠 `runtime\spool\events-*.jsonl` 的**厂商无关 JSONL 事件**解耦，V2 换 gRPC 只改 `SpoolTailer`。
- `src\DwsEdge.Core\` 是双目标（net48 + net10）共享契约层，两边都引用，改模型要同时过两个目标。
- 厂商 DLL 只允许出现在 `DwsEdge.Providers.Dahua`；仿真 provider 用于无相机回归。
- 前端 `frontend\`（TypeScript，无框架无打包器）→ `build.mjs` 产出 `wwwroot\js`，产物提交进仓库，现场不需要 node。

## 硬约束（离线交付环境）
- 拿不到 NuGet 包源 → 历史库、去重指纹、缩略图都用**手写文件/纯 C# 算术**实现（ HistoryStore / DedupStore / ThumbnailService），不要提议引入 SQLite / ImageSharp / System.Drawing.Common。
- 编译用 `build.ps1 -Offline`；版本号唯一来源是根目录 `VERSION`（内容形如 `V1.0.9`，程序集版本 = 去掉 V）。
- 交付前：`runtime\tools\check-package.ps1` 全绿才发；热修不动版本号，只跑 `refresh-checksums.ps1`。

## 现场铁律
- 绝不能 `Stop-Process` 硬杀采集宿主（会导致相机会话卡死，表现为 SDK 报 3000），必须用 `stop-all.ps1` 或 Ctrl+C。
- 改相机清单 / 触发模式 / 存图策略 → 必须**重启采集宿主**；改下游 / 规则 / 阈值 / 班次 → 平台热加载，不用重启。
- 19 个回归脚本在 `tools\test-*.ps1`，都会自己复制一份 runtime 到 `work\` 下跑，不污染现场 runtime。

## 关键配置位置
| 内容 | 路径 |
|---|---|
| 采集侧总配置 | `runtime\config\gateway.ini`（provider、存图、spool 保留、启动重试） |
| 相机清单（GB2312，勿改编码） | `runtime\Cfg\LogisticsBase.cfg` |
| IP↔序列号对照（宿主自动维护） | `runtime\config\camera-identity.ini` |
| 方位映射 | `runtime\config\camera-positions.ini`（改后要重启宿主） |
| 下游 / 规则 / 监控 / 班次 / 账号 | `runtime\config\downstream.json` `barcode-rules.json` `monitor.json` `shifts.json` `users.json` `auth.json` |
