using KiriyamaShareBackend.Application.Common.Interfaces;
using KiriyamaShareBackend.Application.Common.Persistence;
using KiriyamaShareBackend.Infrastructure.Caching;
using KiriyamaShareBackend.Infrastructure.Common.Services;
using KiriyamaShareBackend.Infrastructure.Localization;
using KiriyamaShareBackend.Infrastructure.Persistence.ConnectionString;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KiriyamaShareBackend.Infrastructure.Tests;

public class Startup
{
    public static void ConfigureHost(IHostBuilder host) =>
        host.ConfigureHostConfiguration(config => config.AddJsonFile("appsettings.json"));

    public static void ConfigureServices(IServiceCollection services, HostBuilderContext context) =>
        services
            .AddTransient<IMemoryCache, MemoryCache>()
            .AddTransient<LocalCacheService>()
            .AddTransient<IDistributedCache, MemoryDistributedCache>()
            .AddTransient<ISerializerService, NewtonSoftService>()
            .AddTransient<DistributedCacheService>()
            .AddPOLocalization(context.Configuration)
            .AddTransient<IConnectionStringSecurer, ConnectionStringSecurer>();
}