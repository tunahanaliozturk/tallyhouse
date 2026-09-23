using Microsoft.Extensions.DependencyInjection;
using Tallyhouse.Application.Ingestion;
using Tallyhouse.Domain.Events;
using Tallyhouse.Infrastructure.ClickHouse;

namespace Tallyhouse.IntegrationTests;

/// <summary>
/// Writes events straight into the fact table through the loader's writer, bypassing ingestion. Query tests
/// need exact timestamps days in the past, which the collector would rightly flag as late; what they test
/// is what the queries do with rows, not how the rows arrived.
/// </summary>
public static class Facts
{
    public static EventRecord Event(Guid projectId, string name, string user, DateTimeOffset at, Dictionary<string, string>? properties = null, bool late = false, string? messageId = null)
    {
        ulong key = UserIdentity.KeyOf(user, null);

        return new EventRecord(
            projectId,
            messageId ?? Guid.NewGuid().ToString("N"),
            name,
            1,
            user,
            string.Empty,
            key,
            at,
            late ? at.AddHours(3) : at,
            late,
            Sampling.Buckets,
            Sampling.BucketOf(key),
            properties ?? []);
    }

    public static Task WriteAsync(this TestRig rig, IReadOnlyList<EventRecord> events) =>
        rig.Loader.Services.GetRequiredService<ClickHouseEventWriter>().WriteAsync(events, [], CancellationToken.None);
}
