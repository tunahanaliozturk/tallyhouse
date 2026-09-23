using System.Text.Json;
using Tallyhouse.Kernel.Ingestion;
using Tallyhouse.Kernel.Projects;
using Tallyhouse.Kernel.Schemas;

namespace Tallyhouse.UnitTests;

public sealed class EventNormalizerTests
{
    private static readonly Project Shop = Samples.Project();

    private static readonly SchemaSet Schemas = new SchemaSet()
        .Add(Shop.Id, "purchase", 1, Samples.Purchase)
        .Add(Shop.Id, "purchase", 2, Samples.Purchase with
        {
            Properties = new Dictionary<string, FieldSpec>(Samples.Purchase.Properties) { ["channel"] = new(FieldType.String) },
        });

    private static readonly EventNormalizer Normalizer = new(Schemas, LatenessPolicy.Default);

    private static Normalized Run(JsonElement raw, Project? project = null) => Normalizer.Normalize(project ?? Shop, raw, Samples.Now);

    private static string Reason(Normalized result) => result.ShouldBeOfType<Normalized.Rejected>().Reason;

    [Fact]
    public void A_valid_event_becomes_a_record_with_everything_the_store_needs()
    {
        EventRecord record = Run(Samples.Event()).ShouldBeOfType<Normalized.Accepted>().Record;

        record.ProjectId.ShouldBe(Shop.Id);
        record.MessageId.ShouldBe("m-1");
        record.EventName.ShouldBe("purchase");
        record.SchemaVersion.ShouldBe(2, "without a version the latest schema applies");
        record.UserId.ShouldBe("u-1");
        record.UserKey.ShouldBe(UserIdentity.KeyOf("u-1", null));
        record.Timestamp.ShouldBe(new DateTimeOffset(2026, 9, 23, 11, 59, 0, TimeSpan.Zero));
        record.ReceivedAt.ShouldBe(Samples.Now);
        record.IsLate.ShouldBeFalse();
        record.SampleThreshold.ShouldBe(Sampling.Buckets);
        record.Properties["amount"].ShouldBe("12.5");
    }

    [Fact]
    public void A_pinned_version_is_validated_against_that_version()
    {
        JsonElement raw = Samples.Json("""
            {"messageId": "m-1", "event": "purchase", "version": 1, "userId": "u-1", "timestamp": "2026-09-23T11:59:00Z",
             "properties": {"amount": 1, "currency": "EUR", "channel": "web"}}
            """);

        Reason(Run(raw)).ShouldContain("'channel' is not declared in purchase@1");
    }

    [Theory]
    [InlineData("""{"event": "purchase", "userId": "u", "timestamp": "2026-09-23T11:59:00Z"}""", "messageId is required")]
    [InlineData("""{"messageId": "has space", "event": "purchase", "userId": "u", "timestamp": "2026-09-23T11:59:00Z"}""", "messageId is required")]
    [InlineData("""{"messageId": "m", "event": "9lives", "userId": "u", "timestamp": "2026-09-23T11:59:00Z"}""", "event is required")]
    [InlineData("""{"messageId": "m", "event": "purchase", "timestamp": "2026-09-23T11:59:00Z"}""", "userId or anonymousId is required")]
    [InlineData("""{"messageId": "m", "event": "purchase", "userId": "u"}""", "timestamp is required")]
    [InlineData("""{"messageId": "m", "event": "purchase", "userId": "u", "timestamp": "yesterday"}""", "timestamp is required")]
    [InlineData("""{"messageId": "m", "event": "purchase", "userId": "u", "timestamp": 1790000000000}""", "timestamp is required")]
    [InlineData("""{"messageId": "m", "event": "purchase", "userId": "u", "timestamp": "2026-09-23T11:59:00Z", "version": "2"}""", "version")]
    [InlineData("""{"messageId": "m", "event": "purchase", "userId": "u", "timestamp": "2026-09-23T11:59:00Z", "version": 0}""", "version")]
    [InlineData("""{"messageId": "m", "event": "refund", "userId": "u", "timestamp": "2026-09-23T11:59:00Z"}""", "no schema is registered for event 'refund'")]
    [InlineData("""{"messageId": "m", "event": "purchase", "version": 7, "userId": "u", "timestamp": "2026-09-23T11:59:00Z"}""", "at version 7")]
    [InlineData("[1, 2]", "must be a JSON object")]
    public void Every_envelope_rule_names_what_is_wrong(string json, string expected) =>
        Reason(Run(Samples.Json(json))).ShouldContain(expected);

    [Fact]
    public void A_timestamp_without_an_offset_is_refused_rather_than_guessed() =>
        Reason(Run(Samples.Event(timestamp: "2026-09-23T11:59:00.000"))).ShouldContain("UTC offset");

    [Theory]
    [InlineData("2026-09-23T14:59:00+03:00")]
    [InlineData("2026-09-23T14:59:00+0300")]
    [InlineData("2026-09-23T06:59:00.000-05:00")]
    [InlineData("2026-09-23T11:59:00.000z")]
    public void Any_explicit_offset_is_understood(string timestamp) =>
        Run(Samples.Event(timestamp: timestamp)).ShouldBeOfType<Normalized.Accepted>()
            .Record.Timestamp.ShouldBe(new DateTimeOffset(2026, 9, 23, 11, 59, 0, TimeSpan.Zero));

    [Fact]
    public void Timestamps_are_truncated_to_the_millisecond_the_store_keeps() =>
        Run(Samples.Event(timestamp: "2026-09-23T11:59:00.1239999Z")).ShouldBeOfType<Normalized.Accepted>()
            .Record.Timestamp.Ticks.ShouldBe(new DateTimeOffset(2026, 9, 23, 11, 59, 0, 123, TimeSpan.Zero).Ticks);

    [Fact]
    public void An_event_older_than_the_watermark_is_kept_and_flagged_late() =>
        Run(Samples.Event(timestamp: "2026-09-23T09:30:00Z")).ShouldBeOfType<Normalized.Accepted>().Record.IsLate.ShouldBeTrue();

    [Fact]
    public void An_event_from_the_future_is_quarantined() =>
        Reason(Run(Samples.Event(timestamp: "2026-09-23T12:10:00Z"))).ShouldContain("ahead of the collector clock");

    [Fact]
    public void An_event_older_than_retention_is_quarantined_instead_of_being_deleted_on_arrival() =>
        Reason(Run(Samples.Event(timestamp: "2026-05-01T00:00:00Z"))).ShouldContain("retention");

    [Fact]
    public void Schema_violations_are_joined_into_one_reason() =>
        Reason(Run(Samples.Event(properties: """{"amount": "x"}"""))).ShouldBe(
            "property 'amount' must be a finite number; required property 'currency' is missing");

    [Fact]
    public void Anonymous_visitors_are_counted_as_their_own_users()
    {
        JsonElement raw = Samples.Json("""
            {"messageId": "m", "event": "purchase", "anonymousId": "u-1", "timestamp": "2026-09-23T11:59:00Z",
             "properties": {"amount": 1, "currency": "EUR"}}
            """);

        EventRecord record = Run(raw).ShouldBeOfType<Normalized.Accepted>().Record;

        record.AnonymousId.ShouldBe("u-1");
        record.UserKey.ShouldNotBe(UserIdentity.KeyOf("u-1", null), "a user and a visitor sharing an id string are different people");
    }

    [Fact]
    public void Sampling_keeps_or_drops_a_user_consistently_across_their_events()
    {
        Project sampled = new(Shop.Id, "shop", new ProjectSettings([], new Dictionary<string, double> { ["purchase"] = 0.5 }));

        foreach (int user in Enumerable.Range(0, 200))
        {
            Normalized first = Run(Samples.Event(messageId: $"a{user}", userId: $"user-{user}"), sampled);
            Normalized second = Run(Samples.Event(messageId: $"b{user}", userId: $"user-{user}"), sampled);

            second.GetType().ShouldBe(first.GetType());
        }
    }

    [Fact]
    public void A_kept_sampled_event_carries_the_threshold_it_was_sampled_at()
    {
        Project sampled = new(Shop.Id, "shop", new ProjectSettings([], new Dictionary<string, double> { ["purchase"] = 0.5 }));

        EventRecord kept = Enumerable.Range(0, 50)
            .Select(user => Run(Samples.Event(userId: $"user-{user}"), sampled))
            .OfType<Normalized.Accepted>()
            .First().Record;

        kept.SampleThreshold.ShouldBe((ushort)5_000);
        kept.SampleBucket.ShouldBeLessThan((ushort)5_000);
    }
}

public sealed class LatenessPolicyTests
{
    private static readonly LatenessPolicy Policy = LatenessPolicy.Default;
    private static readonly DateTimeOffset Now = Samples.Now;

    [Fact]
    public void The_watermark_boundary_itself_is_on_time() =>
        Policy.Classify(Now - Policy.Watermark, Now).ShouldBe(EventTiming.OnTime);

    [Fact]
    public void One_millisecond_past_the_watermark_is_late() =>
        Policy.Classify(Now - Policy.Watermark - TimeSpan.FromMilliseconds(1), Now).ShouldBe(EventTiming.Late);

    [Fact]
    public void Clock_skew_within_the_allowance_is_on_time() =>
        Policy.Classify(Now + Policy.MaxClockSkew, Now).ShouldBe(EventTiming.OnTime);

    [Fact]
    public void Beyond_the_skew_allowance_is_the_future() =>
        Policy.Classify(Now + Policy.MaxClockSkew + TimeSpan.FromMilliseconds(1), Now).ShouldBe(EventTiming.TooFarInFuture);

    [Fact]
    public void Beyond_retention_is_its_own_outcome() =>
        Policy.Classify(Now - Policy.Retention - TimeSpan.FromMilliseconds(1), Now).ShouldBe(EventTiming.BeyondRetention);
}

public sealed class SamplingTests
{
    [Theory]
    [InlineData(1.0, true)]
    [InlineData(0.1, true)]
    [InlineData(0.0001, true)]
    [InlineData(0.00015, false)]
    [InlineData(0.0, false)]
    [InlineData(1.5, false)]
    [InlineData(-0.1, false)]
    public void Rates_are_fractions_in_whole_buckets(double rate, bool valid) =>
        Sampling.IsValidRate(rate).ShouldBe(valid);

    [Fact]
    public void Kept_populations_nest_so_a_query_can_scale_by_one_factor()
    {
        ushort tenPercent = Sampling.ThresholdOf(0.1);
        ushort half = Sampling.ThresholdOf(0.5);

        foreach (int user in Enumerable.Range(0, 20_000))
        {
            ushort bucket = Sampling.BucketOf(UserIdentity.KeyOf($"user-{user}", null));

            if (Sampling.Keeps(bucket, tenPercent))
            {
                Sampling.Keeps(bucket, half).ShouldBeTrue();
            }
        }
    }

    [Fact]
    public void The_kept_fraction_matches_the_rate()
    {
        const int users = 200_000;
        ushort threshold = Sampling.ThresholdOf(0.1);

        int kept = Enumerable.Range(0, users)
            .Count(user => Sampling.Keeps(Sampling.BucketOf(UserIdentity.KeyOf($"user-{user}", null)), threshold));

        // Binomial standard deviation at n = 200k, p = 0.1 is about 134 users; four of them is 0.27%.
        ((double)kept / users).ShouldBe(0.1, 0.0027);
    }

    [Fact]
    public void The_weight_is_the_inverse_of_the_kept_fraction() =>
        Sampling.WeightOf(Sampling.ThresholdOf(0.1)).ShouldBe(10.0);
}
