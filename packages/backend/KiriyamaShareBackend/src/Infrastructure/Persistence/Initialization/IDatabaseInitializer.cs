using KiriyamaShareBackend.Infrastructure.Multitenancy;

namespace KiriyamaShareBackend.Infrastructure.Persistence.Initialization;

internal interface IDatabaseInitializer
{
    Task InitializeDatabasesAsync(CancellationToken cancellationToken);
    Task InitializeApplicationDbForTenantAsync(GNXTenantInfo tenant, CancellationToken cancellationToken);
}