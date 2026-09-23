using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tallyhouse.Infrastructure.Hosting;

/// <summary>
/// Work that must finish before the host serves anything: migrations, topic creation. Retried for a while,
/// because in a fresh stack the database is often still starting when the service is, and a crash loop is a
/// worse way to wait than a loop.
/// </summary>
public sealed partial class StartupTask(Func<CancellationToken, Task> work, ILogger<StartupTask> logger) : IHostedService
{
    private const int Attempts = 30;
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await work(cancellationToken);
                return;
            }
            catch (Exception exception) when (attempt < Attempts && !cancellationToken.IsCancellationRequested)
            {
                LogRetrying(logger, exception, attempt, Attempts);
                await Task.Delay(Pause, cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Startup step failed (attempt {Attempt} of {Attempts}); retrying")]
    private static partial void LogRetrying(ILogger logger, Exception exception, int attempt, int attempts);
}
