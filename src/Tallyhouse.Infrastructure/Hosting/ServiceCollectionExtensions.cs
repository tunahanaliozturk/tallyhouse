using ClickHouse.Driver;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Tallyhouse.Infrastructure.Catalog;
using Tallyhouse.Infrastructure.ClickHouse;
using Tallyhouse.Infrastructure.ClickHouse.Queries;
using Tallyhouse.Infrastructure.Kafka;
using Tallyhouse.Infrastructure.Redis;
using Tallyhouse.Kernel.Ingestion;

namespace Tallyhouse.Infrastructure.Hosting;

/// <summary>
/// Wiring for the two hosts. Startup work is registered as hosted services in the order it must happen,
/// because the generic host starts hosted services one after another in registration order.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>The collector: catalog, ingest path, and read access to ClickHouse for queries.</summary>
    public static IServiceCollection AddTallyhouseCollector(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddTallyhouseOptions(configuration);

        string kafka = Required(configuration, "Kafka");
        string postgres = Required(configuration, "Postgres");

        services.AddPooledDbContextFactory<CatalogDbContext>(options => options
            .UseNpgsql(postgres)
            .UseSnakeCaseNamingConvention());
        services.AddSingleton<CatalogService>();
        services.AddSingleton<CatalogCache>();
        services.AddSingleton<ISchemaLookup>(sp => sp.GetRequiredService<CatalogCache>());
        services.AddSingleton<CatalogRefresher>();

        services.AddSingleton(sp => new KafkaEventLog(kafka, sp.GetRequiredService<TallyhouseOptions>().Kafka, sp.GetRequiredService<ILogger<KafkaEventLog>>()));
        services.AddSingleton<IEventLog>(sp => sp.GetRequiredService<KafkaEventLog>());
        services.AddSingleton<KafkaHealth>();

        if (configuration.GetConnectionString("Redis") is { Length: > 0 } redis)
        {
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(RedisOptions(redis)));
            services.AddSingleton<IDeduplicationFilter>(sp => new RedisDeduplication(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<TallyhouseOptions>().Ingestion.DeduplicationWindow,
                sp.GetRequiredService<ILogger<RedisDeduplication>>()));
        }
        else
        {
            services.AddSingleton<IDeduplicationFilter, NoDeduplication>();
        }

        services.AddSingleton(sp => sp.GetRequiredService<TallyhouseOptions>().Ingestion.Lateness());
        services.AddSingleton<EventNormalizer>();
        services.AddSingleton<IngestPipeline>();

        services.AddClickHouse(configuration);
        services.AddSingleton<QuarantineStore>();
        services.AddSingleton<AnalyticsQueries>();

        services.AddStartupTask(async (sp, ct) =>
        {
            await using CatalogDbContext context = await sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>().CreateDbContextAsync(ct);
            await context.Database.MigrateAsync(ct);
        });
        services.AddStartupTask((sp, ct) => KafkaTopics.EnsureCreatedAsync(kafka, sp.GetRequiredService<TallyhouseOptions>().Kafka, ct));
        services.AddHostedService(sp => sp.GetRequiredService<CatalogRefresher>());

        return services;
    }

    /// <summary>The loader: log to ClickHouse, and the sessionizer.</summary>
    public static IServiceCollection AddTallyhouseLoader(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddTallyhouseOptions(configuration);

        string kafka = Required(configuration, "Kafka");

        services.AddClickHouse(configuration);
        services.AddSingleton<ClickHouseMigrator>();
        services.AddSingleton<ClickHouseEventWriter>();
        services.AddSingleton(sp =>
        {
            TallyhouseOptions options = sp.GetRequiredService<TallyhouseOptions>();
            return new KafkaLoader(kafka, options.Kafka, options.Loader, sp.GetRequiredService<ClickHouseEventWriter>(),
                sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<KafkaLoader>>());
        });
        services.AddSingleton(sp => new Sessionizer(
            sp.GetRequiredService<ClickHouseClient>(),
            sp.GetRequiredService<TallyhouseOptions>().Sessions,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<Sessionizer>>()));

        services.AddStartupTask((sp, ct) => sp.GetRequiredService<ClickHouseMigrator>().MigrateAsync(ct));
        services.AddStartupTask((sp, ct) => KafkaTopics.EnsureCreatedAsync(kafka, sp.GetRequiredService<TallyhouseOptions>().Kafka, ct));
        services.AddHostedService(sp => sp.GetRequiredService<KafkaLoader>());
        services.AddHostedService(sp => sp.GetRequiredService<Sessionizer>());

        return services;
    }

    private static IServiceCollection AddTallyhouseOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TallyhouseOptions>().Bind(configuration.GetSection(TallyhouseOptions.Section));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<TallyhouseOptions>>().Value);
        services.AddSingleton(TimeProvider.System);
        return services;
    }

    private static IServiceCollection AddClickHouse(this IServiceCollection services, IConfiguration configuration)
    {
        string connectionString = Required(configuration, "ClickHouse");

        // Thread-safe and pools HTTP connections internally: one per process.
        services.AddSingleton(_ => new ClickHouseClient(connectionString));
        return services;
    }

    internal static IServiceCollection AddStartupTask(this IServiceCollection services, Func<IServiceProvider, CancellationToken, Task> task) =>
        services.AddSingleton<IHostedService>(sp => new StartupTask(ct => task(sp, ct), sp.GetRequiredService<ILogger<StartupTask>>()));

    private static ConfigurationOptions RedisOptions(string connectionString)
    {
        ConfigurationOptions options = ConfigurationOptions.Parse(connectionString);

        // The fast deduplication layer must never be the reason a request is slow. Past this it gives up and
        // the event goes through to the fact table's deduplication.
        options.AbortOnConnectFail = false;
        options.AsyncTimeout = 250;
        options.ConnectTimeout = 2000;
        return options;
    }

    private static string Required(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"ConnectionStrings:{name} is not configured.");
}
