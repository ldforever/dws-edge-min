using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using DwsEdge.Core.Rules;
using DwsEdge.Core.Ipc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DwsEdge.Platform
{
    /// <summary>
    /// 业务平台（方案 B 的 Platform 进程，V1 骨架）。
    ///
    /// 现在做三件事：
    ///   1. 消费采集宿主的 spool 事件，把两次回调合并成一个包裹记录；
    ///   2. 提供 API：健康检查、统计、最新包裹、相机状态、图片按需读取、SSE 实时推送；
    ///   3. 托管前端页面（wwwroot）。
    ///
    /// 下一步（V1 完整版）要补：SQLite 持久化、条码过滤规则、下游 TCP/HTTP 输出、配置管理。
    /// </summary>
    public static class Program
    {
        public static void Main(string[] args)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

            // 只保留控制台日志：默认还会挂 Windows 事件日志提供程序，
            // 在受限账户或服务账户下会因无写权限直接抛出启动异常。
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            });

            builder.Services.AddSingleton<SpoolStore>();
            builder.Services.AddSingleton<HistoryStore>();
            builder.Services.AddSingleton<DedupStore>();
            builder.Services.AddSingleton<BarcodeRuleStore>();
            builder.Services.AddSingleton<DownstreamStore>();
            builder.Services.AddSingleton<ConfigStore>();
            builder.Services.AddSingleton<DownstreamSender>();
            // B8：相机状态监控（在线率 / 掉线记录 / 心跳 / 告警）
            builder.Services.AddSingleton<CameraMonitor>();
            builder.Services.AddHostedService<MonitorWatcher>();
            // B9：账号与接口鉴权（策略 + 账号 + 会话 + 审计）
            builder.Services.AddSingleton<AuthStore>();
            // C4：班次配置（统计看板的"班次维度"）
            builder.Services.AddSingleton<ShiftStore>();
            // C6：日志查看与导出（采集/SDK/spool/审计）
            builder.Services.AddSingleton<LogStore>();
            // A8-3：配置模板（相机清单 + 触发模式 + 存图策略的另存/对比/套用）
            builder.Services.AddSingleton<TemplateStore>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<DownstreamSender>());
            builder.Services.AddHostedService<SpoolTailer>();
            builder.Services.AddHostedService<StorageProbe>();

            WebApplication app = builder.Build();

            app.UseDefaultFiles();

            // 静态文件与缓存：
            //   * index.html 每次都要回源校验（no-cache）—— 前端升级后如果浏览器继续用旧的
            //     index.html，里面那个"新版本号"根本到不了客户端，版本号方案就等于白做；
            //   * js/css 这里不加缓存头，失效靠构建时写进引用的 ?v=<内容指纹>
            //     （引用一变就是新 URL，浏览器必然重新下载）。
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = ctx =>
                {
                    if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Context.Response.Headers["Cache-Control"] = "no-cache";
                    }
                }
            });

            // ------------------------------------------------------------------
            // B9 接口鉴权（中间件放在所有路由前面）
            //
            // 规则（细节见 AuthStore.Check 的注释）：
            //   * 页面与静态资源永远放行 —— 否则连登录框都打不开；
            //   * /api/health、/api/auth/login、/api/auth/status 永远放行（存活检查 + 前端判断登录态）；
            //   * 管理接口（配置/规则/下游/监控阈值/账号）必须登录，未登录 401、角色不够 403；
            //   * 读接口是否也要登录由 config\auth.json 的 protectRead 决定（默认不拦，现场大屏与第三方读数据不受影响）。
            // ------------------------------------------------------------------
            app.Use(async (context, next) =>
            {
                AuthStore auth = context.RequestServices.GetRequiredService<AuthStore>();
                AccessCheck check = auth.Check(context.Request);
                if (string.Equals(check.kind, "allowed", StringComparison.Ordinal))
                {
                    context.Items[AuthStore.SessionItemKey] = check.session;
                    await next();
                    return;
                }

                context.Response.StatusCode = string.Equals(check.kind, "forbidden", StringComparison.Ordinal) ? 403 : 401;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    error = check.error,
                    code = check.kind,
                    loginUrl = "/api/auth/login"
                }), context.RequestAborted);
            });

            app.MapGet("/api/health", (SpoolStore store) => Results.Json(store.Health()));
            app.MapGet("/api/stats", (SpoolStore store) => Results.Json(store.Stats()));
            app.MapGet("/api/parcels", (SpoolStore store, int? limit) =>
            {
                int take = limit.HasValue ? Math.Clamp(limit.Value, 1, 500) : 50;
                return Results.Json(store.LatestParcels(take));
            });
            app.MapGet("/api/cameras", (SpoolStore store) => Results.Json(store.Cameras()));

            // C2：相机状态墙的"计数"部分（出码数 / 掉线次数 / 方位）—— 轻量，界面首屏与周期刷新都用它
            app.MapGet("/api/cameras/counters", (SpoolStore store) => Results.Json(store.CameraCounters()));

            // B1：幂等下发 —— 下游从这里取"待下发"，处理完回报 ack（同一个 traceId 只会出现一次）
            app.MapGet("/api/dispatch/pending", (SpoolStore store, int? limit) =>
            {
                int take = limit.HasValue ? Math.Clamp(limit.Value, 1, 500) : 100;
                return Results.Json(store.PendingDispatch(take));
            });
            app.MapPost("/api/dispatch/ack", (SpoolStore store, DispatchAckRequest request) =>
                store.AckDispatch(request?.traceId, request?.success ?? false, request?.error));

            // B2：条码过滤规则 —— 读取 / 保存（热加载） / 规则测试 / 最近被丢掉的码
            app.MapGet("/api/rules", (BarcodeRuleStore rules) =>
            {
                BarcodeRuleSet set = rules.Current;
                return Results.Json(new
                {
                    file = rules.FilePath,
                    exists = rules.FileExists,
                    defaultAction = set.defaultAction,
                    ignoreCase = set.ignoreCase,
                    ruleCount = set.rules != null ? set.rules.Count : 0,
                    enabledCount = set.EnabledByPriority().Count,
                    rules = set.rules ?? new System.Collections.Generic.List<BarcodeRule>()
                });
            });

            app.MapPost("/api/rules", (BarcodeRuleStore rules, BarcodeRuleSet request) =>
            {
                try
                {
                    string backup = rules.Save(request);
                    BarcodeRuleSet saved = rules.Current;
                    return Results.Json(new
                    {
                        ok = true,
                        file = rules.FilePath,
                        backup,
                        enabledCount = saved.EnabledByPriority().Count,
                        defaultAction = saved.defaultAction,
                        note = "已立即生效（平台每秒检查一次规则文件，不需要重启）"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/rules/test", (BarcodeRuleStore rules, BarcodeRuleTestRequest request) =>
            {
                if (request == null || request.codes == null || request.codes.Count == 0)
                {
                    return Results.BadRequest(new { error = "codes 不能为空" });
                }

                BarcodeRuleSet set = request.ruleset ?? rules.Current;
                System.Collections.Generic.List<string> problems = rules.Validate(set);
                BarcodeFilter filter = rules.CreateFilter(set);
                BarcodeFilterReport report = filter.Decide(request.codes);

                return Results.Json(new
                {
                    usingDraft = request.ruleset != null,
                    defaultAction = set.defaultAction,
                    enabledCount = set.EnabledByPriority().Count,
                    kept = report.kept,
                    dropped = report.dropped,
                    decisions = report.decisions,
                    problems
                });
            });

            app.MapGet("/api/rules/filtered", (SpoolStore store, int? limit) =>
            {
                int take = limit.HasValue ? Math.Clamp(limit.Value, 1, 500) : 100;
                return Results.Json(store.RecentFilteredCodes(take));
            });

            // B1：去重指纹归档的状态与手动整理（按 traceId 归档，定期合并 WAL、清理过期）
            app.MapGet("/api/dedup", (DedupStore dedup) => Results.Json(dedup.Stats()));
            app.MapPost("/api/dedup/compact", (DedupStore dedup) =>
            {
                dedup.Compact();
                return Results.Json(dedup.Stats());
            });

            // B4：下游 TCP 输出（配置 + 状态 + 测试连接 + 模板预览 + 下发日志）
            app.MapGet("/api/downstream", (DownstreamStore config, DownstreamSender sender) =>
            {
                DownstreamOptions options = config.Current;
                return Results.Json(new
                {
                    file = config.FilePath,
                    config = options,
                    stats = sender.Stats(),
                    templateFields = MessageTemplate.Fields
                });
            });

            app.MapPost("/api/downstream", (DownstreamStore config, DownstreamSender sender, DownstreamOptions request) =>
            {
                try
                {
                    string backup = config.Save(request);
                    return Results.Json(new
                    {
                        ok = true,
                        file = config.FilePath,
                        backup,
                        stats = sender.Stats(),
                        note = "已立即生效（发送服务会按新配置重连）"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/downstream/test", (DownstreamStore config, DownstreamSender sender, DownstreamOptions request) =>
            {
                DownstreamOptions options = request ?? config.Current;
                return Results.Json(sender.TestConnection(options));
            });

            // 模板预览：用最近一条包裹渲染，返回报文（把不可见字符转义出来方便看）
            app.MapPost("/api/downstream/preview", (SpoolStore store, DownstreamPreviewRequest request) =>
            {
                string template = request != null ? request.template : null;
                if (string.IsNullOrEmpty(template))
                {
                    template = "{code}|{time}|{camera}|{weight}|{volume}|{traceId}\\r\\n";
                }

                ParcelRecord sample = store.LatestParcels(1).Count > 0 ? store.LatestParcels(1)[0] : null;
                string rendered = MessageTemplate.Render(template, sample);
                List<string> problems = MessageTemplate.Validate(template);

                return Results.Json(new
                {
                    usingSample = sample != null,
                    sample = sample == null ? null : new { sample.traceId, sample.time, codes = sample.codes },
                    rendered = rendered.Replace("\r", "\\r").Replace("\n", "\\n"),
                    bytes = System.Text.Encoding.UTF8.GetByteCount(rendered),
                    problems
                });
            });

            app.MapGet("/api/downstream/log", (DownstreamSender sender, int? limit) =>
            {
                int take = limit.HasValue ? Math.Clamp(limit.Value, 1, 200) : 50;
                return Results.Json(sender.RecentLog(take));
            });

            // A9：设备信息（相机清单 + 方位 + 型号/序列号 + 在线状态）
            app.MapGet("/api/devices", (SpoolStore store, ConfigStore cfg) =>
                Results.Json(store.DeviceList(cfg.PositionsPath)));

            // A9：相机方位映射的读取与保存（界面上改"哪台相机装在哪个面"）
            app.MapGet("/api/camera-positions", (ConfigStore cfg) => Results.Json(cfg.ReadPositions()));
            app.MapPost("/api/camera-positions", (ConfigStore cfg, CameraPositionsRequest request) =>
                cfg.SavePositions(request));

            // A8：一键应用配置（写配置 → 重启 SDK 校验 → 失败自动回滚）
            app.MapGet("/api/config", (ConfigStore cfg) => Results.Json(cfg.Read()));
            app.MapPost("/api/config/apply", (ConfigStore cfg, ConfigApplyRequest request) =>
                cfg.Apply(request));

            // A4：给【正在跑的】采集宿主发命令（常驻命名管道通道）
            //
            // 为什么要这条通道：以前"软触发一次"只能再起一个 DwsEdge.Host.exe 去执行命令，
            // 那个进程会自己开一次 SDK —— 相机被正在跑的宿主占着，结果是 3001（相机被占用），
            // 所以必须先停宿主。现在宿主运行中就能触发/补码。
            app.MapGet("/api/host/channel", (ConfigStore cfg) =>
            {
                HostCommandResult probe = HostCommandChannel.Send(cfg.RuntimeRoot, "status", 2000);
                return Results.Json(new
                {
                    available = probe.ChannelAvailable,
                    pipeName = HostCommandChannel.PipeNameFor(cfg.RuntimeRoot),
                    exitCode = probe.ExitCode,
                    message = probe.Message,
                    runtimeRoot = cfg.RuntimeRoot
                });
            });

            app.MapPost("/api/host/command", (ConfigStore cfg, HostCommandRequest request) =>
            {
                string command = request != null && request.command != null
                    ? request.command.Trim().ToLowerInvariant()
                    : string.Empty;

                if (command != "soft-trigger" && command != "recode" && command != "status")
                {
                    return Results.BadRequest(new
                    {
                        error = "命令只支持 soft-trigger / recode / status（收到：" + command + "）"
                    });
                }
                if (command == "recode" && string.IsNullOrWhiteSpace(request.code))
                {
                    return Results.BadRequest(new { error = "补码要带条码：{\"command\":\"recode\",\"code\":\"SF123\"}" });
                }

                string line = command;
                if (command == "recode")
                {
                    line = "recode " + request.code.Trim() + (request.timeMs > 0 ? " " + request.timeMs : string.Empty);
                }
                else if (request.force)
                {
                    line = command + " --force";
                }

                HostCommandResult result = HostCommandChannel.Send(cfg.RuntimeRoot, line, HostCommandChannel.DefaultTimeoutMs);
                return Results.Json(new
                {
                    available = result.ChannelAvailable,
                    ok = result.Ok,
                    exitCode = result.ExitCode,
                    request = line,
                    message = result.Message
                });
            });

            // A8-3：配置模板 —— 另存当前配置 / 列表 / 差异对比 / 套用 / 删除 / 导出
            app.MapGet("/api/config/templates", (TemplateStore templates) => Results.Json(new
            {
                directory = templates.DirectoryPath,
                templates = templates.List(),
                note = "模板装的是采集侧配置：相机清单 + 触发模式 + 存图策略；拷 json 到别的设备即可套用"
            }));

            app.MapPost("/api/config/templates", (TemplateStore templates, TemplateSaveRequest request) =>
            {
                try
                {
                    return Results.Json(templates.Save(request != null ? request.name : null,
                        request != null ? request.note : null));
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapGet("/api/config/templates/diff", (TemplateStore templates, string name) =>
            {
                try
                {
                    return Results.Json(templates.Diff(name));
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/config/templates/apply", (TemplateStore templates, TemplateApplyRequest request) =>
            {
                try
                {
                    return Results.Json(templates.Apply(request));
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/config/templates/delete", (TemplateStore templates, TemplateSaveRequest request) =>
            {
                try
                {
                    templates.Delete(request != null ? request.name : null);
                    return Results.Json(new { ok = true, note = "模板已删除" });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapGet("/api/config/templates/download", (TemplateStore templates, string name) =>
                templates.Download(name));

            // C5：配置页（简版）—— 存图策略图形化 + 配置备份/回滚
            app.MapGet("/api/config/storage", (ConfigStore cfg) => Results.Json(cfg.ReadStorage()));
            app.MapPost("/api/config/storage", (ConfigStore cfg, ConfigStore.StorageOptions request) =>
            {
                try
                {
                    return Results.Json(cfg.SaveStorage(request));
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            // 备份列表与回滚（rules / downstream / monitor / auth / gateway.ini / Cfg 的 .bak-* 都在这）
            app.MapGet("/api/config/backups", (ConfigStore cfg) => Results.Json(cfg.ReadBackups()));
            app.MapPost("/api/config/rollback", (ConfigStore cfg, ConfigStore.RollbackRequest request) =>
            {
                try
                {
                    return Results.Json(cfg.Rollback(request != null ? request.file : null));
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            // B3：历史查询（条码 / 相机 / 无码 / 下发状态 / 图片），返回总数与耗时便于自证性能
            app.MapGet("/api/history", (SpoolStore store, string from, string to, string code, string deviceId,
                bool? noread, string dispatchState, bool? hasImage, int? limit, int? offset) =>
            {
                HistoryQuery query = BuildHistoryQuery(from, to, code, deviceId, noread, dispatchState, hasImage, limit, offset);
                return Results.Json(store.QueryHistory(query));
            });

            // C4：统计看板（简版）—— 总包数 / 读码率 / 无码率，按相机、班次、小时、日期四个维度
            app.MapGet("/api/stats/board", (HistoryStore history, ShiftStore shifts,
                string from, string to, string dimension, string deviceId) =>
            {
                HistoryQuery query = BuildHistoryQuery(from, to, null, deviceId, null, null, null, 0, 0);
                return Results.Json(history.BuildBoard(query, dimension, shifts.Plan));
            });

            // C4：班次配置的读写（改完立即生效，看板按新班次重新统计）
            app.MapGet("/api/stats/shifts", (ShiftStore shifts) => Results.Json(shifts.Read()));
            app.MapPost("/api/stats/shifts", (ShiftStore shifts, ShiftPlan request) =>
            {
                try
                {
                    string backup = shifts.Save(request);
                    return Results.Json(new { ok = true, file = shifts.FilePath, backup, plan = shifts.Read() });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            // B3：导出 CSV（流式写出，字段覆盖条码/时间/相机/图片路径/无码/下发状态）
            app.MapGet("/api/history/export", async (HttpContext context, HistoryStore history,
                string from, string to, string code, string deviceId,
                bool? noread, string dispatchState, bool? hasImage) =>
            {
                HistoryQuery query = BuildHistoryQuery(from, to, code, deviceId, noread, dispatchState, hasImage, 0, 0);

                string fileName = "dws-history-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv";
                context.Response.ContentType = "text/csv; charset=utf-8";
                context.Response.Headers["Content-Disposition"] = "attachment; filename=\"" + fileName + "\"";

                // UTF-8 BOM：Excel 直接双击打开不会乱码。
                // 用异步写：Kestrel 默认 AllowSynchronousIO=false，同步写会 500。
                StreamWriter writer = new StreamWriter(context.Response.Body, new UTF8Encoding(true));
                int rows = await history.ExportCsvAsync(query, writer);
                await writer.FlushAsync();

                ILogger<HistoryStore> log = context.RequestServices.GetRequiredService<ILogger<HistoryStore>>();
                log.LogInformation("历史导出完成：{0} 行（{1} ~ {2}）", rows,
                    query.From.ToString("yyyy-MM-dd"), query.To.ToString("yyyy-MM-dd"));
            });

            app.MapGet("/api/images", (SpoolStore store, string path) => store.OpenImage(path));

            // B7：缩略图（BMP 真缩小并缓存；JPEG 回退原图）+ 图片元信息
            app.MapGet("/api/images/thumb", (SpoolStore store, string path, int? w) =>
                store.OpenThumbnail(path, w.HasValue ? Math.Clamp(w.Value, 32, 1600) : 320));
            app.MapGet("/api/images/info", (SpoolStore store, string path) => Results.Json(store.ImageInfo(path)));

            // B8：相机状态监控与告警 —— 在线率 / 掉线记录 / 心跳 / 告警 / 阈值配置
            app.MapGet("/api/monitor/summary", (CameraMonitor monitor) => Results.Json(monitor.Summary()));

            app.MapGet("/api/monitor/cameras", (CameraMonitor monitor) => Results.Json(monitor.Statuses()));

            // 掉线记录：谁在什么时候掉的、掉了多久（掉线/上线/恢复/告警都在这条流里）
            app.MapGet("/api/monitor/events", (CameraMonitor monitor, int? limit, string camera) =>
            {
                int take = limit.HasValue ? Math.Clamp(limit.Value, 1, 500) : 100;
                return Results.Json(monitor.RecentEvents(take, camera));
            });

            app.MapGet("/api/monitor/alerts", (CameraMonitor monitor, int? limit, bool? activeOnly) =>
            {
                int take = limit.HasValue ? Math.Clamp(limit.Value, 1, 500) : 100;
                return Results.Json(monitor.Alerts(take, activeOnly.GetValueOrDefault(true)));
            });

            app.MapGet("/api/monitor/config", (CameraMonitor monitor) => Results.Json(new
            {
                file = monitor.ConfigFilePath,
                dataDirectory = monitor.DataDirectory,
                options = monitor.Options,
                note = "改文件或 POST 本接口都立即生效（平台每秒检查一次配置文件，不用重启）"
            }));

            app.MapPost("/api/monitor/config", (CameraMonitor monitor, MonitorOptions request) =>
            {
                try
                {
                    string backup = monitor.SaveOptions(request);
                    return Results.Json(new
                    {
                        ok = true,
                        file = monitor.ConfigFilePath,
                        backup,
                        options = monitor.Options,
                        note = "已立即生效；旧配置已备份"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapGet("/api/stream", (HttpContext context, SpoolStore store, CancellationToken token) =>
                store.StreamAsync(context, token));

            // C6：日志查看与导出（采集宿主 / 平台 / SDK / spool / 审计）
            app.MapGet("/api/diag/sources", (LogStore diag) => Results.Json(diag.Overview()));

            app.MapGet("/api/diag/files", (LogStore diag, string source, string from, string to) =>
            {
                try
                {
                    return Results.Json(diag.Files(source, ParseDay(from, DateTime.Today), ParseDay(to, DateTime.Today)));
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapGet("/api/diag/tail", (LogStore diag, string source, string file, int? lines) =>
            {
                try
                {
                    return Results.Json(diag.Tail(source, file, lines ?? 200));
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapGet("/api/diag/download", (LogStore diag, string source, string file) =>
            {
                try
                {
                    return diag.Download(source, file);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            // 一键打包：把选中的来源按时间范围压成一个 zip（流式写，离线可解）
            app.MapGet("/api/diag/bundle", async (HttpContext context, LogStore diag, string from, string to, string sources) =>
            {
                try
                {
                    await diag.BundleAsync(context, ParseDay(from, DateTime.Today), ParseDay(to, DateTime.Today), sources);
                }
                catch (Exception ex)
                {
                    if (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.ContentType = "application/json; charset=utf-8";
                        await context.Response.WriteAsync("{\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}");
                    }
                }
            });

            // ================================================================
            // B9 账号与鉴权
            // ================================================================

            // 登录：成功后同时写 HttpOnly Cookie（浏览器）并返回 token（程序/脚本用 Bearer）
            app.MapPost("/api/auth/login", (AuthStore auth, HttpContext context, LoginRequest request) =>
            {
                if (request == null)
                {
                    return Results.BadRequest(new { error = "请求体不能为空" });
                }

                LoginResult result = auth.Login(request.username, request.password, AuthStore.ClientIp(context.Request));
                if (!result.ok)
                {
                    int status = string.Equals(result.code, "locked", StringComparison.Ordinal) ? 423 : 401;
                    return Results.Json(new
                    {
                        error = result.error,
                        code = result.code,
                        remainingAttempts = result.remainingAttempts,
                        lockedSeconds = result.lockedSeconds
                    }, statusCode: status);
                }

                context.Response.Cookies.Append(AuthStore.CookieName, result.token, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                    MaxAge = TimeSpan.FromMinutes(result.sessionMinutes)
                });

                return Results.Json(new
                {
                    ok = true,
                    token = result.token,
                    username = result.user.username,
                    role = result.user.role,
                    mustChangePassword = result.user.mustChangePassword,
                    expiresAtMs = result.expiresAtMs,
                    sessionMinutes = result.sessionMinutes,
                    note = "浏览器用 Cookie、程序用 Authorization: Bearer <token> 或 X-Api-Key（服务令牌）"
                });
            });

            app.MapPost("/api/auth/logout", (AuthStore auth, HttpContext context) =>
            {
                AuthSession session = context.Items[AuthStore.SessionItemKey] as AuthSession;
                bool ok = auth.Logout(session != null ? session.token : null);
                context.Response.Cookies.Delete(AuthStore.CookieName);
                return Results.Json(new { ok, note = ok ? "已退出登录" : "当前会话已失效" });
            });

            // 前端每次打开页面问一次：要不要登录、我是谁、初始密码还没改吗
            app.MapGet("/api/auth/status", (AuthStore auth, HttpContext context) =>
            {
                AuthOptions options = auth.Options;
                AuthSession session = auth.Resolve(context.Request, options);
                return Results.Json(new
                {
                    enabled = options.enabled,
                    protectRead = options.protectRead,
                    allowServiceKey = options.allowServiceKey,
                    authenticated = session != null,
                    username = session == null ? null : session.username,
                    role = session == null ? null : session.role,
                    viaServiceKey = session != null && string.IsNullOrEmpty(session.token),
                    activeSessions = auth.ActiveSessionCount,
                    initialPasswordPending = File.Exists(auth.InitialPasswordPath),
                    initialPasswordFile = auth.InitialPasswordPath
                });
            });

            app.MapGet("/api/auth/me", (AuthStore auth, HttpContext context) =>
            {
                AuthSession session = context.Items[AuthStore.SessionItemKey] as AuthSession;
                if (session == null)
                {
                    return Results.Json(new { error = "未登录" }, statusCode: 401);
                }
                return Results.Json(new
                {
                    username = session.username,
                    role = session.role,
                    viaServiceKey = string.IsNullOrEmpty(session.token),
                    expiresAtMs = session.expiresAtMs,
                    activeSessions = auth.ActiveSessionCount
                });
            });

            // 改密码：不填 username 就是改自己的（要原密码）；填了别人 = 管理员重置（需 admin）
            app.MapPost("/api/auth/password", (AuthStore auth, HttpContext context, ChangePasswordRequest request) =>
            {
                AuthSession session = context.Items[AuthStore.SessionItemKey] as AuthSession;
                if (session == null)
                {
                    return Results.Json(new { error = "未登录" }, statusCode: 401);
                }
                if (request == null || string.IsNullOrEmpty(request.newPassword))
                {
                    return Results.BadRequest(new { error = "newPassword 不能为空" });
                }

                string target = string.IsNullOrWhiteSpace(request.username) ? session.username : request.username.Trim();
                bool byAdmin = !string.Equals(target, session.username, StringComparison.OrdinalIgnoreCase);
                if (byAdmin && !AuthStore.IsAdmin(session.role))
                {
                    return Results.Json(new { error = "只有管理员能重置别人的密码" }, statusCode: 403);
                }

                try
                {
                    auth.ChangePassword(target, request.oldPassword, request.newPassword, byAdmin,
                        session.username, session.token);
                    return Results.Json(new { ok = true, username = target, byAdmin, note = byAdmin ? "已重置该账号密码" : "密码已修改" });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapGet("/api/auth/users", (AuthStore auth) => Results.Json(new
            {
                file = auth.UsersPath,
                maxFailures = auth.Options.maxFailures,
                lockMinutes = auth.Options.lockMinutes,
                activeSessions = auth.ActiveSessionCount,
                users = auth.Users()
            }));

            app.MapPost("/api/auth/users", (AuthStore auth, HttpContext context, AuthUserRequest request) =>
            {
                AuthSession session = context.Items[AuthStore.SessionItemKey] as AuthSession;
                try
                {
                    AuthUserView user = auth.AddUser(request != null ? request.username : null,
                        request != null ? request.password : null, request != null ? request.role : null,
                        request != null ? request.note : null, session != null ? session.username : null);
                    return Results.Json(new { ok = true, user, note = "新账号首次登录后请修改密码" });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/auth/users/update", (AuthStore auth, HttpContext context, AuthUserRequest request) =>
            {
                AuthSession session = context.Items[AuthStore.SessionItemKey] as AuthSession;
                try
                {
                    AuthUserView user = auth.UpdateUser(request != null ? request.username : null,
                        request != null ? request.role : null, request != null ? request.enabled : null,
                        request != null ? request.note : null, session != null ? session.username : null);
                    return Results.Json(new { ok = true, user });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/auth/users/delete", (AuthStore auth, HttpContext context, AuthUserRequest request) =>
            {
                AuthSession session = context.Items[AuthStore.SessionItemKey] as AuthSession;
                try
                {
                    auth.DeleteUser(request != null ? request.username : null, session != null ? session.username : null);
                    return Results.Json(new { ok = true, note = "账号已删除" });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/auth/users/reset-password", (AuthStore auth, HttpContext context, AuthUserRequest request) =>
            {
                AuthSession session = context.Items[AuthStore.SessionItemKey] as AuthSession;
                try
                {
                    auth.ChangePassword(request != null ? request.username : null, null,
                        request != null ? request.password : null, true,
                        session != null ? session.username : null, null);
                    return Results.Json(new { ok = true, username = request != null ? request.username : null, note = "密码已重置" });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapGet("/api/auth/config", (AuthStore auth) => Results.Json(new
            {
                file = auth.ConfigPath,
                usersFile = auth.UsersPath,
                initialPasswordFile = auth.InitialPasswordPath,
                dataDirectory = auth.DataDirectory,
                options = auth.Options,
                activeSessions = auth.ActiveSessionCount,
                note = "保存后立即生效；服务令牌可轮换（轮换后旧的立刻失效）"
            }));

            app.MapPost("/api/auth/config", (AuthStore auth, AuthOptions request) =>
            {
                try
                {
                    string backup = auth.SaveOptions(request);
                    return Results.Json(new { ok = true, file = auth.ConfigPath, backup, options = auth.Options });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/auth/service-key", (AuthStore auth) =>
                Results.Json(new { ok = true, serviceKey = auth.RotateServiceKey(), note = "旧令牌已失效，记得到脚本/上位机里更新" }));

            app.MapGet("/api/auth/events", (AuthStore auth, int? limit, string kind) =>
            {
                int take = limit.HasValue ? Math.Clamp(limit.Value, 1, 500) : 100;
                return Results.Json(auth.Events(take, kind));
            });

            ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Platform");

            // B8：告警产生/恢复时推给界面（顶部告警条）
            SpoolStore store = app.Services.GetRequiredService<SpoolStore>();
            CameraMonitor monitor = app.Services.GetRequiredService<CameraMonitor>();
            monitor.OnAlertChanged = alert => store.PublishAlert(alert);

            logger.LogInformation("DwsEdge.Platform 启动完成；图片根目录：{0}", app.Services.GetRequiredService<SpoolStore>().ImagesRoot);

            app.Run();
        }

        private static DateTime ParseDay(string value, DateTime fallback)
        {
            DateTime parsed;
            if (string.IsNullOrEmpty(value))
            {
                return fallback.Date;
            }

            // 先按接口约定解析 yyyyMMdd（界面和脚本都传这个），再退回通用解析。
            // 之前只写 DateTime.TryParse，传 "20260916" 时在当前区域设置下会解析失败并静默用默认值，
            // 导致"查询某一天"实际查成了"昨天到今天"，数字全对不上。
            if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out parsed))
            {
                return parsed.Date;
            }
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out parsed))
            {
                return parsed.Date;
            }
            return fallback.Date;
        }

        /// <summary>把查询串参数拼成 HistoryQuery（日期反了就自动交换）。</summary>
        private static HistoryQuery BuildHistoryQuery(string from, string to, string code, string deviceId,
            bool? noread, string dispatchState, bool? hasImage, int? limit, int? offset)
        {
            HistoryQuery query = new HistoryQuery();
            query.From = ParseDay(from, DateTime.Today.AddDays(-1));
            query.To = ParseDay(to, DateTime.Today);
            if (query.To < query.From)
            {
                DateTime swap = query.From;
                query.From = query.To;
                query.To = swap;
            }

            query.Code = code;
            query.DeviceId = deviceId;
            query.NoRead = noread;
            query.DispatchState = dispatchState;
            query.HasImage = hasImage;
            query.Limit = limit.HasValue ? Math.Clamp(limit.Value, 1, 5000) : 200;
            query.Offset = offset.HasValue ? Math.Max(0, offset.Value) : 0;
            return query;
        }
    }
}
