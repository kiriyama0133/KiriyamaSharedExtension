using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using KiriyamaShareBackend.Infrastructure.Multitenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KiriyamaShareBackend.Infrastructure.Persistence.Initialization;

/// <summary>
/// 建表 / 种子数据。只在 <c>DatabaseSettings:InitializeOnStartup</c> 打开时才会被调用
/// （见 <c>WebApi/Program.cs</c>），本项目默认不启动数据库。
/// </summary>
internal class DatabaseInitializer : IDatabaseInitializer
{
    private readonly IMultiTenantStore<GNXTenantInfo> _tenantStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(IMultiTenantStore<GNXTenantInfo> tenantStore, IServiceProvider serviceProvider, ILogger<DatabaseInitializer> logger)
    {
        _tenantStore = tenantStore ?? throw new ArgumentNullException(nameof(tenantStore));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InitializeDatabasesAsync(CancellationToken cancellationToken)
    {
        // 租户元数据本身不落库了（内存 store，root 租户在 Multitenancy/Startup.cs 里预置），
        // 所以这里只需要为每个租户初始化各自的应用库。
        foreach (var tenant in await _tenantStore.GetAllAsync())
        {
            await InitializeApplicationDbForTenantAsync(tenant, cancellationToken);
        }

        _logger.LogInformation("For documentations and guides, visit https://genocs-blog.netlify.app");
        _logger.LogInformation("To Sponsor this project, visit https://opencollective.com/genocs");
    }

    public async Task InitializeApplicationDbForTenantAsync(GNXTenantInfo tenant, CancellationToken cancellationToken)
    {
        // First create a new scope
        using var scope = _serviceProvider.CreateScope();

        // Then set current tenant so the right ConnectionString is used
        _serviceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<GNXTenantInfo>()
            {
                TenantInfo = tenant
            };

        // Then run the initialization in the new scope
        await scope.ServiceProvider.GetRequiredService<ApplicationDbInitializer>()
            .InitializeAsync(cancellationToken);
    }
}
