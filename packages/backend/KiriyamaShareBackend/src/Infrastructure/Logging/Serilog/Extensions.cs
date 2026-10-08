using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Exceptions;
using Serilog.Formatting.Compact;

namespace KiriyamaShareBackend.Infrastructure.Logging.Serilog;

public static class Extensions
{
    public static void RegisterSerilog(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<LoggerSettings>().BindConfiguration(nameof(LoggerSettings));

        _ = builder.Host.UseSerilog((_, sp, serilogConfig) =>
        {
            var loggerSettings = sp.GetRequiredService<IOptions<LoggerSettings>>().Value;
            string appName = loggerSettings.AppName;
            string elasticSearchUrl = loggerSettings.ElasticSearchUrl;
            bool writeToFile = loggerSettings.WriteToFile;
            bool structuredConsoleLogging = loggerSettings.StructuredConsoleLogging;
            string minLogLevel = loggerSettings.MinimumLogLevel;
            ConfigureEnrichers(serilogConfig, appName);
            ConfigureConsoleLogging(serilogConfig, structuredConsoleLogging);
            ConfigureWriteToFile(serilogConfig, writeToFile);
            ConfigureElasticSearch(builder.Environment.EnvironmentName, serilogConfig, appName, elasticSearchUrl);
            SetMinimumLogLevel(serilogConfig, minLogLevel);
            OverrideMinimumLogLevel(serilogConfig);
        });
    }

    public static IHostBuilder UseLogging(this IHostBuilder builder, string environment = "debug")
    {
        builder.UseSerilog((_, sp, serilogConfig) =>
         {
             var loggerSettings = sp.GetRequiredService<IOptions<LoggerSettings>>().Value;
             string appName = loggerSettings.AppName;
             string elasticSearchUrl = loggerSettings.ElasticSearchUrl;
             bool writeToFile = loggerSettings.WriteToFile;
             bool structuredConsoleLogging = loggerSettings.StructuredConsoleLogging;
             string minLogLevel = loggerSettings.MinimumLogLevel;
             ConfigureEnrichers(serilogConfig, appName);
             ConfigureConsoleLogging(serilogConfig, structuredConsoleLogging);
             ConfigureWriteToFile(serilogConfig, writeToFile);
             ConfigureElasticSearch(environment, serilogConfig, appName, elasticSearchUrl);
             SetMinimumLogLevel(serilogConfig, minLogLevel);
             OverrideMinimumLogLevel(serilogConfig);
         });

        return builder;
    }

    private static void ConfigureEnrichers(LoggerConfiguration serilogConfig, string appName)
    {
        serilogConfig
                        .Enrich.FromLogContext()
                        .Enrich.WithProperty("Application", appName)
                        .Enrich.WithExceptionDetails()
                        .Enrich.WithMachineName()
                        .Enrich.WithProcessId()
                        .Enrich.WithThreadId()
                        .Enrich.FromLogContext();
    }

    private static void ConfigureConsoleLogging(LoggerConfiguration serilogConfig, bool structuredConsoleLogging)
    {
        if (structuredConsoleLogging)
        {
            serilogConfig.WriteTo.Async(wt => wt.Console(new CompactJsonFormatter()));
        }
        else
        {
            serilogConfig.WriteTo.Async(wt => wt.Console());
        }
    }

    private static void ConfigureWriteToFile(LoggerConfiguration serilogConfig, bool writeToFile)
    {
        if (writeToFile)
        {
            serilogConfig.WriteTo.File(
             new CompactJsonFormatter(),
             "Logs/logs.json",
             restrictedToMinimumLevel: LogEventLevel.Information,
             rollingInterval: RollingInterval.Day,
             retainedFileCountLimit: 5);
        }
    }

    private static void ConfigureElasticSearch(string environment, LoggerConfiguration serilogConfig, string appName, string? elasticSearchUrl)
    {
        if (!string.IsNullOrWhiteSpace(elasticSearchUrl))
        {
            string? formattedAppName = appName?.ToLower().Replace(".", "-").Replace(" ", "-");
            string indexFormat = $"{formattedAppName}-logs-{environment.ToLower().Replace(".", "-")}-{DateTime.UtcNow:yyyy-MM}";
            serilogConfig.WriteTo.Async(writeTo =>
            writeTo.Elasticsearch(new(new Uri(elasticSearchUrl))
            {
                AutoRegisterTemplate = true,
                IndexFormat = indexFormat,
                MinimumLogEventLevel = LogEventLevel.Information,
            })).Enrich.WithProperty("Environment", environment);
        }
    }

    private static void OverrideMinimumLogLevel(LoggerConfiguration serilogConfig)
    {
        serilogConfig
                     .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                     .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                     .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Error)
                     // SignalR 的连接诊断（握手失败、**消息超过上限**、心跳超时…）全部打在 Debug 上，
                     // 上面那条 "Microsoft = Warning" 会把它们整段按掉 —— 结果是连接掉了以后
                     // 日志里一条原因都没有，只剩我们自己的 "Client disconnected"。
                     // 这两个分类单独放宽；代价只是每个连接几条握手日志。
                     .MinimumLevel.Override("Microsoft.AspNetCore.SignalR", LogEventLevel.Debug)
                     .MinimumLevel.Override("Microsoft.AspNetCore.Http.Connections", LogEventLevel.Debug);
    }

    private static void SetMinimumLogLevel(LoggerConfiguration serilogConfig, string minLogLevel)
    {
        switch (minLogLevel.ToLower())
        {
            case "debug":
                serilogConfig.MinimumLevel.Debug();
                break;
            case "information":
                serilogConfig.MinimumLevel.Information();
                break;
            case "warning":
                serilogConfig.MinimumLevel.Warning();
                break;
            default:
                serilogConfig.MinimumLevel.Information();
                break;
        }
    }
}
