using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.EntityFrameworkCore;
using KiriyamaShareBackend.Application.Common.Events;
using KiriyamaShareBackend.Application.Common.Interfaces;
using KiriyamaShareBackend.Domain.Common.Contracts;
using KiriyamaShareBackend.Infrastructure.Identity;
using KiriyamaShareBackend.Infrastructure.Multitenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Data;

namespace KiriyamaShareBackend.Infrastructure.Persistence.Context;

public abstract class BaseDbContext : MultiTenantIdentityDbContext<ApplicationUser, ApplicationRole, string, IdentityUserClaim<string>, IdentityUserRole<string>, IdentityUserLogin<string>, ApplicationRoleClaim, IdentityUserToken<string>>
{
    protected readonly ICurrentUser _currentUser;
    private readonly DatabaseSettings _dbSettings;
    private readonly IEventPublisher _events;

    protected BaseDbContext(IMultiTenantContextAccessor<GNXTenantInfo> multiTenantContextAccessor, DbContextOptions options, ICurrentUser currentUser, IOptions<DatabaseSettings> dbSettings, IEventPublisher events)
        : base(multiTenantContextAccessor, options)
    {
        _currentUser = currentUser;
        _dbSettings = dbSettings.Value;
        _events = events;
    }

    // Used by Dapper
    public IDbConnection Connection => Database.GetDbConnection();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // QueryFilters need to be applied before base.OnModelCreating
        modelBuilder.AppendGlobalQueryFilter<ISoftDelete>(s => s.DeletedOn == null);

        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // TODO: We want this only for development probably... maybe better make it configurable in logger.json config?
        optionsBuilder.EnableSensitiveDataLogging();

        // If you want to see the sql queries that ef core executes:

        // Uncomment the next line to see them in the output window of visual studio
        // optionsBuilder.LogTo(m => System.Diagnostics.Debug.WriteLine(m), Microsoft.Extensions.Logging.LogLevel.Information);

        // Or uncomment the next line if you want to see them in the console
        // optionsBuilder.LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information);

        GNXTenantInfo? gNXTenantInfo = TenantInfo as GNXTenantInfo;

        if (!string.IsNullOrWhiteSpace(gNXTenantInfo?.ConnectionString))
        {
            optionsBuilder.UseDatabase(_dbSettings.DBProvider, gNXTenantInfo.ConnectionString);
        }
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = new CancellationToken())
    {
        StampAuditableEntities(_currentUser.GetUserId());

        int result = await base.SaveChangesAsync(cancellationToken);

        await SendDomainEventsAsync();

        return result;
    }

    /// <summary>
    /// 给 <see cref="IAuditableEntity"/> 补上审计字段，并把"删除"翻成一次软删除 Update。
    ///
    /// 只落字段，不记变更流水 —— 原来的 AuditTrails 流水表与 Trail 类型随审计功能一起移除了，
    /// 留下的这两件事属于实体的基础契约（<see cref="IAuditableEntity"/> / <see cref="ISoftDelete"/>），
    /// 不是审计功能本身。
    /// </summary>
    private void StampAuditableEntities(DefaultIdType userId)
    {
        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedBy = userId;
                    entry.Entity.LastModifiedBy = userId;
                    break;

                case EntityState.Modified:
                    entry.Entity.LastModifiedOn = DateTime.UtcNow;
                    entry.Entity.LastModifiedBy = userId;
                    break;

                case EntityState.Deleted:
                    if (entry.Entity is ISoftDelete softDelete)
                    {
                        softDelete.DeletedBy = userId;
                        softDelete.DeletedOn = DateTime.UtcNow;
                        entry.State = EntityState.Modified;
                    }

                    break;
            }
        }

        ChangeTracker.DetectChanges();
    }

    private async Task SendDomainEventsAsync()
    {
        var entitiesWithEvents = ChangeTracker.Entries<IEntity>()
            .Select(e => e.Entity)
            .Where(e => e.DomainEvents.Count > 0)
            .ToArray();

        foreach (var entity in entitiesWithEvents)
        {
            var domainEvents = entity.DomainEvents.ToArray();
            entity.DomainEvents.Clear();
            foreach (var domainEvent in domainEvents)
            {
                await _events.PublishAsync(domainEvent);
            }
        }
    }
}
