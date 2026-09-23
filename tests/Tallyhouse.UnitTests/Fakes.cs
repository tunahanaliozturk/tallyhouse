using System.Text.Json;
using Tallyhouse.Application.Ingestion;
using Tallyhouse.Domain.Events;
using Tallyhouse.Domain.Projects;
using Tallyhouse.Domain.Schemas;

namespace Tallyhouse.UnitTests;

internal sealed class SchemaSet : ISchemaLookup
{
    private readonly Dictionary<(Guid, string), SortedDictionary<int, CompiledSchema>> schemas = [];

    public SchemaSet Add(Guid projectId, string eventName, int version, SchemaSpec spec)
    {
        if (!schemas.TryGetValue((projectId, eventName), out SortedDictionary<int, CompiledSchema>? versions))
        {
            schemas[(projectId, eventName)] = versions = [];
        }

        versions[version] = CompiledSchema.Compile(eventName, version, spec);
        return this;
    }

    public CompiledSchema? Find(Guid projectId, string eventName, int? version)
    {
        if (!schemas.TryGetValue((projectId, eventName), out SortedDictionary<int, CompiledSchema>? versions))
        {
            return null;
        }

        if (version is null)
        {
            return versions.Values.Last();
        }

        return versions.GetValueOrDefault(version.Value);
    }
}

internal sealed class RecordingLog : IEventLog
{
    public List<EventRecord> Events { get; } = [];

    public List<QuarantineRecord> Quarantined { get; } = [];

    public bool Fail { get; set; }

    public int Appends { get; private set; }

    public Task AppendAsync(IReadOnlyList<EventRecord> events, IReadOnlyList<QuarantineRecord> quarantined, CancellationToken cancellationToken)
    {
        Appends++;

        if (Fail)
        {
            throw new EventLogUnavailableException("broker down");
        }

        Events.AddRange(events);
        Quarantined.AddRange(quarantined);
        return Task.CompletedTask;
    }
}

internal sealed class MemoryDeduplication : IDeduplicationFilter
{
    public HashSet<(Guid, string)> Seen { get; } = [];

    public ValueTask<bool[]> FindSeenAsync(Guid projectId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken) =>
        ValueTask.FromResult(messageIds.Select(id => Seen.Contains((projectId, id))).ToArray());

    public ValueTask RememberAsync(Guid projectId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken)
    {
        foreach (string id in messageIds)
        {
            Seen.Add((projectId, id));
        }

        return ValueTask.CompletedTask;
    }
}

internal static class Samples
{
    public static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    public static readonly SchemaSpec Purchase = new(new Dictionary<string, FieldSpec>
    {
        ["amount"] = new(FieldType.Number, Required: true),
        ["quantity"] = new(FieldType.Integer),
        ["currency"] = new(FieldType.String, Required: true, Enum: ["USD", "EUR", "TRY"]),
        ["coupon"] = new(FieldType.String),
        ["gift"] = new(FieldType.Boolean),
        ["email"] = new(FieldType.String),
    });

    public static Project Project(ProjectSettings? settings = null) =>
        new(Guid.Parse("0199b6a0-0000-7000-8000-000000000001"), "shop", settings ?? ProjectSettings.Default);

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static JsonElement Event(
        string messageId = "m-1",
        string name = "purchase",
        string? userId = "u-1",
        string? timestamp = "2026-09-23T11:59:00Z",
        string properties = """{"amount": 12.50, "currency": "EUR"}""")
    {
        string user = userId is null ? string.Empty : $"\"userId\": \"{userId}\",";
        string time = timestamp is null ? string.Empty : $"\"timestamp\": \"{timestamp}\",";
        return Json($$"""{"messageId": "{{messageId}}", "event": "{{name}}", {{user}} {{time}} "properties": {{properties}}}""");
    }
}
