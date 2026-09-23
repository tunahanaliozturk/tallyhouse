using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Tallyhouse.Application;
using Tallyhouse.Application.Ingestion;
using Tallyhouse.Domain.Events;

namespace Tallyhouse.Infrastructure.Redis;

/// <summary>
/// Remembers recently accepted message ids so a client retry is answered "duplicate" without reaching the
/// log. Exact keys, never a probabilistic structure: a false positive here would silently drop an event the
/// client was told was accepted.
/// </summary>
/// <remarks>
/// It fails open. With Redis unreachable every event looks new, duplicates reach the log, and the fact table
/// removes them. Ingest availability therefore does not depend on Redis, which is also why the readiness
/// probe does not check it.
/// </remarks>
public sealed partial class RedisDeduplication(IConnectionMultiplexer redis, TimeSpan window, ILogger<RedisDeduplication> logger)
    : IDeduplicationFilter
{
    private static readonly Counter<long> Hits = Telemetry.Meter.CreateCounter<long>(
        "tallyhouse.dedup.hits", "{event}", "Deliveries the fast layer recognised as duplicates.");

    private static readonly Counter<long> Bypassed = Telemetry.Meter.CreateCounter<long>(
        "tallyhouse.dedup.bypassed", "{event}", "Deliveries checked while Redis was unavailable, so passed through unchecked.");

    public async ValueTask<bool[]> FindSeenAsync(Guid projectId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        bool[] seen = new bool[messageIds.Count];

        if (!redis.IsConnected)
        {
            Bypassed.Add(messageIds.Count);
            return seen;
        }

        try
        {
            // One pipelined round trip. Not MGET: a multi-key command needs every key in one cluster slot,
            // which would put a whole project's traffic on one shard.
            IDatabase database = redis.GetDatabase();
            IBatch batch = database.CreateBatch();
            Task<bool>[] lookups = new Task<bool>[messageIds.Count];

            for (int i = 0; i < lookups.Length; i++)
            {
                lookups[i] = batch.KeyExistsAsync(Key(projectId, messageIds[i]));
            }

            batch.Execute();
            await Task.WhenAll(lookups).WaitAsync(cancellationToken);

            int hits = 0;

            for (int i = 0; i < lookups.Length; i++)
            {
                seen[i] = lookups[i].Result;
                hits += seen[i] ? 1 : 0;
            }

            Hits.Add(hits);
            return seen;
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            LogUnavailable(logger, exception);
            Bypassed.Add(messageIds.Count);
            return new bool[messageIds.Count];
        }
    }

    public ValueTask RememberAsync(Guid projectId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageIds);

        if (!redis.IsConnected || messageIds.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        // Fire and forget: a lost write only means a later retry is caught by the fact table instead of here,
        // and waiting for the reply would add a round trip to every acknowledged request.
        IDatabase database = redis.GetDatabase();
        IBatch batch = database.CreateBatch();

        foreach (string messageId in messageIds)
        {
            _ = batch.StringSetAsync(Key(projectId, messageId), RedisValue.EmptyString, window, flags: CommandFlags.FireAndForget);
        }

        batch.Execute();
        return ValueTask.CompletedTask;
    }

    private static RedisKey Key(Guid projectId, string messageId) => $"th:dd:{projectId:N}:{messageId}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis deduplication unavailable; passing events through to the fact table's deduplication")]
    private static partial void LogUnavailable(ILogger logger, Exception exception);
}

/// <summary>Used when no Redis is configured, and by the proof that the fact table alone counts exactly once.</summary>
public sealed class NoDeduplication : IDeduplicationFilter
{
    public ValueTask<bool[]> FindSeenAsync(Guid projectId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new bool[messageIds?.Count ?? 0]);

    public ValueTask RememberAsync(Guid projectId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
