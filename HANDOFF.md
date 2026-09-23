# HANDOFF —— 接手入口（换机器 / 换人先读这份）

> 用途：让另一个人、或另一台机器上的 Codex，在 10 分钟内接管这个项目。
> 维护约定：每次收工前更新"现状 / 最近改动 / 待办"。
> 最后更新：2026-09-22

---

## 0. 一句话现状

| 项目 | 内容 |
|---|---|
| 产品 | **DWS 物流解码平台**（六面扫为主，可扩狂扫 / 快手） |
| 架构 | 方案 B：**采集宿主**（net48，唯一接触相机 SDK）+ **业务平台**（net10，API + 浏览器界面），中间用厂商无关的 spool 事件解耦 |
| 版本 | **V1.0.9**（唯一来源 = 仓库根 `VERSION`） |
| 需求进度 | A1–A9 / B1–B9 / C1–C6 **共 24 项全部实现**，19 个回归脚本可自证 |
| 交付形态 | **形态 A**：控制台进程 + 计划任务开机自启 + 可选界面外壳 `DwsEdge.Shell.exe` |
| 最新交付包 | 桌面 `DWS-Edge-Min_V1.0.9_20260921`（runtime / docs / VERSION.txt / checksums.txt） |
| 当前现场 | 宿主 `running`、相机在线（Huaray `BC00033AAK00095`，`ip=100.100.100.11`） |

---

## 1. 接手第一件事（照做，10 分钟）

```powershell
cd C:\Users\Administrator\Desktop\lightcookr\dws-edge-min      # 或 git clone 后的路径

.\build.ps1 -Offline                                           # 编译（离线可用）
.\runtime\tools\test-ui-structure.ps1                          # 前端结构体检（2 秒）
.\tools\test-a4-command.ps1                                    # 采集命令通道回归（41 项）

# 想直接看界面（不需要相机）：把 runtime\config\gateway.ini 的 provider 改成 simulator
.\run.ps1 -Both -TriggerOnce -Duration 20
# 浏览器打开 http://127.0.0.1:8090   （初始账号密码见 runtime\config\admin-initial-password.txt）
```

读文档顺序：**本文件 → `README.md`（架构）→ `docs\操作手册.md`（现场操作）→ `VERSION` + `CHANGELOG.md`**。

---

## 2. 代码地图

| 路径 | 作用 |
|---|---|
| `src\DwsEdge.Core\` | 契约与模型（net48 + net10 双目标）：事件、相机身份匹配、配置解析 |
| `src\DwsEdge.Providers.Dahua\` | 大华 SDK 适配（**唯一**引用厂商 DLL 处）：IP↔Key 对照、启动重试 |
| `src\DwsEdge.Providers.Simulator\` | 仿真相机（不需要相机 / 加密狗，回归测试全靠它） |
| `src\DwsEdge.Host\` | 采集宿主：加载 provider、落图、写 spool、常驻命令通道、启动重试、状态文件 |
| `src\DwsEdge.Platform\` | 业务平台：消费 spool、包裹合并/去重、API、SSE、托管前端 |
| `src\DwsEdge.Shell\` | 界面外壳（net48 WinForms，零依赖）：kiosk 套壳、等平台、看门狗 |
| `frontend\` | 前端 TypeScript（无框架、无打包器），`build.mjs` 产出 `wwwroot\js` |
| `tools\` | 自检、回归、打包、热修、诊断、配置应用等脚本 |
| `runtime\` | 运行时目录（SDK DLL、Cfg、config、platform、tools、docs、images/spool/logs） |
| `docs\` | 操作手册等（构建时会拷进 `runtime\docs`） |

---

## 3. 关键路径与配置（现场）

| 内容 | 路径 | 说明 |
|---|---|---|
| 采集配置 | `runtime\config\gateway.ini` | provider、存图策略、`[storage]` 保留、`[startup]` 启动重试 |
| 相机清单 | `runtime\Cfg\LogisticsBase.cfg` | GB2312；`ImageAcq mode/num` + `<Camera ip/key/id enable>` |
| IP↔Key 对照 | `runtime\config\camera-identity.ini` | 宿主自动维护，清单按 IP 写也能对上 SDK 的"厂商:序列号" |
| 方位映射 | `runtime\config\camera-positions.ini` | 配置页 / 设备信息页维护 |
| 下游对接 | `runtime\config\downstream.json` | 协议（tcp-client / tcp-server / http）、模板、重试、补发 |
| 账号与鉴权 | `runtime\config\users.json` / `auth.json` | 首次启动生成随机初始密码 |
| 历史库 | `runtime\data\parcels-*.jsonl` + `.index.jsonl` | 平台写，别手工改 |
| 事件缓冲 | `runtime\spool\events-*.jsonl` | 宿主写、平台消费；`.consumed` 是消费位点 |
| 日志 | `runtime\logs\`（宿主/平台/shell-start）、`runtime\Log\`（大华 SDK 日志） | 排查首选 |

---

## 4. 常用命令速查

```powershell
# ---- 编译 / 打包 / 核验（开发机）----
.\build.ps1 -Offline                                    # 编译全部并铺到 runtime
.\build.ps1 -Offline -RuntimeDir <某包>\runtime          # 把产物铺进已有包（保留现场配置）
.\tools\make-package.ps1 -KeepCameras                   # 出厂包（保持现场相机清单）
.\runtime\tools\check-package.ps1                       # 包核验：版本/程序集/校验和/与源码一致
.\runtime\tools\test-ui-structure.ps1                   # 前端结构体检

# ---- 热修（不动版本号，只对齐校验和）----
.\runtime\tools\refresh-checksums.ps1 -Package <某包>     # 覆盖文件之后重算 checksums.txt

# ---- 现场运行 ----
.\run.ps1 -Both                                         # 前台跑宿主 + 平台
.\run.ps1 -Background                                   # 隐藏启动（不弹黑窗口）
.\runtime\tools\start-all.ps1  /  stop-all.ps1          # 启停（stop-all -Status 看状态）
.\runtime\tools\self-check.ps1                          # 一键自检（交付前必跑）
.\runtime\tools\host-command.ps1 -Status | -SoftTrigger | -Recode -Code XXX
.\runtime\tools\install-autostart.ps1 [-Shell]          # 开机自启（-Shell = 由界面外壳自启）

# ---- 回归（19 个脚本，全部自证）----
.\tools\test-a4-command.ps1   .\tools\test-a4-channel.ps1  .\tools\test-a8-template.ps1
.\tools\test-b1-dedup.ps1     .\tools\test-b2-rules.ps1    .\tools\test-b3-history.ps1
.\tools\test-b4-downstream.ps1 .\tools\test-b5-tcp-server.ps1 .\tools\test-b6-http.ps1
.\tools\test-b7-images.ps1    .\tools\test-b8-monitor.ps1  .\tools\test-b9-auth.ps1
.\tools\test-c1-cards.ps1     .\tools\test-c2-wall.ps1     .\tools\test-c4-stats.ps1
.\tools\test-c5-config.ps1    .\tools\test-c6-diag.ps1     .\tools\test-startup-retry.ps1
.\tools\test-ui-structure.ps1
```

---

## 5. 现场发现 Bug 的修复流程

**原则：现场只做"取证"和"换文件 + 重启"；编译与回归在开发机做**（现场盒子没有 .NET SDK）。

1. **现场取证（只读）**：`runtime\tools\self-check.ps1`、`/api/host/status`、`stop-all.ps1 -Status`、诊断页一键打包日志；记录**时间点 + 条码/包号 + 期望 vs 实际 + 包版本**。
2. **开发机修复**：定位 → 改代码 → `build.ps1 -Offline` → 跑相关回归 → 全绿。
3. **出热修包**：只含改动文件 + `deploy.ps1`（停→备份→替换→启→自检）+ `rollback.ps1` + 说明。
4. **现场部署**：解压 → `deploy.ps1` → 按原复现步骤验证 → 有问题 `rollback.ps1`。
5. **记录**：CHANGELOG / 交付记录写一行；源码与 runtime 产物分两个提交 push。
6. **版本规则**：**热修不动版本号**，只 `refresh-checksums.ps1`；攒够改动才改 `VERSION` 正式发版。

---

## 6. 已知坑与规避（血泪清单）

| # | 现象 | 原因 | 规避 |
|---|---|---|---|
| 1 | 相机 ping 通但 SDK 报 3000（发现不到） | 上次宿主被**硬杀**（没走完 StopApp），相机停在"流已打开"，不再回答设备发现 | **给相机断电重上电**（拔网线没用）；宿主现在会自动重试等它 |
| 2 | 换新交付包后登不上 | 出厂包不带账号，首次启动**重新生成随机密码** | 看 `runtime\config\admin-initial-password.txt`；要延续账号就拷 `users.json` / `auth.json`（+ `data\` 历史） |
| 3 | 应用配置失败后采集停摆 | `apply-config` 会先停宿主，老逻辑回滚后不拉回来 | 已修：回滚后自动按旧配置重启宿主；界面还有**应用前设备预检**（可选"只写配置"） |
| 4 | 多网卡时 SDK 发现不到相机 | 默认路由在 WLAN 上、相机网口没有默认路由，发现广播可能走错网口 | 关 WiFi / 禁用其它网卡；或调低相机网口跃点数（**待现场最终确认**） |
| 5 | 界面能开但按钮全不响应、登录框不弹 | 前端 JS 初始化崩了（引用了页面上不存在的元素） | 跑 `test-ui-structure.ps1`，缺 id 会直接列出来 |
| 6 | 未登录时输密码，光标被抢回用户名 | 后台轮询拿到 401 → 弹登录框 → focus | 已修：`/api/host` 只保护 command、后台轮询 `silent401`、showMask 只在原本隐藏时聚焦 |
| 7 | 界面显示"宿主已停止"，其实只是设备问题 | 一次性命令（`--verify-config`）也写 `host-status.json`，覆盖了常驻宿主状态 | 已修：只有常驻模式写状态文件；状态文件超 90 秒判为过期 |
| 8 | `start-all.ps1` 说"已有 DWS 进程在跑"却什么都没启动 | 老判断用 `进程名 -like 'DwsEdge*'`，把**界面外壳自己**也算进去了 | 已修：只认 `DwsEdge.Host` / `DwsEdge.Platform`；`test-a4-channel.ps1` 有断言守着 |
| 9 | 一键应用失败，提示"回滚后仍起不来" | 回滚后的旧配置同样指向不可用的相机，两次校验都 3000 | 这是**设备问题**不是回滚失败：看 SDK 返回码（3000 没相机 / 2200 没加密狗 / 3001 被占用） |
| 10 | 前端改了看不到效果 | 浏览器缓存（页面引用带指纹的 `js/main.js?v=...`） | 页面里 **Ctrl+F5**；`build.mjs` 会自动更新指纹 |
| 11 | 交付包与源码对不上 | 手工覆盖过包里的文件 | 跑 `check-package.ps1`；热修后跑 `refresh-checksums.ps1`（不动版本号） |
| 12 | 不要用 Stop-Process / 任务管理器硬杀宿主 | 会导致坑 1（相机会话卡住） | 用 `stop-all.ps1`，或宿主窗口 Ctrl+C（会走 StopApp） |

---

## 7. 待办 / 下一步（按优先级）

**P0（现场稳定性）**
- [ ] 多网卡绑定：确认"SDK 发现走错网卡"的结论（关 WiFi 验证），结论写进装机 SOP
- [ ] `tools\make-hotfix.ps1`：一键生成热修包（diff + deploy + rollback + README）
- [ ] `tools\collect-evidence.ps1`：现场一键打包取证 zip（日志 / 状态 / 版本 / 自检）

**P1（产品化）**
- [x] 界面顶部**系统状态条**（宿主 / 通道 / 相机 / 磁盘 / 下游）—— T1，V1.0.9 热修已完成
- [x] 界面外壳：分组导航（监控 / 配置 / 系统）+ 主题 / 信息密度 —— T0，V1.0.9 热修已完成
- [x] 侧边栏导航 + 形态切换、顶条品牌区 + 导航图标 + 角标 —— T0.5 / T0.6，已完成
- [x] 过包区改版：大图 + 限高滚动列表 + 解码绿框（绿框坐标来自 SDK `AreaList`）—— T8，已完成（**跨前后端**）
- [ ] 现场收尾：宿主 / 平台二进制还没铺（进程占着文件），**重启宿主 + 平台后**把 `DwsEdge.Host.exe`、`DwsEdge.Core.dll`、`platform\DwsEdge.Platform.*` 拷进包，再跑 `refresh-checksums.ps1`（当前 check-package 有 4 处二进制差异）
- [ ] 一个包裹多张图（六面扫每面一张）在大图区翻看：要让平台把 `images[]` 一并返回
- [ ] 界面改造的剩余任务包（T2–T7）见 `docs\前端改造任务清单.md`
- [ ] 包裹异常件（叠件 / 异形 / 翘边 / 类型）接出来：见 `docs\包裹异常检测可行性评估.md`（SDK 字段现成，**不含破损**）
- [ ] 界面文案去术语化（删掉标题里的需求编号 A8-3 / B8 / C4）
- [ ] 下游对接说明书（三种协议 + 模板占位符 + 联调五步）
- [ ] 自包含发布（免装 .NET 10 运行时）

**P2（能力扩展）**
- [ ] 相机停用/启用（软件层 disable，不拔线）；"按相机屏蔽数据"
- [ ] 运行时热插拔相机（先验证 SDK 未文档化的 AddCamera / RemoveCamera）
- [ ] 包裹破损检测等增值服务（与六面扫数据联动）

---

## 8. 换机器 / 换人交接清单

- [ ] 代码：`git pull`（确认与现场包版本一致：`VERSION` 对得上 `VERSION.txt`）
- [ ] 文档：读 `HANDOFF.md` → `README.md` → `docs\操作手册.md`
- [ ] 环境：.NET SDK 10（编译）、.NET 10 运行时（跑平台）、.NET Framework 4.8（系统自带）
- [ ] 交付包：确认桌面上的 `DWS-Edge-Min_V1.0.x_*` 是最新包（`check-package.ps1` 全绿）
- [ ] 账号：现场 `users.json` 是否已有实际账号（别再用初始密码）
- [ ] 变更记录：`CHANGELOG.md` / `交付记录.md` 是否已补最近改动
- [ ] 备份：现场 `config\` + `Cfg\` 拷一份带走
- [ ] （可选）会话迁移：见 `tools\export-codex-context.ps1`

---

## 9. 术语表

| 词 | 含义 |
|---|---|
| 采集宿主 | `DwsEdge.Host.exe`：唯一加载大华 SDK / 加密狗、拿图收码的进程 |
| 业务平台 | `DwsEdge.Platform.exe`：消费事件、合并包裹、出 API 与界面 |
| 界面外壳 | `DwsEdge.Shell.exe`：kiosk 套壳，等平台 → 拉起 → 用 Edge 应用模式开界面 |
| spool | 宿主写给平台的 JSONL 事件缓冲（`runtime\spool\`） |
| traceId | 一个包裹的唯一追踪号（两次回调合并、去重都靠它） |
| 清单 | `Cfg\LogisticsBase.cfg` 里的相机声明 |
| 一键应用 | 写配置 → 启动 SDK 回读校验 → 失败自动回滚（`tools\apply-config.ps1`） |
| 热修 | 只替换改动文件、不动版本号的现场修复 |
