using KiriyamaShareBackend.Infrastructure.Caching;

namespace KiriyamaShareBackend.Infrastructure.Tests.Caching;

public class LocalCacheServiceTests(LocalCacheService cacheService) : CacheServiceTests(cacheService);
