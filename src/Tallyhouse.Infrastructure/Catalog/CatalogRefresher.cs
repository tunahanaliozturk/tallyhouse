using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tallyhouse.Infrastructure.Catalog;

/// <summary>
/// Keeps <see cref="CatalogCache"/> in step with Postgres by polling one integer. A schema registered through
/// another collector is visible here within one interval; one registered through this collector is visible
/// immediately, because the endpoint that wrote it refreshes before answering.
/// </summary>
/// <remarks>
/// Postgres going away does not stop ingestion. The cache keeps serving the last snapshot, and the only
/// thing that waits for the database is a change to the catalog.
/// </remarks>
public sealed partial class CatalogRefresher(
    CatalogService catalog,
    CatalogCache cache,
    TimeProvider time,
    ILogger<CatalogRefresher> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    public async Task RefreshAsync(CancellationToken cancellationToken) =>
        cache.Replace(await catalog.LoadAsync(cancellationToken));

    /// <summary>The first load happens before the host reports started, so no request ever sees an empty catalog.</summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval, time);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (await catalog.RevisionAsync(stoppingToken) != cache.Current.Revision)
                {
                    await RefreshAsync(stoppingToken);
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogRefreshFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Catalog refresh failed; still serving the last snapshot")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);
}
