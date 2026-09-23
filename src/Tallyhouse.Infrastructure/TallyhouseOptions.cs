using Tallyhouse.Application.Ingestion;
using Tallyhouse.Domain.Events;

namespace Tallyhouse.Infrastructure;

/// <summary>
/// Everything tunable, bound from the <c>Tallyhouse</c> configuration section. Connection strings live in
/// the standard <c>ConnectionStrings</c> section instead. Defaults are the values the README's numbers were
/// measured with.
/// </summary>
public sealed class TallyhouseOptions
{
    public const string Section = "Tallyhouse";

    /// <summary>Creates projects, registers schemas, changes settings and replays quarantine. Null disables those endpoints.</summary>
    public string? OperatorToken { get; set; }

    public KafkaOptions Kafka { get; set; } = new();

    public IngestionOptions Ingestion { get; set; } = new();

    public LoaderOptions Loader { get; set; } = new();

    public SessionOptions Sessions { get; set; } = new();
}

public sealed class KafkaOptions
{
    public string EventsTopic { get; set; } = "tallyhouse.events";

    public string QuarantineTopic { get; set; } = "tallyhouse.quarantine";

    /// <summary>Partitions for the events topic. The key is the user, so this caps loader parallelism, not ordering.</summary>
    public int Partitions { get; set; } = 12;

    public short ReplicationFactor { get; set; } = 1;

    /// <summary>How long librdkafka keeps trying to deliver before the request fails with 503.</summary>
    public TimeSpan DeliveryTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the producer waits to fill a batch. Trades a few milliseconds of latency for throughput.</summary>
    public int LingerMilliseconds { get; set; } = 5;

    /// <summary>
    /// Messages the producer may hold before refusing more. This is the collector's back-pressure: when the
    /// broker cannot keep up the queue fills, produce fails fast, and the client gets a 503 to retry.
    /// </summary>
    public int MaxQueuedMessages { get; set; } = 200_000;

    public string ConsumerGroup { get; set; } = "tallyhouse-loader";
}

public sealed class IngestionOptions
{
    public TimeSpan Watermark { get; set; } = LatenessPolicy.Default.Watermark;

    public TimeSpan MaxClockSkew { get; set; } = LatenessPolicy.Default.MaxClockSkew;

    public TimeSpan Retention { get; set; } = LatenessPolicy.Default.Retention;

    public int MaxBatchSize { get; set; } = 500;

    /// <summary>
    /// How long the fast deduplication layer remembers a message id. Sized for client retries, not for the
    /// watermark: at 20,000 events a second a two-hour window is 144 million keys. The fact table catches
    /// whatever arrives later. See docs/adr/0002.
    /// </summary>
    public TimeSpan DeduplicationWindow { get; set; } = TimeSpan.FromMinutes(10);

    public LatenessPolicy Lateness() => new(Watermark, MaxClockSkew, Retention);
}

public sealed class LoaderOptions
{
    /// <summary>Rows per ClickHouse insert. ClickHouse wants few large inserts; one per event would bury it in parts.</summary>
    public int MaxBatchSize { get; set; } = 50_000;

    /// <summary>The longest an event waits in the loader before being inserted in a smaller batch.</summary>
    public TimeSpan MaxBatchDelay { get; set; } = TimeSpan.FromSeconds(1);
}

public sealed class SessionOptions
{
    public TimeSpan InactivityGap { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);
}
