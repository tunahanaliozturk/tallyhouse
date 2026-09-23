using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Tallyhouse.Load;

/// <summary>
/// Seeds the 100-million-event dataset and times the queries the dashboard runs over it, end to end through
/// the HTTP API, one at a time. Latency, not throughput: the question is how long an analyst waits.
/// </summary>
internal static class QueryBenchmark
{
    public static async Task<int> SeedAsync(Dictionary<string, string> options)
    {
        Endpoints endpoints = Endpoints.FromEnvironment();
        long total = options.Long("events", 100_000_000);
        long users = options.Long("users", 2_000_000);
        int days = (int)options.Long("days", 30);

        // The period ends a week before today, so no event is in the future and none is near the 90-day TTL.
        DateOnly start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-(days + 20));

        // Signups, activations and purchases are a function of the user count; views and feature events make
        // up the rest, four to one.
        long funnelEvents = (long)(users * (1 + 0.45 + (0.45 * 0.30 * 2)));
        long pageViews = (total - funnelEvents) * 4 / 5;
        long features = total - funnelEvents - pageViews;

        using TallyhouseClient api = new(endpoints);
        using ClickHouseHttp clickHouse = new(endpoints);

        BenchProject project = await api.CreateProjectAsync($"benchmark-{DateTime.UtcNow:yyyyMMddHHmm}", new { redactProperties = Array.Empty<string>(), sampleRates = new { } });
        await BenchState.SaveAsync(project, start, days);

        Console.WriteLine($"Project {project.Id}: seeding ~{Stats.N(total)} events for {Stats.N(users)} users over {days} days from {start:yyyy-MM-dd}.");

        string sql;

        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("seed.sql")!)
        using (StreamReader reader = new(stream))
        {
            sql = await reader.ReadToEndAsync();
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        await clickHouse.QueryAsync(
            sql,
            ("project", project.Id.ToString()),
            ("users", users.ToString(CultureInfo.InvariantCulture)),
            ("days", days.ToString(CultureInfo.InvariantCulture)),
            ("start", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00.000"),
            ("pageViews", pageViews.ToString(CultureInfo.InvariantCulture)),
            ("features", features.ToString(CultureInfo.InvariantCulture)));

        Console.WriteLine($"Inserted in {elapsed.Elapsed.TotalSeconds:0} s.");
        Console.WriteLine(await clickHouse.QueryAsync(
            "SELECT event_name, count() FROM events WHERE project_id = {project:UUID} GROUP BY event_name ORDER BY count() DESC FORMAT PrettyCompactMonoBlock",
            ("project", project.Id.ToString())));
        Console.WriteLine(await clickHouse.QueryAsync(
            """
            SELECT table, formatReadableSize(sum(data_compressed_bytes)) AS compressed, formatReadableSize(sum(data_uncompressed_bytes)) AS uncompressed,
                   round(sum(data_uncompressed_bytes) / sum(data_compressed_bytes), 1) AS ratio, sum(rows) AS rows
            FROM system.parts WHERE active AND database = currentDatabase() AND table IN ('events', 'user_event_months')
            GROUP BY table FORMAT PrettyCompactMonoBlock
            """));

        return 0;
    }

    public static async Task<int> RunAsync(Dictionary<string, string> options)
    {
        Endpoints endpoints = Endpoints.FromEnvironment();
        int runs = (int)options.Long("runs", 30);
        int warmup = (int)options.Long("warmup", 3);
        string? output = options.GetValueOrDefault("out");

        (BenchProject project, DateOnly start, int days) = await BenchState.LoadAsync();
        DateOnly end = start.AddDays(days - 1);

        using TallyhouseClient api = new(endpoints, connections: 4);
        using ClickHouseHttp clickHouse = new(endpoints);

        // A bulk load leaves every day's partition in several overlapping parts, which is the worst case for
        // FINAL and for the rollup. A live system reaches the settled state by background merges within
        // minutes of a partition going quiet; --settle gets there now, so both states can be measured.
        if (options.ContainsKey("settle"))
        {
            Stopwatch settling = Stopwatch.StartNew();
            await clickHouse.QueryAsync("OPTIMIZE TABLE events FINAL SETTINGS max_execution_time = 0");
            await clickHouse.QueryAsync("OPTIMIZE TABLE user_event_months FINAL SETTINGS max_execution_time = 0");
            Console.WriteLine($"Merged in {settling.Elapsed.TotalSeconds:0} s.");
        }

        string parts = await clickHouse.QueryAsync("SELECT count() FROM system.parts WHERE active AND database = currentDatabase() AND table = 'events'");
        long rows = await clickHouse.CountAsync(project.Id);

        (string Name, string Kind, object Query)[] suite =
        [
            ("3-step funnel: signup, activate, purchase (7-day window)", "funnel",
                new { steps = new[] { "signup", "activate", "purchase" }, from = start, to = end, windowSeconds = 7 * 86400 }),
            ("4-step funnel starting at page_view (7-day window)", "funnel",
                new { steps = new[] { "page_view", "signup", "activate", "purchase" }, from = start, to = end, windowSeconds = 7 * 86400 }),
            ("30-day retention, signup to page_view (rollup)", "retention",
                new { startEvent = "signup", returnEvent = "page_view", from = start, to = end, days = 30 }),
            ("30-day retention, signup to page_view (raw events)", "retention",
                new { startEvent = "signup", returnEvent = "page_view", from = start, to = end, days = 30, source = "events" }),
            ("Daily page_view counts where path = /pricing", "segment",
                new { @event = "page_view", from = start, to = end, filters = new[] { new { property = "path", @operator = "eq", values = new[] { "/pricing" } } } }),
            ("Daily purchases in EUR or TRY", "segment",
                new { @event = "purchase", from = start, to = end, filters = new[] { new { property = "currency", @operator = "in", values = new[] { "EUR", "TRY" } } } }),
        ];

        StringBuilder report = new();
        report.AppendLine(CultureInfo.InvariantCulture, $"Dataset: {Stats.N(rows)} events, {days} days from {start:yyyy-MM-dd}, in {parts} active parts. {runs} timed runs per query after {warmup} warm-up runs, one query at a time, measured by the client through the HTTP API.");
        report.AppendLine();
        report.AppendLine("| Query | p50 ms | p95 ms | p99 ms | max ms |");
        report.AppendLine("|---|---:|---:|---:|---:|");

        foreach ((string name, string kind, object query) in suite)
        {
            for (int i = 0; i < warmup; i++)
            {
                await api.QueryAsync(project, kind, query);
            }

            List<double> samples = new(runs);

            for (int i = 0; i < runs; i++)
            {
                long started = Stopwatch.GetTimestamp();
                await api.QueryAsync(project, kind, query);
                samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            samples.Sort();
            string line = $"| {name} | {Stats.Ms(Stats.Percentile(samples, 50))} | {Stats.Ms(Stats.Percentile(samples, 95))} | {Stats.Ms(Stats.Percentile(samples, 99))} | {Stats.Ms(samples[^1])} |";
            report.AppendLine(line);
            Console.WriteLine(line);
        }

        // One sample answer, so the report shows the numbers are real and not an empty result returned fast.
        JsonElement funnel = await api.QueryAsync(project, "funnel", suite[0].Query);
        report.AppendLine();
        report.AppendLine("The 3-step funnel's answer on this dataset:");
        report.AppendLine();
        report.AppendLine("| Step | Users | From previous | Median time from start |");
        report.AppendLine("|---|---:|---:|---:|");

        foreach (JsonElement step in funnel.GetProperty("steps").EnumerateArray())
        {
            string median = step.TryGetProperty("medianSecondsFromStart", out JsonElement seconds) && seconds.ValueKind == JsonValueKind.Number ? TimeSpan.FromSeconds(seconds.GetDouble()).ToString(@"d\d\ hh\h\ mm\m", CultureInfo.InvariantCulture) : "";
            report.AppendLine(CultureInfo.InvariantCulture, $"| {step.GetProperty("event").GetString()} | {Stats.N(step.GetProperty("users").GetInt64())} | {step.GetProperty("conversionFromPrevious").GetDouble():P1} | {median} |");
        }

        if (output is not null)
        {
            await File.WriteAllTextAsync(output, report.ToString());
            Console.WriteLine($"Wrote {output}");
        }

        return 0;
    }
}
