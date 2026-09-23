using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.ADO.Readers;
using ClickHouse.Driver.Utility;
using Tallyhouse.Application.Ingestion;
using Tallyhouse.Domain.Events;

namespace Tallyhouse.IntegrationTests;

public sealed class QueryTests(TestRig rig)
{
    private static readonly string[] Steps = ["page_view", "signup", "activate", "purchase"];
    private static readonly DateOnly Day = new(2026, 9, 1);
    private static readonly DateTimeOffset Midnight = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Funnel_levels_agree_with_windowFunnel_and_times_agree_with_a_reference_model()
    {
        TestProject project = await rig.NewProjectAsync();
        TimeSpan window = TimeSpan.FromHours(4);

        // Random users, random step sequences, timestamps spread across the day and into the next morning so
        // that some chains start inside the range and finish outside it, and some first steps fall outside
        // the range altogether and must not start a chain.
        Random random = new(20260923);
        List<EventRecord> events = [];
        Dictionary<string, List<(long Ms, int Step)>> byUser = [];

        for (int u = 0; u < 400; u++)
        {
            string user = $"user-{u}";
            HashSet<long> used = [];
            List<(long, int)> own = [];

            for (int e = random.Next(1, 30); e > 0; e--)
            {
                long offset;

                do
                {
                    offset = random.NextInt64(0, (long)TimeSpan.FromHours(27).TotalMilliseconds);
                }
                while (!used.Add(offset));

                int step = random.Next(Steps.Length + 1);
                DateTimeOffset at = Midnight.AddMilliseconds(offset);

                // One in five events is something no funnel asks about.
                events.Add(Facts.Event(project.Id, step == Steps.Length ? "noise" : Steps[step], user, at));

                if (step < Steps.Length)
                {
                    own.Add((at.ToUnixTimeMilliseconds(), step));
                }
            }

            byUser[user] = own;
        }

        await rig.WriteAsync(events);

        using HttpClient reader = rig.Client(project.ReadKey);
        JsonElement funnel = await PostAsync(reader, "/v1/queries/funnel", new { steps = Steps, from = Day, to = Day, windowSeconds = (long)window.TotalSeconds });

        long toMs = Midnight.AddDays(1).ToUnixTimeMilliseconds();
        List<ReferenceFunnel.Result> expected = [.. byUser.Values.Select(own => ReferenceFunnel.Run(own, Steps.Length, toMs, (long)window.TotalMilliseconds))];
        long[] native = await NativeWindowFunnelAsync(project.Id, (long)window.TotalSeconds);

        JsonElement[] steps = [.. funnel.GetProperty("steps").EnumerateArray()];
        steps.Length.ShouldBe(Steps.Length);

        for (int level = 1; level <= Steps.Length; level++)
        {
            long users = steps[level - 1].GetProperty("users").GetInt64();

            users.ShouldBe(native[level - 1], $"step {level} against windowFunnel");
            users.ShouldBe(expected.Count(result => result.Level >= level), $"step {level} against the reference model");

            if (level > 1 && users > 0)
            {
                double median = steps[level - 1].GetProperty("medianSecondsFromStart").GetDouble();
                median.ShouldBe(ReferenceFunnel.MedianSeconds(expected, level), 0.001, $"median time to step {level}");
            }
        }

        steps[0].GetProperty("users").GetInt64().ShouldBeGreaterThan(100, "the data has to exercise the funnel for the comparison to mean anything");
        steps[^1].GetProperty("users").GetInt64().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_duplicate_that_has_not_been_merged_away_changes_no_answer()
    {
        TestProject project = await rig.NewProjectAsync();
        List<EventRecord> events = [];

        for (int u = 0; u < 50; u++)
        {
            DateTimeOffset start = Midnight.AddMinutes(u);
            events.Add(Facts.Event(project.Id, "page_view", $"u{u}", start, new() { ["path"] = "/" }));
            events.Add(Facts.Event(project.Id, "signup", $"u{u}", start.AddMinutes(5), new() { ["plan"] = u % 2 == 0 ? "pro" : "free" }));
            events.Add(Facts.Event(project.Id, "purchase", $"u{u}", start.AddMinutes(9), new() { ["amount"] = (u * 10).ToString(CultureInfo.InvariantCulture), ["currency"] = u % 3 == 0 ? "EUR" : "USD" }));
        }

        using HttpClient reader = rig.Client(project.ReadKey);
        object funnelQuery = new { steps = new[] { "page_view", "signup", "purchase" }, from = Day, to = Day, windowSeconds = 3600 };
        object segmentQuery = new { @event = "purchase", from = Day, to = Day, filters = new[] { new { property = "currency", @operator = "eq", values = new[] { "EUR" } } } };
        object bigSpenders = new { @event = "purchase", from = Day, to = Day, filters = new[] { new { property = "amount", @operator = "gt", values = new[] { "250" } } } };

        // With merges running, the duplicates could be collapsed before the queries see them and the test would
        // prove nothing. Stopped, both copies are guaranteed to be physically present.
        await rig.ClickHouse.ExecuteNonQueryAsync("SYSTEM STOP MERGES events");

        try
        {
            await rig.WriteAsync(events);
            JsonElement funnelOnce = await PostAsync(reader, "/v1/queries/funnel", funnelQuery);
            JsonElement segmentOnce = await PostAsync(reader, "/v1/queries/segment", segmentQuery);

            // The loader delivering the same events again, as it does after a crash between insert and offset
            // commit. Reversed, because an identical block would be dropped by ClickHouse's insert
            // deduplication before it ever became a duplicate row, and that is not the case under test.
            await rig.WriteAsync([.. events.AsEnumerable().Reverse()]);
            (await rig.CountAsync(project.Id, final: false)).ShouldBe(300, "both copies are physically there until a merge");

            JsonElement funnelTwice = await PostAsync(reader, "/v1/queries/funnel", funnelQuery);
            JsonElement segmentTwice = await PostAsync(reader, "/v1/queries/segment", segmentQuery);
            JsonElement spenders = await PostAsync(reader, "/v1/queries/segment", bigSpenders);

            funnelTwice.GetProperty("steps").ToString().ShouldBe(funnelOnce.GetProperty("steps").ToString());
            segmentTwice.GetProperty("days").ToString().ShouldBe(segmentOnce.GetProperty("days").ToString());

            segmentTwice.GetProperty("days")[0].GetProperty("events").GetInt64().ShouldBe(17, "users 0, 3, ..., 48 paid in EUR");
            segmentTwice.GetProperty("days")[0].GetProperty("users").GetInt64().ShouldBe(17);
            spenders.GetProperty("days")[0].GetProperty("events").GetInt64().ShouldBe(24, "amounts 260 to 490");
            funnelTwice.GetProperty("steps")[2].GetProperty("users").GetInt64().ShouldBe(50);
            funnelTwice.GetProperty("steps")[2].GetProperty("medianSecondsFromStart").GetDouble().ShouldBe(540);
        }
        finally
        {
            await rig.ClickHouse.ExecuteNonQueryAsync("SYSTEM START MERGES events");
        }
    }

    [Fact]
    public async Task Retention_counts_each_user_in_one_cohort_and_the_rollup_agrees_with_the_raw_events()
    {
        TestProject project = await rig.NewProjectAsync();

        List<EventRecord> events =
        [
            Facts.Event(project.Id, "signup", "a", Midnight.AddHours(9)),
            Facts.Event(project.Id, "signup", "b", Midnight.AddHours(10)),
            Facts.Event(project.Id, "signup", "c", Midnight.AddHours(11)),
            Facts.Event(project.Id, "page_view", "a", Midnight.AddDays(1).AddHours(8)),
            Facts.Event(project.Id, "page_view", "b", Midnight.AddDays(1).AddHours(9)),
            Facts.Event(project.Id, "page_view", "b", Midnight.AddDays(1).AddHours(12)),
            Facts.Event(project.Id, "page_view", "b", Midnight.AddDays(3).AddHours(9)),

            // d signs up on day two, and again on day three: still one cohort, the first.
            Facts.Event(project.Id, "signup", "d", Midnight.AddDays(1).AddHours(7)),
            Facts.Event(project.Id, "signup", "d", Midnight.AddDays(2).AddHours(7)),
            Facts.Event(project.Id, "page_view", "d", Midnight.AddDays(2).AddHours(8)),

            // Late events never enter the rollup, so they must not enter the raw path either.
            Facts.Event(project.Id, "page_view", "c", Midnight.AddDays(2), late: true),
        ];

        await rig.WriteAsync(events);

        using HttpClient reader = rig.Client(project.ReadKey);
        object query = new { startEvent = "signup", returnEvent = "page_view", from = Day, to = Day.AddDays(1), days = 3 };

        JsonElement rollup = await PostAsync(reader, "/v1/queries/retention", query);
        JsonElement raw = await PostAsync(reader, "/v1/queries/retention", new { startEvent = "signup", returnEvent = "page_view", from = Day, to = Day.AddDays(1), days = 3, source = "events" });

        raw.GetProperty("cohorts").ToString().ShouldBe(rollup.GetProperty("cohorts").ToString());

        JsonElement[] cohorts = [.. rollup.GetProperty("cohorts").EnumerateArray()];
        cohorts.Length.ShouldBe(2);

        cohorts[0].GetProperty("day").GetString().ShouldBe("2026-09-01");
        cohorts[0].GetProperty("users").GetInt64().ShouldBe(3);
        cohorts[0].GetProperty("retained").EnumerateArray().Select(v => v.GetInt64()).ShouldBe([0L, 2L, 0L, 1L]);

        cohorts[1].GetProperty("day").GetString().ShouldBe("2026-09-02");
        cohorts[1].GetProperty("users").GetInt64().ShouldBe(1);
        cohorts[1].GetProperty("retained").EnumerateArray().Select(v => v.GetInt64()).ShouldBe([0L, 1L, 0L, 0L]);
    }

    [Fact]
    public async Task A_late_event_inside_the_watermark_merges_two_sessions_when_the_day_is_recomputed()
    {
        TestProject project = await rig.NewProjectAsync();
        DateTimeOffset nine = Midnight.AddHours(9);

        await rig.WriteAsync(
        [
            Facts.Event(project.Id, "page_view", "ada", nine),
            Facts.Event(project.Id, "page_view", "ada", nine.AddMinutes(10)),
            Facts.Event(project.Id, "page_view", "ada", nine.AddMinutes(50)),
            Facts.Event(project.Id, "page_view", "bob", nine.AddMinutes(5)),

            // Past the watermark: stored, but it must not bridge anything.
            Facts.Event(project.Id, "page_view", "bob", nine.AddMinutes(33), late: true),
            Facts.Event(project.Id, "page_view", "bob", nine.AddMinutes(60)),
        ]);

        await rig.Sessionizer.RecomputeAsync(project.Id, Day, CancellationToken.None);
        (await SessionsAsync(project.Id)).ShouldBe(["ada:2:600000", "ada:1:0", "bob:1:0", "bob:1:0"], ignoreOrder: true);

        // Arrives within the watermark and sits 20 minutes from both of ada's sessions.
        await rig.WriteAsync([Facts.Event(project.Id, "page_view", "ada", nine.AddMinutes(30))]);
        await rig.Sessionizer.RecomputeAsync(project.Id, Day, CancellationToken.None);

        (await SessionsAsync(project.Id)).ShouldBe(["ada:4:3000000", "bob:1:0", "bob:1:0"], ignoreOrder: true);

        using HttpClient reader = rig.Client(project.ReadKey);
        JsonElement sessions = await PostAsync(reader, "/v1/queries/sessions", new { from = Day, to = Day });
        JsonElement day = sessions.GetProperty("days")[0];
        day.GetProperty("sessions").GetInt64().ShouldBe(3);
        day.GetProperty("users").GetInt64().ShouldBe(2);
    }

    [Fact]
    public async Task The_sessionizer_finds_the_days_the_loader_marked()
    {
        TestProject project = await rig.NewProjectAsync();
        await rig.WriteAsync([Facts.Event(project.Id, "page_view", "ada", Midnight.AddDays(2).AddHours(1))]);

        // Markers are read up to five seconds behind the ClickHouse clock, so the one just written is picked up
        // on a pass that starts after that.
        await TestRig.WaitUntilAsync(async () =>
        {
            await rig.Sessionizer.RunOnceAsync(CancellationToken.None);
            return (await SessionsAsync(project.Id)).Count == 1;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Queries_outside_their_bounds_are_refused_with_the_reason()
    {
        TestProject project = await rig.NewProjectAsync();
        using HttpClient reader = rig.Client(project.ReadKey);

        HttpResponseMessage response = await reader.PostAsJsonAsync(
            "/v1/queries/funnel",
            new { steps = new[] { "signup", "signup" }, from = Day, to = Day.AddDays(400), windowSeconds = 0 },
            TestRig.Json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("each funnel step must be a different event");
        body.ShouldContain("windowSeconds");
        body.ShouldContain("at most 366 days");
    }

    private async Task<long[]> NativeWindowFunnelAsync(Guid projectId, long windowSeconds)
    {
        ClickHouseParameterCollection parameters = new();
        parameters.AddParameter("project", projectId);
        parameters.AddParameter("from", Midnight.UtcDateTime);
        parameters.AddParameter("to", Midnight.AddDays(1).UtcDateTime);
        parameters.AddParameter("scanTo", Midnight.AddDays(1).AddSeconds(windowSeconds).UtcDateTime);
        parameters.AddParameter("window", (ulong)(windowSeconds * 1000));

        using ClickHouseDataReader reader = await rig.ClickHouse.ExecuteReaderAsync(
            """
            SELECT level, count() FROM
            (
                SELECT user_key,
                       windowFunnel({window:UInt64})(toUInt64(toUnixTimestamp64Milli(ts)),
                           event_name = 'page_view' AND ts < {to:DateTime64(3, 'UTC')},
                           event_name = 'signup',
                           event_name = 'activate',
                           event_name = 'purchase') AS level
                FROM events
                WHERE project_id = {project:UUID}
                  AND ts >= {from:DateTime64(3, 'UTC')} AND ts < {scanTo:DateTime64(3, 'UTC')}
                  AND event_name IN ('page_view', 'signup', 'activate', 'purchase')
                GROUP BY user_key
            )
            GROUP BY level
            """,
            parameters);

        long[] exactly = new long[Steps.Length + 1];

        while (reader.Read())
        {
            exactly[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        // windowFunnel reports how far each user got; the API reports how many got at least that far.
        return [.. Enumerable.Range(1, Steps.Length).Select(level => exactly[level..].Sum())];
    }

    private async Task<List<string>> SessionsAsync(Guid projectId)
    {
        ClickHouseParameterCollection parameters = new();
        parameters.AddParameter("project", projectId);

        using ClickHouseDataReader reader = await rig.ClickHouse.ExecuteReaderAsync(
            "SELECT user_id, event_count, dateDiff('millisecond', session_start, session_end) FROM sessions WHERE project_id = {project:UUID}",
            parameters);

        List<string> sessions = [];

        while (reader.Read())
        {
            sessions.Add(string.Create(CultureInfo.InvariantCulture, $"{reader.GetString(0)}:{reader.GetValue(1)}:{reader.GetValue(2)}"));
        }

        return sessions;
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, object body)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(path, body, TestRig.Json);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestRig.Json);
    }
}

/// <summary>
/// The funnel algorithm written plainly, as the oracle for the SQL. Kept deliberately naive: it is the
/// definition, and the SQL is the optimisation that has to agree with it.
/// </summary>
internal static class ReferenceFunnel
{
    public sealed record Result(int Level, long[] Start, long[] Reached);

    public static Result Run(IEnumerable<(long Ms, int Step)> events, int steps, long toMs, long windowMs)
    {
        long[] start = new long[steps];
        long[] reached = new long[steps];

        foreach ((long ms, int step) in events.OrderBy(e => e.Ms).ThenBy(e => e.Step))
        {
            if (step == 0)
            {
                if (ms < toMs)
                {
                    start[0] = ms;
                    reached[0] = ms;
                }
            }
            else if (start[step - 1] > 0 && ms <= start[step - 1] + windowMs)
            {
                start[step] = start[step - 1];
                reached[step] = ms;
            }
        }

        return new Result(start.Count(s => s > 0), start, reached);
    }

    /// <summary>ClickHouse's quantileExact(0.5): the element at index floor(n / 2) of the sorted values.</summary>
    public static double MedianSeconds(IEnumerable<Result> results, int level)
    {
        long[] durations = [.. results
            .Where(result => result.Level >= level)
            .Select(result => result.Reached[level - 1] - result.Start[level - 1])
            .Order()];

        return durations[durations.Length / 2] / 1000.0;
    }
}
