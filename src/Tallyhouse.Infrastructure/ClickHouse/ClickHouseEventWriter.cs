using ClickHouse.Driver;
using Tallyhouse.Application.Ingestion;
using Tallyhouse.Domain.Events;

namespace Tallyhouse.Infrastructure.ClickHouse;

/// <summary>
/// Writes a loader batch: the events, the quarantined events, and the markers that tell the sessionizer
/// which days changed. Written in that order and all before the loader commits its offsets, so a crash
/// anywhere in between replays the whole batch, and replaying it is harmless because every table it touches
/// is deduplicated or idempotent.
/// </summary>
public sealed class ClickHouseEventWriter
{
    private readonly ClickHouseClient client;

    public ClickHouseEventWriter(ClickHouseClient client)
    {
        this.client = client;
        client.RegisterBinaryInsertType<EventRow>();
        client.RegisterBinaryInsertType<QuarantineRow>();
        client.RegisterBinaryInsertType<DirtyDayRow>();
    }

    public async Task WriteAsync(IReadOnlyList<EventRecord> events, IReadOnlyList<QuarantineRecord> quarantined, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(quarantined);

        if (events.Count > 0)
        {
            await client.InsertBinaryAsync("events", events.Select(EventRow.From), cancellationToken: cancellationToken);
        }

        if (quarantined.Count > 0)
        {
            await client.InsertBinaryAsync("quarantine", quarantined.Select(QuarantineRow.From), cancellationToken: cancellationToken);
        }

        // Late events are kept out of sessions, so they do not make a day dirty.
        DirtyDayRow[] dirty = [.. events
            .Where(record => !record.IsLate)
            .Select(record => (record.ProjectId, Day: DateOnly.FromDateTime(record.Timestamp.UtcDateTime)))
            .Distinct()
            .Select(pair => new DirtyDayRow { ProjectId = pair.ProjectId, Day = pair.Day })];

        if (dirty.Length > 0)
        {
            await client.InsertBinaryAsync("session_dirty", dirty, cancellationToken: cancellationToken);
        }
    }

    internal sealed class EventRow
    {
        [ClickHouseColumn(Name = "project_id")]
        public Guid ProjectId { get; init; }

        [ClickHouseColumn(Name = "event_name")]
        public required string EventName { get; init; }

        [ClickHouseColumn(Name = "user_key")]
        public ulong UserKey { get; init; }

        [ClickHouseColumn(Name = "ts")]
        public DateTime Timestamp { get; init; }

        [ClickHouseColumn(Name = "message_id")]
        public required string MessageId { get; init; }

        [ClickHouseColumn(Name = "user_id")]
        public required string UserId { get; init; }

        [ClickHouseColumn(Name = "anonymous_id")]
        public required string AnonymousId { get; init; }

        [ClickHouseColumn(Name = "schema_version")]
        public ushort SchemaVersion { get; init; }

        [ClickHouseColumn(Name = "properties")]
        public required Dictionary<string, string> Properties { get; init; }

        [ClickHouseColumn(Name = "received_at")]
        public DateTime ReceivedAt { get; init; }

        [ClickHouseColumn(Name = "is_late")]
        public bool IsLate { get; init; }

        [ClickHouseColumn(Name = "sample_threshold")]
        public ushort SampleThreshold { get; init; }

        [ClickHouseColumn(Name = "sample_bucket")]
        public ushort SampleBucket { get; init; }

        public static EventRow From(EventRecord record) => new()
        {
            ProjectId = record.ProjectId,
            EventName = record.EventName,
            UserKey = record.UserKey,
            Timestamp = record.Timestamp.UtcDateTime,
            MessageId = record.MessageId,
            UserId = record.UserId,
            AnonymousId = record.AnonymousId,
            SchemaVersion = (ushort)record.SchemaVersion,
            Properties = record.Properties,
            ReceivedAt = record.ReceivedAt.UtcDateTime,
            IsLate = record.IsLate,
            SampleThreshold = record.SampleThreshold,
            SampleBucket = record.SampleBucket,
        };
    }

    internal sealed class QuarantineRow
    {
        [ClickHouseColumn(Name = "project_id")]
        public Guid ProjectId { get; init; }

        [ClickHouseColumn(Name = "received_at")]
        public DateTime ReceivedAt { get; init; }

        [ClickHouseColumn(Name = "quarantine_id")]
        public Guid QuarantineId { get; init; }

        [ClickHouseColumn(Name = "message_id")]
        public required string MessageId { get; init; }

        [ClickHouseColumn(Name = "event_name")]
        public required string EventName { get; init; }

        [ClickHouseColumn(Name = "reason")]
        public required string Reason { get; init; }

        [ClickHouseColumn(Name = "payload")]
        public required string Payload { get; init; }

        [ClickHouseColumn(Name = "status")]
        public required string Status { get; init; }

        [ClickHouseColumn(Name = "updated_at")]
        public DateTime UpdatedAt { get; init; }

        public static QuarantineRow From(QuarantineRecord record) => new()
        {
            ProjectId = record.ProjectId,
            ReceivedAt = record.ReceivedAt.UtcDateTime,
            QuarantineId = record.QuarantineId,
            MessageId = record.MessageId,
            EventName = record.EventName,
            Reason = record.Reason,
            Payload = record.Payload,
            Status = "open",
            UpdatedAt = record.ReceivedAt.UtcDateTime,
        };
    }

    internal sealed class DirtyDayRow
    {
        [ClickHouseColumn(Name = "project_id")]
        public Guid ProjectId { get; init; }

        [ClickHouseColumn(Name = "day")]
        public DateOnly Day { get; init; }
    }
}
