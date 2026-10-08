using Finbuckle.MultiTenant;
using KiriyamaShareBackend.Application.Multitenancy;
using KiriyamaShareBackend.Shared.Authorization;
using KiriyamaShareBackend.Shared.Multitenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace KiriyamaShareBackend.Infrastructure.Multitenancy;

internal static class Startup
{
    internal static IServiceCollection AddMultitenancy(this IServiceCollection services)
    {
        // 租户信息放在内存里，不落库。
        // 之前用的是 Finbuckle 的 EF Core store，每次租户解析都会查一次 Tenants 表；
        // 而本项目目前只有 root 一个租户，且不需要数据库。
        var rootTenant = new GNXTenantInfo(
            MultitenancyConstants.Root.Id,
            MultitenancyConstants.Root.Name,
            string.Empty,
            MultitenancyConstants.Root.EmailAddress);
        rootTenant.SetValidity(DateTime.UtcNow.AddYears(1));

        return services
            .AddMultiTenant<GNXTenantInfo>()
                .WithClaimStrategy(GNXClaims.Tenant)
                .WithHeaderStrategy(MultitenancyConstants.TenantIdName)
                .WithQueryStringStrategy(MultitenancyConstants.TenantIdName)

                // 内存 store：只能有一个，不要和 EF Core store 同时注册。
                .WithInMemoryStore(options => options.Tenants.Add(rootTenant))
                .Services
            .AddScoped<ITenantService, TenantService>();
    }

    internal static IApplicationBuilder UseMultiTenancy(this IApplicationBuilder app) =>
        app.UseMultiTenant();

    private static MultiTenantBuilder<GNXTenantInfo> WithQueryStringStrategy(this MultiTenantBuilder<GNXTenantInfo> builder, string queryStringKey) =>
        builder.WithDelegateStrategy(context =>
        {
            if (context is not HttpContext httpContext)
            {
                return Task.FromResult((string?)null);
            }

            httpContext.Request.Query.TryGetValue(queryStringKey, out var tenantIdParam);

            return Task.FromResult((string?)tenantIdParam.ToString());
        });
}
