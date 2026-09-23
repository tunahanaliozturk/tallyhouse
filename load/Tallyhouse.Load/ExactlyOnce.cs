using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Tallyhouse.Load;

/// <summary>
/// The counting proof. Generates events with a known answer, delivers a fifth of them twice in the ways
/// duplicates really arrive, and checks that what the queries report is the known answer exactly.
/// </summary>
/// <remarks>
/// Half of the duplicates travel in a second request sent at the same moment as the first. Both reach the
/// collector before either has been remembered as seen, so both get past the Redis layer and into Kafka:
/// only the fact table's deduplication stands between them and a double count. The other half are
/// retries sent later, the case the Redis layer exists for. With <c>--restart-loader</c> the loader is
/// restarted halfway through, so batches it wrote but had not committed are delivered to ClickHouse again.
/// </remarks>
internal static class ExactlyOnce
{
    private static readonly string[] FunnelSteps = ["signup", "activate", "purchase"];

    public static async Task<int> RunAsync(Dictionary<string, string> options)
    {
        int total = (int)options.Long("events", 1_000_000);
        double duplicateRate = options.Double("duplicates", 0.2);
        bool restartLoader = options.ContainsKey("restart-loader");
        const int batchSize = 400;

        Endpoints endpoints = Endpoints.FromEnvironment();
        using TallyhouseClient api = new(endpoints, connections: 64);
        using ClickHouseHttp clickHouse = new(endpoints);

        BenchProject project = await api.CreateProjectAsync($"exactly-once-{DateTime.UtcNow:yyyyMMddHHmm}", new { redactProperties = Array.Empty<string>(), sampleRates = new { } });

        (List<Event> events, Dictionary<string, long> truthEvents, long[] truthFunnel) = Generate(total);
        Random random = new(7);

        // Arrival order is not event order.
        Shuffle(events, random);

        List<Event>[] batches = [.. events.Chunk(batchSize).Select(chunk => chunk.ToList())];
        List<Event>[] twins = [.. batches.Select(_ => new List<Event>())];
        int duplicates = (int)(total * duplicateRate);

        for (int d = 0; d < duplicates; d++)
        {
            int position = random.Next(events.Count);
            int home = position / batchSize;
            Event copy = events[position];

            int later = Math.Min(batches.Length - 1, home + 1 + random.Next(20));

            // A request holds at most 500 events; a later batch that is already full takes no more.
            if (d % 2 == 0 || batches[later].Count >= 500)
            {
                twins[home].Add(copy);
            }
            else
            {
                batches[later].Add(copy);
            }
        }

        Console.WriteLine($"Project {project.Id}: {total:N0} events, {duplicates:N0} delivered twice, {batches.Length:N0} batches.");

        long acknowledged = 0, duplicatesSeen = 0, restarted = 0;
        Stopwatch clock = Stopwatch.StartNew();

        await Parallel.ForEachAsync(Enumerable.Range(0, batches.Length), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (index, cancellationToken) =>
        {
            if (restartLoader && index == batches.Length / 2 && Interlocked.Exchange(ref restarted, 1) == 0)
            {
                Console.WriteLine("Restarting the loader mid-run.");
                using Process process = Process.Start(new ProcessStartInfo("docker", "compose restart loader") { RedirectStandardOutput = true, RedirectStandardError = true })!;
                await process.WaitForExitAsync(cancellationToken);
            }

            // The twin goes out at the same moment as its batch: a race the Redis layer cannot win.
            Task<Delivery>[] sends = twins[index].Count > 0
                ? [SendUntilAcceptedAsync(api, project, batches[index]), SendUntilAcceptedAsync(api, project, twins[index])]
                : [SendUntilAcceptedAsync(api, project, batches[index])];

            foreach (Delivery delivery in await Task.WhenAll(sends))
            {
                Interlocked.Add(ref acknowledged, delivery.Accepted);
                Interlocked.Add(ref duplicatesSeen, delivery.Duplicates);
            }
        });

        Console.WriteLine($"Delivered in {clock.Elapsed.TotalSeconds:0} s: {acknowledged:N0} acknowledged as new, {duplicatesSeen:N0} recognised as duplicates by the collector.");

        Stopwatch stalled = Stopwatch.StartNew();
        long stored = 0, previous = -1;

        while (stored < total && stalled.Elapsed < TimeSpan.FromSeconds(120))
        {
            await Task.Delay(1000);
            stored = await clickHouse.CountAsync(project.Id);

            if (stored != previous)
            {
                previous = stored;
                stalled.Restart();
            }
        }

        long physical = long.Parse(await clickHouse.QueryAsync("SELECT count() FROM events WHERE project_id = {project:UUID}", ("project", project.Id.ToString())), System.Globalization.CultureInfo.InvariantCulture);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        List<(string Measure, long Truth, long Measured)> checks = [("distinct events stored", total, stored)];

        foreach ((string name, long count) in truthEvents.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            JsonElement segment = await api.QueryAsync(project, "segment", new { @event = name, from = today.AddDays(-1), to = today });
            long measured = segment.GetProperty("days").EnumerateArray().Sum(day => day.GetProperty("events").GetInt64());
            checks.Add(($"{name} events", count, measured));
        }

        JsonElement funnel = await api.QueryAsync(project, "funnel", new { steps = FunnelSteps, from = today.AddDays(-1), to = today, windowSeconds = 7200 });
        JsonElement[] steps = [.. funnel.GetProperty("steps").EnumerateArray()];

        for (int i = 0; i < FunnelSteps.Length; i++)
        {
            checks.Add(($"funnel step {i + 1} ({FunnelSteps[i]}) users", truthFunnel[i], steps[i].GetProperty("users").GetInt64()));
        }

        Console.WriteLine();
        Console.WriteLine($"{acknowledged - total:N0} duplicate deliveries got past the collector's Redis layer and into Kafka.");
        Console.WriteLine($"{physical - stored:N0} of them were still physically in ClickHouse when counted; the rest had been collapsed on insert or merge. The queries are exact either way.");
        Console.WriteLine();
        Console.WriteLine("| Measure | Ground truth | Measured |");
        Console.WriteLine("|---|---:|---:|");

        foreach ((string measure, long truth, long measured) in checks)
        {
            Console.WriteLine($"| {measure} | {truth:N0} | {measured:N0}{(truth == measured ? string.Empty : "  <-- MISMATCH")} |");
        }

        return checks.All(check => check.Truth == check.Measured) ? 0 : 1;
    }

    private static async Task<Delivery> SendUntilAcceptedAsync(TallyhouseClient api, BenchProject project, List<Event> events)
    {
        // A 503 means none of the batch was acknowledged, so the client retries it whole, as a real SDK does.
        for (int attempt = 0; ; attempt++)
        {
            Delivery delivery = await EventBatches.SendAsync(api.Http, project.WriteKey, events);

            if (delivery.Status == HttpStatusCode.Accepted || attempt == 20)
            {
                return delivery;
            }

            await Task.Delay(500);
        }
    }

    /// <summary>
    /// Users sign up, six in ten activate, four in ten of those purchase, each step up to half an hour after
    /// the last, and page views fill the rest. Everything happened in the last 100 minutes, inside the
    /// watermark, so nothing is late and the funnel's ground truth is exact.
    /// </summary>
    private static (List<Event> Events, Dictionary<string, long> Truth, long[] Funnel) Generate(int total)
    {
        Random random = new(42);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int users = Math.Max(1, total / 10);
        List<Event> events = new(total);
        long[] funnel = new long[3];

        for (int u = 0; u < users && events.Count < total; u++)
        {
            string user = $"user-{u}";
            DateTimeOffset signedUp = now.AddMinutes(-100).AddSeconds(random.Next(0, 60 * 30));

            events.Add(new Event($"su-{u}", "signup", user, signedUp, [("plan", "pro")]));
            funnel[0]++;

            if (random.Next(100) < 60)
            {
                DateTimeOffset activated = signedUp.AddSeconds(random.Next(60, 1800));
                events.Add(new Event($"ac-{u}", "activate", user, activated, []));
                funnel[1]++;

                if (random.Next(100) < 40)
                {
                    events.Add(new Event($"pu-{u}", "purchase", user, activated.AddSeconds(random.Next(60, 1800)), [("amount", 19.0), ("currency", "EUR")]));
                    funnel[2]++;
                }
            }
        }

        for (int v = 0; events.Count < total; v++)
        {
            int user = random.Next(users);
            events.Add(EventBatches.PageView($"pv-{v}", $"user-{user}", now.AddMinutes(-100).AddSeconds(random.Next(0, 99 * 60)), v));
        }

        Dictionary<string, long> truth = events.GroupBy(e => e.Name).ToDictionary(group => group.Key, group => (long)group.Count());
        return (events, truth, funnel);
    }

    private static void Shuffle(List<Event> events, Random random)
    {
        for (int i = events.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (events[i], events[j]) = (events[j], events[i]);
        }
    }
}
