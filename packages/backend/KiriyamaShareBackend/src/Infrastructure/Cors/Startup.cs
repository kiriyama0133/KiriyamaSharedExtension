using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KiriyamaShareBackend.Infrastructure.Cors;

internal static class Startup
{
    private const string CorsPolicy = nameof(CorsPolicy);

    internal static IServiceCollection AddCorsPolicy(this IServiceCollection services, IConfiguration config)
    {
        var corsSettings = config.GetSection(nameof(CorsSettings)).Get<CorsSettings>();
        if (corsSettings == null) return services;
        var origins = new List<string>();
        if (corsSettings.Angular is not null)
            origins.AddRange(corsSettings.Angular.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (corsSettings.Blazor is not null)
            origins.AddRange(corsSettings.Blazor.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (corsSettings.React is not null)
            origins.AddRange(corsSettings.React.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // 浏览器扩展的 origin 只按前缀放行（见 CorsSettings.ExtensionOriginPrefixes 的说明）
        var extensionPrefixes = (corsSettings.ExtensionOriginPrefixes ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return services.AddCors(opt =>
            opt.AddPolicy(CorsPolicy, policy =>
                policy.AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()
                    // 这里不能用 AllowAnyOrigin：它与 AllowCredentials 互斥，而 SignalR 的协商请求是带凭据的
                    .SetIsOriginAllowed(origin =>
                        origins.Contains(origin, StringComparer.OrdinalIgnoreCase)
                        || extensionPrefixes.Any(prefix => origin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))));
    }

    internal static IApplicationBuilder UseCorsPolicy(this IApplicationBuilder app) =>
        app.UseCors(CorsPolicy);
}