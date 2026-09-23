using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Tallyhouse.Core.Ingestion;
using Tallyhouse.Core.Projects;
using Tallyhouse.Core.Schemas;

namespace Tallyhouse.UnitTests;

public sealed class IngestPipelineTests
{
    private readonly Project shop = Samples.Project(new ProjectSettings(["email"], new Dictionary<string, double>()));
    private readonly RecordingLog log = new();
    private readonly MemoryDeduplication deduplication = new();
    private readonly IngestPipeline pipeline;

    public IngestPipelineTests()
    {
        SchemaSet schemas = new SchemaSet().Add(shop.Id, "purchase", 1, Samples.Purchase);
        pipeline = new IngestPipeline(
            new EventNormalizer(schemas, LatenessPolicy.Default),
            deduplication,
            log,
            new FakeTimeProvider(Samples.Now));
    }

    private Task<IngestResult> Ingest(params JsonElement[] events) => pipeline.IngestAsync(shop, events, CancellationToken.None);

    [Fact]
    public async Task Accepted_events_reach_the_log_and_are_then_remembered()
    {
        IngestResult result = await Ingest(Samples.Event("m-1"), Samples.Event("m-2"));

        result.Count(EventStatus.Accepted).ShouldBe(2);
        log.Events.Select(e => e.MessageId).ShouldBe(["m-1", "m-2"]);
        deduplication.Seen.ShouldBe([(shop.Id, "m-1"), (shop.Id, "m-2")], ignoreOrder: true);
    }

    [Fact]
    public async Task A_retry_of_an_acknowledged_event_is_acknowledged_again_and_not_stored_twice()
    {
        await Ingest(Samples.Event("m-1"));
        IngestResult retry = await Ingest(Samples.Event("m-1"));

        retry.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(EventStatus.Duplicate);
        log.Events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_same_message_twice_in_one_batch_is_stored_once()
    {
        IngestResult result = await Ingest(Samples.Event("m-1"), Samples.Event("m-1"));

        result.Outcomes.Select(o => o.Status).ShouldBe([EventStatus.Accepted, EventStatus.Duplicate]);
        log.Events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Nothing_is_remembered_when_the_log_is_unavailable_so_the_retry_is_not_mistaken_for_a_duplicate()
    {
        log.Fail = true;

        await Should.ThrowAsync<EventLogUnavailableException>(() => Ingest(Samples.Event("m-1")));

        deduplication.Seen.ShouldBeEmpty();

        log.Fail = false;
        IngestResult retry = await Ingest(Samples.Event("m-1"));

        retry.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(EventStatus.Accepted);
        log.Events.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_invalid_event_is_quarantined_durably_without_affecting_the_rest_of_the_batch()
    {
        IngestResult result = await Ingest(
            Samples.Event("m-1"),
            Samples.Event("m-2", properties: """{"amount": "free", "currency": "EUR"}"""),
            Samples.Event("m-3"));

        result.Outcomes.Select(o => o.Status).ShouldBe([EventStatus.Accepted, EventStatus.Quarantined, EventStatus.Accepted]);
        result.Outcomes[1].Reason.ShouldBe("property 'amount' must be a finite number");

        QuarantineRecord quarantined = log.Quarantined.ShouldHaveSingleItem();
        quarantined.MessageId.ShouldBe("m-2");
        quarantined.EventName.ShouldBe("purchase");
        quarantined.ReceivedAt.ShouldBe(Samples.Now);
        log.Appends.ShouldBe(1, "valid and quarantined events are made durable together");
    }

    [Fact]
    public async Task Personal_data_is_redacted_in_quarantine_as_well_as_in_the_fact_table()
    {
        await Ingest(
            Samples.Event("m-1", properties: """{"amount": 1, "currency": "EUR", "email": "ada@example.com"}"""),
            Samples.Event("m-2", properties: """{"amount": "x", "currency": "EUR", "email": "ada@example.com"}"""));

        log.Events.ShouldHaveSingleItem().Properties["email"].ShouldBe(CompiledSchema.RedactedMarker);

        string payload = log.Quarantined.ShouldHaveSingleItem().Payload;
        payload.ShouldNotContain("ada@example.com");
        payload.ShouldContain(CompiledSchema.RedactedMarker);
    }

    [Fact]
    public async Task A_batch_of_only_duplicates_does_not_touch_the_log()
    {
        await Ingest(Samples.Event("m-1"));
        int before = log.Appends;

        await Ingest(Samples.Event("m-1"));

        log.Appends.ShouldBe(before);
    }

    [Fact]
    public async Task A_quarantined_event_replays_once_its_schema_accepts_it()
    {
        await Ingest(Samples.Event("m-9", properties: """{"amount": 1, "currency": "EUR", "channel": "web"}"""));
        QuarantineRecord quarantined = log.Quarantined.ShouldHaveSingleItem();

        SchemaSet fixedSchemas = new SchemaSet().Add(shop.Id, "purchase", 1, Samples.Purchase with
        {
            Properties = new Dictionary<string, FieldSpec>(Samples.Purchase.Properties) { ["channel"] = new(FieldType.String) },
        });

        IngestPipeline afterFix = new(
            new EventNormalizer(fixedSchemas, LatenessPolicy.Default),
            deduplication,
            log,
            new FakeTimeProvider(Samples.Now.AddHours(5)));

        EventOutcome outcome = await afterFix.ReplayAsync(shop, Samples.Json(quarantined.Payload), quarantined.ReceivedAt, CancellationToken.None);

        outcome.Status.ShouldBe(EventStatus.Accepted);
        EventRecord replayed = log.Events.ShouldHaveSingleItem();
        replayed.ReceivedAt.ShouldBe(Samples.Now, "lateness is judged as of the original arrival");
        replayed.IsLate.ShouldBeFalse();
    }

    [Fact]
    public async Task A_replay_that_still_fails_is_not_quarantined_a_second_time()
    {
        await Ingest(Samples.Event("m-9", properties: """{"amount": "x", "currency": "EUR"}"""));
        QuarantineRecord quarantined = log.Quarantined.ShouldHaveSingleItem();

        EventOutcome outcome = await pipeline.ReplayAsync(shop, Samples.Json(quarantined.Payload), quarantined.ReceivedAt, CancellationToken.None);

        outcome.Status.ShouldBe(EventStatus.Quarantined);
        log.Quarantined.Count.ShouldBe(1);
    }
}
