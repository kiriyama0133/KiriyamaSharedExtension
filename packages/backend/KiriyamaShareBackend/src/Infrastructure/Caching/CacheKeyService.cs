using Finbuckle.MultiTenant.Abstractions;
using KiriyamaShareBackend.Application.Common.Caching;
using KiriyamaShareBackend.Infrastructure.Multitenancy;

namespace KiriyamaShareBackend.Infrastructure.Caching;

public class CacheKeyService(IMultiTenantContextAccessor<GNXTenantInfo> multiTenantContextAccessor) : ICacheKeyService
{
    private readonly ITenantInfo? _currentTenant = multiTenantContextAccessor?.MultiTenantContext?.TenantInfo;

    public string GetCacheKey(string name, object id, bool includeTenantId = true)
    {
        string tenantId = includeTenantId
            ? _currentTenant?.Id ?? throw new InvalidOperationException("GetCacheKey: includeTenantId set to true and no ITenantInfo available.")
            : "GLOBAL";
        return $"{tenantId}-{name}-{id}";
    }
}