using Confluent.Kafka;

namespace Tallyhouse.Infrastructure.Kafka;

/// <summary>
/// Whether the collector can currently reach a broker, asked through the producer's own connection so the
/// answer is about the path events actually take. Cached briefly: a readiness probe every second should not
/// become a metadata request every second.
/// </summary>
public sealed class KafkaHealth(KafkaEventLog log, TimeProvider time) : IDisposable
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    private readonly IAdminClient admin = new DependentAdminClientBuilder(log.Handle).Build();
    private readonly SemaphoreSlim gate = new(1, 1);
    private Probe last = new(DateTimeOffset.MinValue, false);

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        Probe probe = Volatile.Read(ref last);

        if (time.GetUtcNow() - probe.At < CacheFor)
        {
            return probe.Healthy;
        }

        await gate.WaitAsync(cancellationToken);

        try
        {
            probe = Volatile.Read(ref last);

            if (time.GetUtcNow() - probe.At >= CacheFor)
            {
                // GetMetadata blocks, so it runs off the request thread.
                bool healthy = await Task.Run(() =>
                {
                    try
                    {
                        return admin.GetMetadata(TimeSpan.FromSeconds(2)).Brokers.Count > 0;
                    }
                    catch (KafkaException)
                    {
                        return false;
                    }
                }, cancellationToken);

                probe = new Probe(time.GetUtcNow(), healthy);
                Volatile.Write(ref last, probe);
            }

            return probe.Healthy;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        admin.Dispose();
        gate.Dispose();
    }

    private sealed record Probe(DateTimeOffset At, bool Healthy);
}
