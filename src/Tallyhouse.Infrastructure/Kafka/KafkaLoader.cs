using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tallyhouse.Application;
using Tallyhouse.Application.Ingestion;
using Tallyhouse.Domain.Events;
using Tallyhouse.Infrastructure.ClickHouse;

namespace Tallyhouse.Infrastructure.Kafka;

/// <summary>
/// Drains the log into ClickHouse in large batches. Offsets are stored only after the batch is written, so a
/// crash, a rebalance or a ClickHouse outage replays events rather than losing them, and the fact table's
/// deduplication absorbs the replay. At-least-once delivery into an idempotent sink is how "exactly once"
/// is actually built.
/// </summary>
/// <remarks>
/// While ClickHouse is down the loader retries the same batch with backoff and consumes nothing new. Lag
/// grows in Kafka, where there is a week of retention, and the collector keeps acknowledging because it
/// never waits for ClickHouse at all.
/// </remarks>
public sealed partial class KafkaLoader(
    string bootstrapServers,
    KafkaOptions kafka,
    LoaderOptions options,
    ClickHouseEventWriter writer,
    TimeProvider time,
    ILogger<KafkaLoader> logger) : BackgroundService
{
    private static readonly Counter<long> Loaded = Telemetry.Meter.CreateCounter<long>(
        "tallyhouse.loader.rows", "{row}", "Rows written to ClickHouse, tagged by table.");

    private static readonly Histogram<double> BatchDuration = Telemetry.Meter.CreateHistogram<double>(
        "tallyhouse.loader.write.duration", "s", "Time to write one loader batch to ClickHouse.");

    private static readonly Counter<long> WriteFailures = Telemetry.Meter.CreateCounter<long>(
        "tallyhouse.loader.write.failures", "{attempt}", "Batch writes that failed and will be retried.");

    private static readonly Counter<long> Unreadable = Telemetry.Meter.CreateCounter<long>(
        "tallyhouse.loader.unreadable", "{message}", "Log messages that could not be deserialised and were skipped.");

    private long lag;

    /// <summary>Messages in assigned partitions not yet written, as of the last batch.</summary>
    public long Lag => Interlocked.Read(ref lag);

    /// <summary>Set once the consumer has joined the group and received partitions.</summary>
    public bool Assigned { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Consume blocks its thread, so the loop gets one of its own rather than a thread-pool worker.
        Task.Factory.StartNew(() => RunAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        ConsumerConfig config = new()
        {
            BootstrapServers = bootstrapServers,
            GroupId = kafka.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,

            // librdkafka commits in the background, but only offsets this code has stored, and it stores an
            // offset only after the rows behind it are in ClickHouse.
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
            AutoCommitIntervalMs = 1000,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,

            // A long ClickHouse outage stops polling. Past this the consumer leaves the group and rejoins
            // afterwards from its last committed offset, which is correct, only noisier.
            MaxPollIntervalMs = 600_000,
        };

        using IConsumer<byte[], byte[]> consumer = new ConsumerBuilder<byte[], byte[]>(config)
            .SetPartitionsAssignedHandler((_, _) => Assigned = true)
            .SetLogHandler((_, message) => LogLibrdkafka(logger, message.Level, message.Message))
            .Build();

        consumer.Subscribe([kafka.EventsTopic, kafka.QuarantineTopic]);

        List<EventRecord> events = new(options.MaxBatchSize);
        List<QuarantineRecord> quarantined = [];
        Dictionary<TopicPartition, TopicPartitionOffset> next = [];

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Collect(consumer, events, quarantined, next, stoppingToken);

                if (next.Count == 0)
                {
                    continue;
                }

                await WriteUntilDoneAsync(events, quarantined, stoppingToken);
                Store(consumer, next.Values);
                UpdateLag(consumer);

                events.Clear();
                quarantined.Clear();
                next.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Anything consumed but not written has no stored offset and will be read again.
        }
        finally
        {
            // Commits stored offsets and leaves the group cleanly, so the partitions move without waiting
            // for a session timeout.
            consumer.Close();
        }
    }

    private void Collect(
        IConsumer<byte[], byte[]> consumer,
        List<EventRecord> events,
        List<QuarantineRecord> quarantined,
        Dictionary<TopicPartition, TopicPartitionOffset> next,
        CancellationToken stoppingToken)
    {
        long deadline = time.GetTimestamp() + (long)(options.MaxBatchDelay.TotalSeconds * Stopwatch.Frequency);

        while (events.Count + quarantined.Count < options.MaxBatchSize && !stoppingToken.IsCancellationRequested)
        {
            TimeSpan remaining = time.GetElapsedTime(time.GetTimestamp(), deadline);

            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            ConsumeResult<byte[], byte[]>? result = consumer.Consume(remaining);

            if (result is null)
            {
                break;
            }

            if (result.IsPartitionEOF)
            {
                continue;
            }

            next[result.TopicPartition] = new TopicPartitionOffset(result.TopicPartition, result.Offset + 1);

            try
            {
                if (result.Topic == kafka.EventsTopic)
                {
                    events.Add(JsonSerializer.Deserialize(result.Message.Value, LogJson.Default.EventRecord)!);
                }
                else
                {
                    quarantined.Add(JsonSerializer.Deserialize(result.Message.Value, LogJson.Default.QuarantineRecord)!);
                }
            }
            catch (JsonException exception)
            {
                // Only this collector writes to these topics, so an unreadable message is a bug, not data.
                // It is skipped rather than blocking the partition forever; it is still in Kafka for as long
                // as the topic retains it, and the log line says exactly where.
                Unreadable.Add(1);
                LogUnreadable(logger, exception, result.Topic, result.Partition.Value, result.Offset.Value);
            }
        }
    }

    private async Task WriteUntilDoneAsync(List<EventRecord> events, List<QuarantineRecord> quarantined, CancellationToken stoppingToken)
    {
        TimeSpan backoff = TimeSpan.FromMilliseconds(100);

        while (true)
        {
            long started = time.GetTimestamp();

            try
            {
                await writer.WriteAsync(events, quarantined, stoppingToken);

                BatchDuration.Record(time.GetElapsedTime(started).TotalSeconds);
                Loaded.Add(events.Count, new KeyValuePair<string, object?>("table", "events"));
                Loaded.Add(quarantined.Count, new KeyValuePair<string, object?>("table", "quarantine"));
                return;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested && exception is not OperationCanceledException)
            {
                WriteFailures.Add(1);
                LogWriteFailed(logger, exception, events.Count + quarantined.Count, backoff.TotalSeconds);

                await Task.Delay(backoff, time, stoppingToken);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            }
        }
    }

    private void Store(IConsumer<byte[], byte[]> consumer, IEnumerable<TopicPartitionOffset> offsets)
    {
        foreach (TopicPartitionOffset offset in offsets)
        {
            try
            {
                consumer.StoreOffset(offset);
            }
            catch (KafkaException exception)
            {
                // The partition was revoked while this batch was being written. Its new owner starts from the
                // last committed offset and writes these rows again, which deduplication makes harmless.
                LogRevoked(logger, offset.TopicPartition, exception.Error.Reason);
            }
        }
    }

    private void UpdateLag(IConsumer<byte[], byte[]> consumer)
    {
        long total = 0;

        foreach (TopicPartition partition in consumer.Assignment)
        {
            WatermarkOffsets watermarks = consumer.GetWatermarkOffsets(partition);
            Offset position = consumer.Position(partition);

            if (watermarks.High.Value >= 0 && position.Value >= 0)
            {
                total += Math.Max(0, watermarks.High.Value - position.Value);
            }
        }

        Interlocked.Exchange(ref lag, total);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "librdkafka {Level}: {Message}")]
    private static partial void LogLibrdkafka(ILogger logger, SyslogLevel level, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Skipped unreadable message at {Topic}[{Partition}]@{Offset}")]
    private static partial void LogUnreadable(ILogger logger, Exception exception, string topic, int partition, long offset);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing a batch of {Rows} rows failed; retrying in {Seconds}s")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, int rows, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not store offset for {Partition}: {Reason}")]
    private static partial void LogRevoked(ILogger logger, TopicPartition partition, string reason);
}
