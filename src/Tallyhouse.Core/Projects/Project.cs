using System.Collections.Frozen;
using Tallyhouse.Core.Ingestion;

namespace Tallyhouse.Core.Projects;

/// <summary>
/// A tenant. Everything stored, deduplicated or queried is scoped to one, and its settings decide what the
/// ingest path redacts and samples.
/// </summary>
public sealed class Project
{
    private readonly FrozenDictionary<string, ushort> sampleThresholds;

    public Project(Guid id, string name, ProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Id = id;
        Name = name;
        Settings = settings;
        Redact = settings.RedactProperties.ToFrozenSet(StringComparer.Ordinal);
        sampleThresholds = settings.SampleRates.ToFrozenDictionary(
            pair => pair.Key,
            pair => Sampling.ThresholdOf(pair.Value),
            StringComparer.Ordinal);
    }

    public Guid Id { get; }

    public string Name { get; }

    public ProjectSettings Settings { get; }

    public IReadOnlySet<string> Redact { get; }

    /// <summary>How many of the <see cref="Sampling.Buckets"/> user buckets keep this event type.</summary>
    public ushort SampleThresholdOf(string eventName) =>
        sampleThresholds.TryGetValue(eventName, out ushort threshold) ? threshold : Sampling.Buckets;
}

/// <param name="RedactProperties">Property names whose values are replaced before anything is stored.</param>
/// <param name="SampleRates">Event name to the fraction of users whose events of that type are kept.</param>
public sealed record ProjectSettings(IReadOnlyList<string> RedactProperties, IReadOnlyDictionary<string, double> SampleRates)
{
    public const int MaxRedactedProperties = 64;
    public const int MaxSampledEvents = 256;

    public static ProjectSettings Default { get; } = new([], new Dictionary<string, double>());

    public IReadOnlyList<string> Problems()
    {
        List<string> problems = [];

        if (RedactProperties is null || SampleRates is null)
        {
            problems.Add("redactProperties and sampleRates are both required (use empty collections for none)");
            return problems;
        }

        if (RedactProperties.Count > MaxRedactedProperties)
        {
            problems.Add($"at most {MaxRedactedProperties} properties may be redacted");
        }

        problems.AddRange(RedactProperties
            .Where(name => name is null || !Names.IsValidPropertyName(name))
            .Select(name => $"'{name}' is not a valid property name"));

        if (SampleRates.Count > MaxSampledEvents)
        {
            problems.Add($"at most {MaxSampledEvents} event types may be sampled");
        }

        foreach ((string eventName, double rate) in SampleRates)
        {
            if (!Names.IsValidEventName(eventName))
            {
                problems.Add($"'{eventName}' is not a valid event name");
            }

            if (!Sampling.IsValidRate(rate))
            {
                problems.Add($"sample rate for '{eventName}' must be in (0, 1] in steps of 1/{Sampling.Buckets}");
            }
        }

        return problems;
    }
}
