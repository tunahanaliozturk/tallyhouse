using Tallyhouse.Core.Schemas;

namespace Tallyhouse.Core.Ingestion;

/// <summary>
/// The durable buffer. Once <see cref="AppendAsync"/> returns, every record passed to it survives the loss of
/// the collector, the loader and the analytics store, which is what makes it safe to acknowledge the client.
/// </summary>
public interface IEventLog
{
    /// <exception cref="EventLogUnavailableException">Some records could not be made durable. None of them
    /// may be acknowledged; a retry is safe because delivery is deduplicated downstream.</exception>
    Task AppendAsync(IReadOnlyList<EventRecord> events, IReadOnlyList<QuarantineRecord> quarantined, CancellationToken cancellationToken);
}

public sealed class EventLogUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// The cheap first layer of deduplication, sized for client retries. It is an optimisation, not the
/// guarantee: the fact table deduplicates by message id whatever this layer says.
/// </summary>
/// <remarks>
/// An implementation may miss a duplicate (report it unseen) but must never report an id as seen that was
/// not remembered, because that drops a first delivery the client has been told was accepted. That rules out
/// a Bloom filter, and it is why ids are remembered only after the log has them.
/// </remarks>
public interface IDeduplicationFilter
{
    ValueTask<bool[]> FindSeenAsync(Guid projectId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken);

    ValueTask RememberAsync(Guid projectId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken);
}

/// <summary>Registered schemas, compiled and in memory. Looked up once per event, so it never does I/O.</summary>
public interface ISchemaLookup
{
    /// <summary>The schema for <paramref name="version"/>, or the latest version when it is null.</summary>
    CompiledSchema? Find(Guid projectId, string eventName, int? version);
}
