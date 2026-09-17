using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using DwsEdge.Core.Rules;
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

            app.MapGet("/api/health", (SpoolStore store) => Results.Json(store.Health()));
            app.MapGet("/api/stats", (SpoolStore store) => Results.Json(store.Stats()));
            app.MapGet("/api/parcels", (SpoolStore store, int? limit) =>
            {
                int take = limit.HasValue ? Math.Clamp(limit.Value, 1, 500) : 50;
                return Results.Json(store.LatestParcels(take));
            });
            app.MapGet("/api/cameras", (SpoolStore store) => Results.Json(store.Cameras()));

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

            // B3：历史查询（条码 / 相机 / 无码 / 下发状态 / 图片），返回总数与耗时便于自证性能
            app.MapGet("/api/history", (SpoolStore store, string from, string to, string code, string deviceId,
                bool? noread, string dispatchState, bool? hasImage, int? limit, int? offset) =>
            {
                HistoryQuery query = BuildHistoryQuery(from, to, code, deviceId, noread, dispatchState, hasImage, limit, offset);
                return Results.Json(store.QueryHistory(query));
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
