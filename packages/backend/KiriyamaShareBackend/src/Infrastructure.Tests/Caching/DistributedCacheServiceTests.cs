using KiriyamaShareBackend.Infrastructure.Caching;

namespace KiriyamaShareBackend.Infrastructure.Tests.Caching;

public class DistributedCacheServiceTests(DistributedCacheService cacheService) : CacheServiceTests(cacheService);
