using Finbuckle.MultiTenant.Abstractions;
using KiriyamaShareBackend.Application.Common.Events;
using KiriyamaShareBackend.Application.Common.Interfaces;
using KiriyamaShareBackend.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KiriyamaShareBackend.Infrastructure.Persistence.Context;

/// <summary>
/// 应用库。目前只剩认证相关表（Identity），由 <see cref="Infrastructure.Persistence.Configuration"/>
/// 里的配置按 <c>SchemaNames.Identity</c> 显式建表，所以这里不需要再指定默认 schema。
/// </summary>
public class ApplicationDbContext : BaseDbContext
{
    public ApplicationDbContext(IMultiTenantContextAccessor<GNXTenantInfo> multiTenantContextAccessor, DbContextOptions options, ICurrentUser currentUser, IOptions<DatabaseSettings> dbSettings, IEventPublisher events)
        : base(multiTenantContextAccessor, options, currentUser, dbSettings, events)
    {
    }
}
