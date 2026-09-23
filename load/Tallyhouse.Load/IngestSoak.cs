using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace Tallyhouse.Load;

/// <summary>
/// Sustained ingest at a fixed rate, then a reconciliation of what was acknowledged against what ClickHouse
/// holds.
/// </summary>
/// <remarks>
/// <para>Open loop: request i is due at i * (batch / rate) seconds whether or not earlier requests have
/// answered, and its latency is measured from when it was due, not from when it was sent. A closed-loop
/// client slows down when the server does and reports a flattering p99; this one does not.</para>
/// <para>Windows timers tick every 15.6 ms, so requests leave in small bursts at that granularity rather than
/// perfectly spaced. The rate over any second is exact.</para>
/// </remarks>
internal static class IngestSoak
{
    public static async Task<int> RunAsync(Dictionary<string, string> options)
    {
        long rate = options.Long("rate", 20_000);
        long seconds = options.Long("seconds", 600);
        int batch = (int)options.Long("batch", 100);
        int users = (int)options.Long("users", 200_000);
        string? output = options.GetValueOrDefault("out");

        Endpoints endpoints = Endpoints.FromEnvironment();
        using TallyhouseClient api = new(endpoints, connections: 1024);
        using ClickHouseHttp clickHouse = new(endpoints);

        BenchProject project = await api.CreateProjectAsync($"soak-{DateTime.UtcNow:yyyyMMddHHmm}", new { redactProperties = Array.Empty<string>(), sampleRates = new { } });

        long requests = rate * seconds / batch;
        double interval = (double)batch / rate;
        double[] latencies = new double[requests];
        long accepted = 0, refused = 0, failed = 0, inFlight = 0, peakInFlight = 0;

        Console.WriteLine($"Soak: {Stats.N(rate)} events/s for {seconds} s in batches of {batch} ({Stats.N(requests)} requests) into project {project.Id}.");

        List<Task> tasks = new((int)requests);
        Stopwatch clock = Stopwatch.StartNew();

        for (long i = 0; i < requests; i++)
        {
            double due = i * interval;

            while (clock.Elapsed.TotalSeconds < due)
            {
                await Task.Delay(1);
            }

            long index = i;
            tasks.Add(Task.Run(async () =>
            {
                long current = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref peakInFlight, current);

                Event[] events = new Event[batch];
                DateTimeOffset now = DateTimeOffset.UtcNow;

                for (int j = 0; j < batch; j++)
                {
                    int user = Random.Shared.Next(users);
                    events[j] = EventBatches.PageView($"soak-{index}-{j}", $"user-{user}", now, user + j);
                }

                try
                {
                    Delivery delivery = await EventBatches.SendAsync(api.Http, project.WriteKey, events);

                    if (delivery.Status == HttpStatusCode.Accepted)
                    {
                        Interlocked.Add(ref accepted, delivery.Accepted);
                    }
                    else
                    {
                        Interlocked.Increment(ref refused);
                    }
                }
                catch (HttpRequestException)
                {
                    Interlocked.Increment(ref failed);
                }

                latencies[index] = (clock.Elapsed.TotalSeconds - due) * 1000;
                Interlocked.Decrement(ref inFlight);
            }));
        }

        await Task.WhenAll(tasks);
        double elapsed = clock.Elapsed.TotalSeconds;

        Console.WriteLine($"Sent in {elapsed:0.0} s. Reconciling against ClickHouse.");

        // Wait for the loader to drain: until everything acknowledged is stored, or nothing has moved for a minute.
        long stored = 0, previous = -1;
        Stopwatch stalled = Stopwatch.StartNew();

        while (stored < accepted && stalled.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(1000);
            stored = await clickHouse.CountAsync(project.Id);

            if (stored != previous)
            {
                previous = stored;
                stalled.Restart();
            }
        }

        Array.Sort(latencies);

        string report = string.Join(Environment.NewLine,
        [
            $"Target: {Stats.N(rate)} events/s for {seconds} s, {batch} events per request, open loop.",
            "",
            "| Measure | Value |",
            "|---|---:|",
            $"| Events acknowledged (202) | {Stats.N(accepted)} |",
            $"| Achieved rate | {Stats.N((long)(accepted / elapsed))} events/s |",
            $"| Requests refused (503) | {Stats.N(refused)} |",
            $"| Requests failed (transport) | {Stats.N(failed)} |",
            $"| Ack latency p50 | {Stats.Ms(Stats.Percentile(latencies, 50))} ms |",
            $"| Ack latency p95 | {Stats.Ms(Stats.Percentile(latencies, 95))} ms |",
            $"| Ack latency p99 | {Stats.Ms(Stats.Percentile(latencies, 99))} ms |",
            $"| Ack latency max | {Stats.Ms(latencies[^1])} ms |",
            $"| Peak requests in flight | {Stats.N(peakInFlight)} |",
            $"| Events in ClickHouse afterwards | {Stats.N(stored)} |",
            $"| Acknowledged but not stored | {Stats.N(accepted - stored)} |",
        ]);

        Console.WriteLine(report);

        if (output is not null)
        {
            await File.WriteAllTextAsync(output, report + Environment.NewLine);
        }

        return accepted == stored ? 0 : 1;
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long current = Interlocked.Read(ref target);

        while (value > current)
        {
            long seen = Interlocked.CompareExchange(ref target, value, current);

            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
