using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Tallyhouse.Infrastructure.Kafka;

/// <summary>
/// Creates the topics if they are missing. Both hosts call it at start, so neither depends on broker
/// auto-creation, which production clusters usually disable and which would pick the wrong partition count.
/// </summary>
public static class KafkaTopics
{
    /// <summary>
    /// How long the log keeps events. This is the longest analytics store outage the pipeline survives
    /// without loss: the loader resumes from its committed offset once the store is back.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static async Task EnsureCreatedAsync(string bootstrapServers, KafkaOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        using IAdminClient admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

        Dictionary<string, string> config = new()
        {
            ["retention.ms"] = ((long)Retention.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        TopicSpecification[] topics =
        [
            new() { Name = options.EventsTopic, NumPartitions = options.Partitions, ReplicationFactor = options.ReplicationFactor, Configs = config },
            new() { Name = options.QuarantineTopic, NumPartitions = 3, ReplicationFactor = options.ReplicationFactor, Configs = config },
        ];

        try
        {
            await admin.CreateTopicsAsync(topics).WaitAsync(cancellationToken);
        }
        catch (CreateTopicsException exception) when (exception.Results.All(result =>
            result.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            // Another host got there first, or this is a restart.
        }
    }
}
