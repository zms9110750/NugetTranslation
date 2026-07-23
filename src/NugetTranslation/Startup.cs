using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NeoSmart.Caching.Sqlite;
using System.Text.Json;
using ZiggyCreatures.Caching.Fusion;

namespace NugetTranslation;

internal static class Startup
{
    /// <summary>
    /// 注册FusionCache为HybridCache并使用Sqlite为二级缓存
    /// </summary>
    public static IFusionCacheBuilder AddFusionCacheAndSqliteCache(this IServiceCollection services, string cachePath = "cache.sqlite.db", JsonSerializerOptions? jsonOptions = null)
    {
        return AddFusionCacheAndSqliteCache(services, "FusionCache", cachePath, jsonOptions);
    }

    /// <summary>注册命名FusionCache。</summary>
    public static IFusionCacheBuilder AddFusionCacheAndSqliteCache(this IServiceCollection services, string name, string cachePath, JsonSerializerOptions? jsonOptions = null)
    {
        services.AddMemoryCache();
        services.AddFusionCacheSystemTextJsonSerializer(jsonOptions);

        var sqliteCache = new SqliteCache(
            Options.Create(new SqliteCacheOptions { CachePath = cachePath }));

        return services
            .AddFusionCache(name)
            .WithDistributedCache(sqliteCache)
            .WithDefaultEntryOptions(options => {
                options.DistributedCacheDuration = TimeSpan.FromDays(365 * 1000);
            })
            .AsHybridCache();
    }
}
