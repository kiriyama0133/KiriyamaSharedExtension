using System.Reflection;
using System.Runtime.CompilerServices;
using KiriyamaShareBackend.Infrastructure.Auth;
using KiriyamaShareBackend.Infrastructure.Caching;
using KiriyamaShareBackend.Infrastructure.Common;
using KiriyamaShareBackend.Infrastructure.Cors;
using KiriyamaShareBackend.Infrastructure.FileStorage;
using KiriyamaShareBackend.Infrastructure.Localization;
using KiriyamaShareBackend.Infrastructure.Mailing;
using KiriyamaShareBackend.Infrastructure.Mapping;
using KiriyamaShareBackend.Infrastructure.Middleware;
using KiriyamaShareBackend.Infrastructure.Multitenancy;
using KiriyamaShareBackend.Infrastructure.OpenApi;
using KiriyamaShareBackend.Infrastructure.Persistence;
using KiriyamaShareBackend.Infrastructure.Persistence.Initialization;
using KiriyamaShareBackend.Infrastructure.SecurityHeaders;
using KiriyamaShareBackend.Infrastructure.Sharing;
using KiriyamaShareBackend.Infrastructure.Validations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: InternalsVisibleTo("Infrastructure.Test")]

namespace KiriyamaShareBackend.Infrastructure;

public static class Startup
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var applicationAssembly = typeof(Application.Startup).GetTypeInfo().Assembly;
        MapsterSettings.Configure();
        return services
            .AddGnxApiVersioning()
            .AddGnxAuth(config)
            .AddCaching(config)
            .AddCorsPolicy(config)
            .AddExceptionMiddleware()
            .Behaviors(applicationAssembly)
            .AddHealthCheck()
            .AddPOLocalization(config)
            .AddMailing(config)
            .AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(AppDomain.CurrentDomain.GetAssemblies()))
            .AddMultitenancy()
            .AddOpenApiDocumentation(config)
            .AddPersistence()
            .AddRequestLogging(config)
            .AddRouting(options => options.LowercaseUrls = true)
            .AddSharing(config)
            .AddServices();
    }

    private static IServiceCollection AddGnxApiVersioning(this IServiceCollection services)
    {
        // Core API Versioning services with support for Minimal APIs
        // API version-aware extensions for MVC Core with controllers (not full MVC)
        services
                .AddApiVersioning()
                .AddMvc()
                .AddApiExplorer();

        // API version-aware API Explorer extensions
        services.AddEndpointsApiExplorer();
        return services;
    }

    private static IServiceCollection AddHealthCheck(this IServiceCollection services)
        => services.AddHealthChecks().AddCheck<TenantHealthCheck>("Tenant").Services;

    public static async Task InitializeDatabasesAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        // Create a new scope to retrieve scoped services
        using var scope = services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeDatabasesAsync(cancellationToken);
    }

    public static IApplicationBuilder UseInfrastructure(this IApplicationBuilder builder, IConfiguration config) =>
        builder
            .UseRequestLocalization()
            .UseStaticFiles()
            .UseSecurityHeaders(config)
            .UseFileStorage()
            .UseExceptionMiddleware()
            .UseRouting()
            .UseCorsPolicy()
            .UseAuthentication()
            .UseGnxCurrentUser()
            .UseMultiTenancy()
            .UseAuthorization()
            .UseRequestLogging(config)
            .UseOpenApiDocumentation(config);

    public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapControllers().RequireAuthorization();
        builder.MapHealthCheck();
        builder.MapSharing();
        return builder;
    }

    private static IEndpointConventionBuilder MapHealthCheck(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapHealthChecks("/healthz");
}