using System.Text.Json;
using Tallyhouse.Kernel.Projects;

namespace Tallyhouse.Kernel.Ingestion;

/// <summary>
/// The ingest path: normalise every event, drop the duplicates the fast layer knows about, make the rest
/// durable in the log, and only then remember them as seen. The order is the whole design.
/// </summary>
/// <remarks>
/// <para>Remembering an id before the log has it would be simpler and would lose data: if the append then
/// failed, the client's retry would be answered "duplicate" for an event that was never stored. So ids are
/// remembered after the append, and two concurrent deliveries of one event can both reach the log. That is
/// fine. The fact table deduplicates by message id, and this layer only exists to make that rare.</para>
/// <para>Invalid events go to the log too, on the quarantine topic, so "acknowledged" means the same thing
/// for every event in a batch: it is durable somewhere a person can find it.</para>
/// </remarks>
public sealed class IngestPipeline(
    EventNormalizer normalizer,
    IDeduplicationFilter deduplication,
    IEventLog log,
    TimeProvider time)
{
    public async Task<IngestResult> IngestAsync(Project project, IReadOnlyList<JsonElement> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(events);

        DateTimeOffset receivedAt = Milliseconds(time.GetUtcNow());
        EventOutcome[] outcomes = new EventOutcome[events.Count];
        List<EventRecord> candidates = new(events.Count);
        List<int> candidateIndexes = new(events.Count);
        List<QuarantineRecord> quarantined = [];
        HashSet<string> inBatch = new(StringComparer.Ordinal);

        for (int i = 0; i < events.Count; i++)
        {
            switch (normalizer.Normalize(project, events[i], receivedAt))
            {
                case Normalized.Accepted { Record: var record } when !inBatch.Add(record.MessageId):
                    outcomes[i] = new EventOutcome(EventStatus.Duplicate, record.MessageId, "repeated within the batch");
                    break;

                case Normalized.Accepted { Record: var record }:
                    candidates.Add(record);
                    candidateIndexes.Add(i);
                    break;

                case Normalized.SampledOut sampled:
                    outcomes[i] = new EventOutcome(EventStatus.SampledOut, sampled.MessageId, null);
                    break;

                case Normalized.Rejected rejected:
                    quarantined.Add(Quarantine(project, events[i], rejected, receivedAt));
                    outcomes[i] = new EventOutcome(EventStatus.Quarantined, rejected.MessageId, rejected.Reason);
                    break;
            }
        }

        List<EventRecord> fresh = await DropKnownDuplicatesAsync(project, candidates, candidateIndexes, outcomes, cancellationToken);

        if (fresh.Count > 0 || quarantined.Count > 0)
        {
            await log.AppendAsync(fresh, quarantined, cancellationToken);
        }

        if (fresh.Count > 0)
        {
            // The events are durable whether or not the caller is still listening, so the caller's token no
            // longer applies. Skipping this only means a retry is caught one layer later.
            await deduplication.RememberAsync(project.Id, Ids(fresh), CancellationToken.None);
        }

        Record(outcomes, fresh);
        return new IngestResult(outcomes);
    }

    /// <summary>
    /// Judges a quarantined event again, as of the time it originally arrived. An event that is still invalid
    /// stays where it is; it is not quarantined a second time.
    /// </summary>
    public async Task<EventOutcome> ReplayAsync(Project project, JsonElement payload, DateTimeOffset originallyReceivedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        switch (normalizer.Normalize(project, payload, originallyReceivedAt))
        {
            case Normalized.Rejected rejected:
                return new EventOutcome(EventStatus.Quarantined, rejected.MessageId, rejected.Reason);

            case Normalized.SampledOut sampled:
                return new EventOutcome(EventStatus.SampledOut, sampled.MessageId, null);

            case Normalized.Accepted { Record: var record }:
                bool[] seen = await deduplication.FindSeenAsync(project.Id, [record.MessageId], cancellationToken);

                if (seen[0])
                {
                    return new EventOutcome(EventStatus.Duplicate, record.MessageId, "already accepted");
                }

                await log.AppendAsync([record], [], cancellationToken);
                await deduplication.RememberAsync(project.Id, [record.MessageId], CancellationToken.None);
                return new EventOutcome(EventStatus.Accepted, record.MessageId, null);

            default:
                throw new InvalidOperationException("Unknown normalisation result.");
        }
    }

    private async Task<List<EventRecord>> DropKnownDuplicatesAsync(
        Project project,
        List<EventRecord> candidates,
        List<int> indexes,
        EventOutcome[] outcomes,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        bool[] seen = await deduplication.FindSeenAsync(project.Id, Ids(candidates), cancellationToken);
        List<EventRecord> fresh = new(candidates.Count);

        for (int j = 0; j < candidates.Count; j++)
        {
            EventRecord record = candidates[j];

            if (seen[j])
            {
                outcomes[indexes[j]] = new EventOutcome(EventStatus.Duplicate, record.MessageId, "already accepted");
                continue;
            }

            fresh.Add(record);
            outcomes[indexes[j]] = new EventOutcome(EventStatus.Accepted, record.MessageId, null);
        }

        return fresh;
    }

    private static QuarantineRecord Quarantine(Project project, JsonElement raw, Normalized.Rejected rejected, DateTimeOffset receivedAt)
    {
        string? payload = QuarantinePayload.Write(raw, project.Redact);

        return new QuarantineRecord(
            project.Id,
            Guid.CreateVersion7(receivedAt),
            receivedAt,
            rejected.MessageId,
            rejected.EventName,
            payload is null ? $"{rejected.Reason} (payload larger than {QuarantinePayload.MaxBytes / 1024} KiB was not kept)" : rejected.Reason,
            payload ?? "{}");
    }

    private static string[] Ids(List<EventRecord> records)
    {
        string[] ids = new string[records.Count];

        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = records[i].MessageId;
        }

        return ids;
    }

    private static void Record(EventOutcome[] outcomes, List<EventRecord> fresh)
    {
        Span<long> counts = stackalloc long[4];

        foreach (EventOutcome outcome in outcomes)
        {
            counts[(int)outcome.Status]++;
        }

        for (int status = 0; status < counts.Length; status++)
        {
            if (counts[status] > 0)
            {
                Telemetry.IngestedEvents.Add(counts[status], new KeyValuePair<string, object?>("outcome", StatusTag((EventStatus)status)));
            }
        }

        int late = fresh.Count(record => record.IsLate);

        if (late > 0)
        {
            Telemetry.LateEvents.Add(late);
        }
    }

    public static string StatusTag(EventStatus status) => status switch
    {
        EventStatus.Accepted => "accepted",
        EventStatus.Duplicate => "duplicate",
        EventStatus.SampledOut => "sampled_out",
        _ => "quarantined",
    };

    private static DateTimeOffset Milliseconds(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
}

public sealed class IngestResult(EventOutcome[] outcomes)
{
    public IReadOnlyList<EventOutcome> Outcomes => outcomes;

    public int Count(EventStatus status)
    {
        int count = 0;

        foreach (EventOutcome outcome in outcomes)
        {
            if (outcome.Status == status)
            {
                count++;
            }
        }

        return count;
    }
}
