using Microsoft.Extensions.DependencyInjection;
using NeoSmart.Caching.Sqlite;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using OpenAI;
using OpenAI.Chat;
using Polly;
using Polly.RateLimiting;
using Polly.Retry;
using System.ClientModel;
using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Xml;
using ZiggyCreatures.Caching.Fusion;

namespace NugetTranslation;

internal static class Startup
{
    /// <summary>
    /// 注册FusionCache为HybridCache并使用Sqlite为二级缓存
    /// </summary>
    /// <param name="services">服务容器</param> 
    /// <param name="cachePath">缓存路径</param>
    public static IFusionCacheBuilder AddFusionCacheAndSqliteCache(this IServiceCollection services, string cachePath = "cache.sqlite.db", JsonSerializerOptions? jsonOptions = null)
    {
        return services
            .AddMemoryCache()
            .AddSqliteCache(cachePath)
            .AddFusionCacheSystemTextJsonSerializer(jsonOptions)
            .AddFusionCache()
            .WithDefaultEntryOptions(options =>
            {
                options.DistributedCacheDuration = TimeSpan.FromDays(365 * 1000);
            })
            .TryWithAutoSetup()
            .AsHybridCache();
    }

    /// <summary>注册<see cref="ChatClient"/></summary>
    /// <remarks>如果未提供参数，则从环境变量中获取，检查的key和参数名完全相同</remarks>
    public static IServiceCollection AddChatClient(this IServiceCollection services, string? model = null, string? apiKey = null, string? endpoint = null)
    {
        model ??= Environment.GetEnvironmentVariable(nameof(model));
        apiKey ??= Environment.GetEnvironmentVariable(nameof(apiKey));
        endpoint ??= Environment.GetEnvironmentVariable(nameof(endpoint));

        ArgumentNullException.ThrowIfNull(model, nameof(model));
        ArgumentNullException.ThrowIfNull(apiKey, nameof(apiKey));
        ArgumentNullException.ThrowIfNull(endpoint, nameof(endpoint));

        return services.AddSingleton(new ChatClient(model, new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(endpoint) }));
    }

    /// <summary>注册<see cref="SourceRepository"/>的网络获取。</summary> 
    public static IServiceCollection AddSourceRepository(this IServiceCollection services, string url = "https://api.nuget.org/v3/index.json")
    {
        return services.AddSingleton(Repository.Factory.GetCoreV3(url));
    }
    /// <summary>注册<see cref="SourceRepository"/>的网络获取。</summary> 
    /// <param name="replenishmentRatePerSecond">每秒令牌补充量</param>
    /// <param name="maxBurst">最高突发量（令牌桶容量）</param>
    /// <param name="expectedCompletionTimeInSeconds">期望完成时间（秒）</param>
    public static IServiceCollection AddPolly(this IServiceCollection services, int replenishmentRatePerSecond = 10, int maxBurst = 100, TimeSpan expectedCompletionTimeInSeconds = default)
    {
        var rpb = new ResiliencePipelineBuilder<string>();
        rpb.AddRetry(new RetryStrategyOptions<string>
        {
            ShouldHandle = new PredicateBuilder<string>().HandleResult(x => x is null)
                .Handle<XmlException>()
                , MaxRetryAttempts = 6
        });
        rpb.AddRateLimiterAndRetry(replenishmentRatePerSecond, maxBurst, expectedCompletionTimeInSeconds);
        services.AddSingleton(rpb.Build());
        return services;
    }

    /// <summary>
    /// 构建多层限流策略管道
    /// </summary>
    /// <param name="builder">管道构建器</param>
    /// <param name="replenishmentRatePerSecond">每秒令牌补充量</param>
    /// <param name="maxBurst">最高突发量（令牌桶容量）</param>
    /// <param name="expectedCompletionTimeInSeconds">期望完成时间（秒）</param>
    /// <returns>配置好的弹性管道</returns>
    public static ResiliencePipelineBuilder<T> AddRateLimiterAndRetry<T>(this ResiliencePipelineBuilder<T> builder,
        int replenishmentRatePerSecond, int maxBurst = 0,
       TimeSpan expectedCompletionTimeInSeconds = default)
    {
        if (maxBurst <= 0)
        {
            maxBurst = replenishmentRatePerSecond;
        }
        if (expectedCompletionTimeInSeconds == default)
        {
            expectedCompletionTimeInSeconds = TimeSpan.FromSeconds(maxBurst / replenishmentRatePerSecond);
        }
        return builder
            // 最外层：无限重试（捕获所有限流错误）
            .AddRetry(new RetryStrategyOptions<T>
            {
                ShouldHandle = new PredicateBuilder<T>().Handle<RateLimiterRejectedException>(),
                MaxRetryAttempts = 6,
                Delay = TimeSpan.FromSeconds(maxBurst) / replenishmentRatePerSecond
            })
            // 中间层：并发限流器（带可选的排队）
            .AddRateLimiter(new ConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = (int)(maxBurst / expectedCompletionTimeInSeconds.TotalSeconds + replenishmentRatePerSecond) + 1,
                QueueLimit = (int)(replenishmentRatePerSecond * expectedCompletionTimeInSeconds.TotalSeconds) + 1
            }))
            .AddRateLimiter(new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = maxBurst,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1) / replenishmentRatePerSecond,
                TokensPerPeriod = 1
            }));
    }
}
