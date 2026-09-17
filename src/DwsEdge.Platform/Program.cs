using System;
using System.Threading;
using System.Threading.Tasks;
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
            builder.Services.AddSingleton<BarcodeRuleStore>();
            builder.Services.AddSingleton<ConfigStore>();
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

            app.MapGet("/api/history", (SpoolStore store, string from, string to, string code, string deviceId, bool? noread, int? limit) =>
            {
                DateTime fromDay = ParseDay(from, DateTime.Today.AddDays(-1));
                DateTime toDay = ParseDay(to, DateTime.Today);
                if (toDay < fromDay)
                {
                    DateTime swap = fromDay;
                    fromDay = toDay;
                    toDay = swap;
                }
                int take = limit.HasValue ? Math.Clamp(limit.Value, 1, 2000) : 200;
                return Results.Json(store.QueryHistory(fromDay, toDay, code, deviceId, noread, take));
            });
            app.MapGet("/api/images", (SpoolStore store, string path) => store.OpenImage(path));
            app.MapGet("/api/stream", (HttpContext context, SpoolStore store, CancellationToken token) =>
                store.StreamAsync(context, token));

            ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Platform");
            logger.LogInformation("DwsEdge.Platform 启动完成；图片根目录：{0}", app.Services.GetRequiredService<SpoolStore>().ImagesRoot);

            app.Run();
        }

        private static DateTime ParseDay(string value, DateTime fallback)
        {
            DateTime parsed;
            if (!string.IsNullOrEmpty(value) && DateTime.TryParse(value, out parsed))
            {
                return parsed.Date;
            }
            return fallback.Date;
        }
    }
}
