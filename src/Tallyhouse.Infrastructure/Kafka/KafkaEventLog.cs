using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Tallyhouse.Kernel.Ingestion;

namespace Tallyhouse.Infrastructure.Kafka;

/// <summary>
/// The collector's durable buffer. An append completes when every record in it has been acknowledged by all
/// in-sync replicas, which is the moment the collector may answer 202.
/// </summary>
/// <remarks>
/// <para>There is no in-process queue in front of the producer. librdkafka already holds a bounded queue and
/// batches from it; a second queue would add a place for acknowledged-looking events to die without adding
/// a property. When that queue is full, produce fails at once and the request gets a 503, which is the
/// back-pressure the client should see.</para>
/// <para>The producer is idempotent, so a broker retry inside librdkafka cannot write a record twice. A
/// client retry can, and that is what the two deduplication layers are for.</para>
/// </remarks>
public sealed partial class KafkaEventLog : IEventLog, IDisposable
{
    private readonly IProducer<byte[], byte[]> producer;
    private readonly KafkaOptions options;

    public KafkaEventLog(string bootstrapServers, KafkaOptions options, ILogger<KafkaEventLog> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;

        ProducerConfig config = new()
        {
            BootstrapServers = bootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            LingerMs = options.LingerMilliseconds,
            CompressionType = CompressionType.Lz4,
            MessageTimeoutMs = (int)options.DeliveryTimeout.TotalMilliseconds,
            QueueBufferingMaxMessages = options.MaxQueuedMessages,

            // The report only has to say whether delivery worked. The default also marshals the key and value
            // back into managed memory for every message, which is an allocation per event for nothing.
            DeliveryReportFields = "status",
        };

        producer = new ProducerBuilder<byte[], byte[]>(config)
            .SetLogHandler((_, message) => LogLibrdkafka(logger, message.Level, message.Message))
            .Build();
    }

    internal Handle Handle => producer.Handle;

    public Task AppendAsync(IReadOnlyList<EventRecord> events, IReadOnlyList<QuarantineRecord> quarantined, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(quarantined);

        BatchDelivery delivery = new(events.Count + quarantined.Count);
        int produced = 0;

        try
        {
            foreach (EventRecord record in events)
            {
                producer.Produce(options.EventsTopic, new Message<byte[], byte[]>
                {
                    Key = UserKey(record.UserKey),
                    Value = JsonSerializer.SerializeToUtf8Bytes(record, LogJson.Default.EventRecord),
                }, delivery.Report);
                produced++;
            }

            foreach (QuarantineRecord record in quarantined)
            {
                producer.Produce(options.QuarantineTopic, new Message<byte[], byte[]>
                {
                    Key = record.ProjectId.ToByteArray(),
                    Value = JsonSerializer.SerializeToUtf8Bytes(record, LogJson.Default.QuarantineRecord),
                }, delivery.Report);
                produced++;
            }
        }
        catch (KafkaException exception)
        {
            // Typically Local_QueueFull. What was already produced will still be reported; what was not never
            // will be, so it is failed here and the whole append fails.
            delivery.Abandon(delivery.Expected - produced, exception.Error);
        }

        return delivery.Completion;
    }

    public void Dispose()
    {
        // Give in-flight deliveries a chance to finish on shutdown rather than failing requests that were
        // about to succeed.
        producer.Flush(TimeSpan.FromSeconds(5));
        producer.Dispose();
    }

    private static byte[] UserKey(ulong userKey)
    {
        byte[] key = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(key, userKey);
        return key;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "librdkafka {Level}: {Message}")]
    private static partial void LogLibrdkafka(ILogger logger, SyslogLevel level, string message);

    /// <summary>One completion for a whole batch, completed by the last delivery report, instead of a task per message.</summary>
    private sealed class BatchDelivery
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int pending;
        private Error? firstError;

        public BatchDelivery(int expected)
        {
            Expected = expected;
            pending = expected;

            if (expected == 0)
            {
                completion.SetResult();
            }
        }

        public int Expected { get; }

        public Task Completion => completion.Task;

        public void Report(DeliveryReport<byte[], byte[]> report)
        {
            if (report.Error.IsError)
            {
                Interlocked.CompareExchange(ref firstError, report.Error, null);
            }

            Settle(1);
        }

        public void Abandon(int count, Error error)
        {
            Interlocked.CompareExchange(ref firstError, error, null);
            Settle(count);
        }

        private void Settle(int count)
        {
            if (count <= 0 || Interlocked.Add(ref pending, -count) != 0)
            {
                return;
            }

            if (Volatile.Read(ref firstError) is { } error)
            {
                completion.SetException(new EventLogUnavailableException($"Kafka did not acknowledge the batch: {error.Reason} ({error.Code})"));
            }
            else
            {
                completion.SetResult();
            }
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EventRecord))]
[JsonSerializable(typeof(QuarantineRecord))]
internal sealed partial class LogJson : JsonSerializerContext;
