namespace Tallyhouse.Kernel.Ingestion;

public enum EventTiming
{
    OnTime,

    /// <summary>Older than the watermark. Stored, but flagged and kept out of closed time buckets.</summary>
    Late,

    /// <summary>Further in the future than clock skew explains. Quarantined.</summary>
    TooFarInFuture,

    /// <summary>Older than the retention period, so it would be deleted on arrival. Quarantined.</summary>
    BeyondRetention,
}

/// <summary>
/// Where an event belongs in time. An event is bucketed by the time it says it happened, and the watermark
/// is the point after which a bucket is considered closed: an event older than that when it arrives is still
/// stored and still counted, but in a labelled late bucket rather than silently rewriting a day somebody
/// already reported on.
/// </summary>
/// <remarks>
/// The watermark moves with the collector's clock, not with the newest event time seen. An event-time
/// watermark lets one client with a clock set to next year close every bucket for everybody.
/// </remarks>
public sealed record LatenessPolicy(TimeSpan Watermark, TimeSpan MaxClockSkew, TimeSpan Retention)
{
    public static LatenessPolicy Default { get; } = new(TimeSpan.FromHours(2), TimeSpan.FromMinutes(5), TimeSpan.FromDays(90));

    public EventTiming Classify(DateTimeOffset eventTime, DateTimeOffset receivedAt)
    {
        if (eventTime > receivedAt + MaxClockSkew)
        {
            return EventTiming.TooFarInFuture;
        }

        if (eventTime < receivedAt - Retention)
        {
            return EventTiming.BeyondRetention;
        }

        return eventTime < receivedAt - Watermark ? EventTiming.Late : EventTiming.OnTime;
    }
}
