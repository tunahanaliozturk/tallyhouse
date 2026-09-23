using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Tallyhouse.Application.Ingestion;
using Tallyhouse.Domain.Events;
using Tallyhouse.Domain.Projects;
using Tallyhouse.Domain.Schemas;
using Tallyhouse.Infrastructure.Kafka;

namespace Tallyhouse.Benchmarks;

/// <summary>
/// The CPU the collector spends on an event before any I/O: parsing the request, validating and normalising
/// each event, and serialising the record for Kafka. Kafka and Redis round trips are measured by the load
/// test instead, because on a real network they dwarf all of this and a micro-benchmark would only measure
/// a fake.
/// </summary>
[MemoryDiagnoser]
public class IngestBenchmarks
{
    private static readonly Project Shop = new(Guid.CreateVersion7(), "shop", new ProjectSettings(["email"], new Dictionary<string, double> { ["page_view"] = 0.5 }));

    private readonly SchemaLookup schemas = new();
    private EventNormalizer normalizer = null!;
    private IngestPipeline pipeline = null!;
    private byte[] batchBody = null!;
    private JsonDocument single = null!;
    private List<JsonElement> batch = null!;
    private JsonDocument batchDocument = null!;
    private EventRecord record = null!;
    private DateTimeOffset now;

    [GlobalSetup]
    public void Setup()
    {
        now = DateTimeOffset.UtcNow;
        schemas.Add(Shop.Id, CompiledSchema.Compile("purchase", 1, new SchemaSpec(new Dictionary<string, FieldSpec>
        {
            ["amount"] = new(FieldType.Number, Required: true),
            ["currency"] = new(FieldType.String, Required: true, Enum: ["USD", "EUR", "TRY"]),
            ["plan"] = new(FieldType.String),
            ["seats"] = new(FieldType.Integer),
            ["email"] = new(FieldType.String),
        })));
        schemas.Add(Shop.Id, CompiledSchema.Compile("page_view", 1, new SchemaSpec(new Dictionary<string, FieldSpec>
        {
            ["path"] = new(FieldType.String, Required: true),
            ["referrer"] = new(FieldType.String),
        })));

        normalizer = new EventNormalizer(schemas, LatenessPolicy.Default);
        pipeline = new IngestPipeline(normalizer, new NoDedup(), new DiscardingLog(), TimeProvider.System);

        single = JsonDocument.Parse(Purchase(0));
        batchBody = Encoding.UTF8.GetBytes($$"""{"events": [{{string.Join(",", Enumerable.Range(0, 500).Select(i => i % 2 == 0 ? Purchase(i) : PageView(i)))}}]}""");
        batchDocument = JsonDocument.Parse(batchBody);
        batch = [.. batchDocument.RootElement.GetProperty("events").EnumerateArray()];
        record = ((Normalized.Accepted)normalizer.Normalize(Shop, single.RootElement, now)).Record;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        single.Dispose();
        batchDocument.Dispose();
    }

    /// <summary>Validation, redaction, identity hashing and sampling for one typical event.</summary>
    [Benchmark]
    public Normalized NormalizeOneEvent() => normalizer.Normalize(Shop, single.RootElement, now);

    /// <summary>Parsing a 500-event request body into a pooled document.</summary>
    [Benchmark]
    public int ParseBatchOf500()
    {
        using JsonDocument document = JsonDocument.Parse(batchBody);
        return document.RootElement.GetProperty("events").GetArrayLength();
    }

    /// <summary>Everything the pipeline does for a 500-event batch, with the log and Redis replaced by no-ops.</summary>
    [Benchmark]
    public Task<IngestResult> PipelineBatchOf500() => pipeline.IngestAsync(Shop, batch, CancellationToken.None);

    /// <summary>The Kafka value for one event.</summary>
    [Benchmark]
    public byte[] SerializeRecordForKafka() => JsonSerializer.SerializeToUtf8Bytes(record, LogJson.Default.EventRecord);

    private string Purchase(int i) => $$"""
        {"messageId": "m-{{i}}", "event": "purchase", "userId": "user-{{i % 997}}", "timestamp": "{{now.AddSeconds(-i):O}}",
         "properties": {"amount": 49.90, "currency": "EUR", "plan": "pro", "seats": 3, "email": "someone@example.com"} }
        """;

    private string PageView(int i) => $$"""
        {"messageId": "m-{{i}}", "event": "page_view", "anonymousId": "anon-{{i % 1301}}", "timestamp": "{{now.AddSeconds(-i):O}}",
         "properties": {"path": "/pricing", "referrer": "https://news.example.com/"} }
        """;

    private sealed class SchemaLookup : ISchemaLookup
    {
        private readonly Dictionary<(Guid, string), CompiledSchema> schemas = [];

        public void Add(Guid projectId, CompiledSchema schema) => schemas[(projectId, schema.EventName)] = schema;

        public CompiledSchema? Find(Guid projectId, string eventName, int? version) => schemas.GetValueOrDefault((projectId, eventName));
    }

    private sealed class NoDedup : IDeduplicationFilter
    {
        public ValueTask<bool[]> FindSeenAsync(Guid projectId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new bool[messageIds.Count]);

        public ValueTask RememberAsync(Guid projectId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class DiscardingLog : IEventLog
    {
        public Task AppendAsync(IReadOnlyList<EventRecord> events, IReadOnlyList<QuarantineRecord> quarantined, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
