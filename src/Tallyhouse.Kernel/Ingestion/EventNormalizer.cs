using System.Globalization;
using System.Text.Json;
using Tallyhouse.Kernel.Projects;
using Tallyhouse.Kernel.Schemas;

namespace Tallyhouse.Kernel.Ingestion;

/// <summary>
/// Turns one untrusted event into exactly one of three things: a record to store, a sampled-out
/// acknowledgement, or a rejection with a reason. Pure and deterministic given the schema set and the arrival
/// time, which is what lets a quarantined event be replayed later and judged exactly as if it had just
/// arrived with its original arrival time.
/// </summary>
/// <remarks>
/// Fields are read one at a time from the raw element rather than deserialised into a type. A typed
/// deserialiser fails the whole request on the first malformed timestamp; here a malformed event is one
/// quarantined event and the other 499 in the batch are unaffected.
/// </remarks>
public sealed class EventNormalizer(ISchemaLookup schemas, LatenessPolicy lateness)
{
    public Normalized Normalize(Project project, JsonElement raw, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (raw.ValueKind != JsonValueKind.Object)
        {
            return new Normalized.Rejected(string.Empty, string.Empty, "an event must be a JSON object");
        }

        string? messageId = ReadString(raw, "messageId");

        if (messageId is null || !Names.IsValidMessageId(messageId))
        {
            return new Normalized.Rejected(string.Empty, ReadString(raw, "event") ?? string.Empty,
                $"messageId is required: printable ASCII without spaces, at most {Names.MaxMessageIdLength} characters, generated once per event and reused on retry");
        }

        string? eventName = ReadString(raw, "event");

        if (eventName is null || !Names.IsValidEventName(eventName))
        {
            return new Normalized.Rejected(messageId, string.Empty,
                $"event is required: a name starting with a letter, using letters, digits and _ . : -, at most {Names.MaxEventNameLength} characters");
        }

        string? userId = ReadString(raw, "userId");
        string? anonymousId = ReadString(raw, "anonymousId");

        if (string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(anonymousId))
        {
            return new Normalized.Rejected(messageId, eventName, "userId or anonymousId is required");
        }

        if ((!string.IsNullOrEmpty(userId) && !Names.IsValidUserId(userId))
            || (!string.IsNullOrEmpty(anonymousId) && !Names.IsValidUserId(anonymousId)))
        {
            return new Normalized.Rejected(messageId, eventName,
                $"userId and anonymousId are limited to {Names.MaxUserIdLength} characters without control characters");
        }

        if (!TryReadTimestamp(raw, out DateTimeOffset timestamp))
        {
            return new Normalized.Rejected(messageId, eventName,
                "timestamp is required: ISO 8601 with a UTC offset or Z, for example 2026-09-23T10:15:00.000Z");
        }

        if (!TryReadVersion(raw, out int? version))
        {
            return new Normalized.Rejected(messageId, eventName, "version, when present, must be an integer between 1 and 65535");
        }

        EventTiming timing = lateness.Classify(timestamp, receivedAt);

        switch (timing)
        {
            case EventTiming.TooFarInFuture:
                return new Normalized.Rejected(messageId, eventName,
                    $"timestamp is more than {lateness.MaxClockSkew.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes ahead of the collector clock");

            case EventTiming.BeyondRetention:
                return new Normalized.Rejected(messageId, eventName,
                    $"timestamp is older than the {lateness.Retention.TotalDays.ToString(CultureInfo.InvariantCulture)} day retention period");
        }

        CompiledSchema? schema = schemas.Find(project.Id, eventName, version);

        if (schema is null)
        {
            return new Normalized.Rejected(messageId, eventName, version is null
                ? $"no schema is registered for event '{eventName}'"
                : $"no schema is registered for event '{eventName}' at version {version.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        Dictionary<string, string> properties = new(StringComparer.Ordinal);
        raw.TryGetProperty("properties", out JsonElement rawProperties);

        if (schema.Normalize(rawProperties, project.Redact, properties) is { } violations)
        {
            return new Normalized.Rejected(messageId, eventName, string.Join("; ", violations));
        }

        ulong userKey = UserIdentity.KeyOf(userId, anonymousId);
        ushort bucket = Sampling.BucketOf(userKey);
        ushort threshold = project.SampleThresholdOf(eventName);

        if (!Sampling.Keeps(bucket, threshold))
        {
            return new Normalized.SampledOut(messageId);
        }

        return new Normalized.Accepted(new EventRecord(
            project.Id,
            messageId,
            eventName,
            schema.Version,
            userId ?? string.Empty,
            anonymousId ?? string.Empty,
            userKey,
            timestamp,
            receivedAt,
            timing == EventTiming.Late,
            threshold,
            bucket,
            properties));
    }

    private static string? ReadString(JsonElement raw, string name) =>
        raw.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryReadVersion(JsonElement raw, out int? version)
    {
        version = null;

        if (!raw.TryGetProperty("version", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed) && parsed is >= 1 and <= ushort.MaxValue)
        {
            version = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A timestamp without an offset is ambiguous, and guessing is how a whole region's events land three hours
    /// off. The offset is required, and the instant is truncated to the millisecond the store keeps.
    /// </summary>
    private static bool TryReadTimestamp(JsonElement raw, out DateTimeOffset timestamp)
    {
        timestamp = default;
        string? text = ReadString(raw, "timestamp");

        if (text is null || text.Length is < 20 or > 40 || !HasExplicitOffset(text))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
        {
            return false;
        }

        long ticks = parsed.UtcTicks - (parsed.UtcTicks % TimeSpan.TicksPerMillisecond);
        timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    private static bool HasExplicitOffset(string text) =>
        text[^1] is 'Z' or 'z'
        || (text[^6] is '+' or '-' && text[^3] == ':')
        || text[^5] is '+' or '-';
}

/// <summary>The three things an event can become.</summary>
public abstract record Normalized
{
    private Normalized()
    {
    }

    public sealed record Accepted(EventRecord Record) : Normalized;

    public sealed record SampledOut(string MessageId) : Normalized;

    public sealed record Rejected(string MessageId, string EventName, string Reason) : Normalized;
}
