using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
            builder.Services.AddSingleton<ConfigStore>();
            builder.Services.AddHostedService<SpoolTailer>();
            builder.Services.AddHostedService<StorageProbe>();

            WebApplication app = builder.Build();

            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.MapGet("/api/health", (SpoolStore store) => Results.Json(store.Health()));
            app.MapGet("/api/stats", (SpoolStore store) => Results.Json(store.Stats()));
            app.MapGet("/api/parcels", (SpoolStore store, int? limit) =>
            {
                int take = limit.HasValue ? Math.Clamp(limit.Value, 1, 500) : 50;
                return Results.Json(store.LatestParcels(take));
            });
            app.MapGet("/api/cameras", (SpoolStore store) => Results.Json(store.Cameras()));

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
