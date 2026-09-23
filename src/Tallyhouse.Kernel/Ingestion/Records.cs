namespace Tallyhouse.Kernel.Ingestion;

/// <summary>
/// A validated, normalised event as it is written to the log and, from there, to the fact table. Nothing in
/// it came from the client unchecked, and nothing in it depends on when or how often it is loaded, so
/// loading the same record twice produces the same row twice.
/// </summary>
/// <param name="ReceivedAt">When the collector accepted it. Decides lateness, and which copy of a duplicate
/// survives: the earliest.</param>
/// <param name="SampleThreshold">The number of user buckets kept for this event type when it arrived.
/// <see cref="Sampling.Buckets"/> means unsampled.</param>
public sealed record EventRecord(
    Guid ProjectId,
    string MessageId,
    string EventName,
    int SchemaVersion,
    string UserId,
    string AnonymousId,
    ulong UserKey,
    DateTimeOffset Timestamp,
    DateTimeOffset ReceivedAt,
    bool IsLate,
    ushort SampleThreshold,
    ushort SampleBucket,
    Dictionary<string, string> Properties);

/// <summary>
/// An event that failed validation, kept with the reason so somebody can fix the schema or the client and
/// replay it. <paramref name="Payload"/> is the event as received, with configured properties redacted.
/// </summary>
public sealed record QuarantineRecord(
    Guid ProjectId,
    Guid QuarantineId,
    DateTimeOffset ReceivedAt,
    string MessageId,
    string EventName,
    string Reason,
    string Payload);

public enum EventStatus
{
    Accepted,

    /// <summary>Already accepted once; this delivery is acknowledged and dropped.</summary>
    Duplicate,

    /// <summary>Valid, but the user is outside the sample for this event type.</summary>
    SampledOut,

    /// <summary>Invalid. Durably kept in quarantine, not in the fact table.</summary>
    Quarantined,
}

public readonly record struct EventOutcome(EventStatus Status, string? MessageId, string? Reason);
