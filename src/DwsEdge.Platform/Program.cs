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
            app.MapGet("/api/images", (SpoolStore store, string path) => store.OpenImage(path));
            app.MapGet("/api/stream", (HttpContext context, SpoolStore store, CancellationToken token) =>
                store.StreamAsync(context, token));

            ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Platform");
            logger.LogInformation("DwsEdge.Platform 启动完成；图片根目录：{0}", app.Services.GetRequiredService<SpoolStore>().ImagesRoot);

            app.Run();
        }
    }
}
