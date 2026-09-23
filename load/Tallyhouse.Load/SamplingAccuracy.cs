using System.Diagnostics;
using System.Text.Json;

namespace Tallyhouse.Load;

/// <summary>
/// Sends the same page views to two projects, one keeping everything and one keeping 10% of users, and
/// compares what each reports. The claim is that the sampled estimate is within 2% of the full count; this
/// is where that number comes from.
/// </summary>
internal static class SamplingAccuracy
{
    public static async Task<int> RunAsync(Dictionary<string, string> options)
    {
        int users = (int)options.Long("users", 200_000);
        double rate = options.Double("rate", 0.1);

        Endpoints endpoints = Endpoints.FromEnvironment();
        using TallyhouseClient api = new(endpoints, connections: 64);
        using ClickHouseHttp clickHouse = new(endpoints);

        string stamp = $"{DateTime.UtcNow:yyyyMMddHHmm}";
        BenchProject full = await api.CreateProjectAsync($"sampling-full-{stamp}", new { redactProperties = Array.Empty<string>(), sampleRates = new { } });
        BenchProject sampled = await api.CreateProjectAsync($"sampling-{rate}-{stamp}", new { redactProperties = Array.Empty<string>(), sampleRates = new Dictionary<string, double> { ["page_view"] = rate } });

        // Heavy and light users alike: one to nine views each, so the estimate is tested on a skewed
        // distribution and not only on identical users.
        Random random = new(11);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<Event> events = [];

        for (int u = 0; u < users; u++)
        {
            for (int v = 1 + random.Next(9); v > 0; v--)
            {
                events.Add(EventBatches.PageView($"pv-{u}-{v}", $"user-{u}", now.AddSeconds(-random.Next(1, 3600)), u + v));
            }
        }

        Console.WriteLine($"{events.Count:N0} page views from {users:N0} users, to both projects.");

        long fullAccepted = 0, sampledAccepted = 0;
        Stopwatch clock = Stopwatch.StartNew();

        await Parallel.ForEachAsync(events.Chunk(500), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (batch, cancellationToken) =>
        {
            Delivery toFull = await EventBatches.SendAsync(api.Http, full.WriteKey, batch, cancellationToken);
            Delivery toSampled = await EventBatches.SendAsync(api.Http, sampled.WriteKey, batch, cancellationToken);
            Interlocked.Add(ref fullAccepted, toFull.Accepted);
            Interlocked.Add(ref sampledAccepted, toSampled.Accepted);
        });

        Console.WriteLine($"Sent in {clock.Elapsed.TotalSeconds:0} s. Kept {sampledAccepted:N0} of {fullAccepted:N0} in the sampled project ({(double)sampledAccepted / fullAccepted:P2}).");

        await WaitForAsync(clickHouse, full.Id, fullAccepted);
        await WaitForAsync(clickHouse, sampled.Id, sampledAccepted);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        (long fullEvents, long fullUsers) = await TotalsAsync(api, full, today);
        (long estimatedEvents, long estimatedUsers) = await TotalsAsync(api, sampled, today);

        double eventError = (double)(estimatedEvents - fullEvents) / fullEvents;
        double userError = (double)(estimatedUsers - fullUsers) / fullUsers;

        Console.WriteLine();
        Console.WriteLine($"Sampling rate {rate:P0}, {users:N0} users, {events.Count:N0} events.");
        Console.WriteLine();
        Console.WriteLine("| Measure | Full count | Sampled estimate | Error |");
        Console.WriteLine("|---|---:|---:|---:|");
        Console.WriteLine($"| page_view events | {fullEvents:N0} | {estimatedEvents:N0} | {eventError:+0.00%;-0.00%} |");
        Console.WriteLine($"| page_view users | {fullUsers:N0} | {estimatedUsers:N0} | {userError:+0.00%;-0.00%} |");

        return Math.Abs(eventError) < 0.02 && Math.Abs(userError) < 0.02 ? 0 : 1;
    }

    private static async Task WaitForAsync(ClickHouseHttp clickHouse, Guid projectId, long expected)
    {
        for (int i = 0; i < 120 && await clickHouse.CountAsync(projectId) < expected; i++)
        {
            await Task.Delay(1000);
        }
    }

    private static async Task<(long Events, long Users)> TotalsAsync(TallyhouseClient api, BenchProject project, DateOnly today)
    {
        JsonElement segment = await api.QueryAsync(project, "segment", new { @event = "page_view", from = today.AddDays(-1), to = today });
        JsonElement[] days = [.. segment.GetProperty("days").EnumerateArray()];

        // Users are counted per day by the API. Every view here is within the last hour, which can straddle
        // midnight, so a user can appear on two days; the harness only compares like with like, the same
        // sum on both projects.
        return (days.Sum(day => day.GetProperty("events").GetInt64()), days.Sum(day => day.GetProperty("users").GetInt64()));
    }
}
