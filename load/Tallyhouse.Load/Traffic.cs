namespace Tallyhouse.Load;

/// <summary>
/// A stream that looks like a small product being used: visitors browse, some sign up, some of those
/// activate, some of those buy, each step a few seconds to a few minutes after the last. Sent to the demo
/// project so the dashboard has something moving on it.
/// </summary>
internal static class Traffic
{
    public static async Task<int> RunAsync(Dictionary<string, string> options)
    {
        int visitorsPerSecond = (int)options.Long("rate", 30);
        long seconds = options.Long("seconds", long.MaxValue / 2);
        string writeKey = options.String("write-key", "thw_demo_write_key_do_not_use_outside_localhost");

        Endpoints endpoints = Endpoints.FromEnvironment();
        using TallyhouseClient api = new(endpoints, connections: 16);

        Random random = new();
        PriorityQueue<Event, DateTimeOffset> pending = new();
        long visitor = 0, sent = 0;
        DateTimeOffset stopAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(seconds, 10_000_000));
        string[] plans = ["free", "pro", "team"];

        Console.WriteLine($"Sending about {visitorsPerSecond} new visitors a second to the demo project. Ctrl+C to stop.");

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));

        while (DateTimeOffset.UtcNow < stopAt && await timer.WaitForNextTickAsync())
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<Event> due = [];

            for (int i = 0; i < visitorsPerSecond; i++)
            {
                string user = $"visitor-{visitor++}";
                int views = 1 + random.Next(6);

                for (int v = 0; v < views; v++)
                {
                    pending.Enqueue(EventBatches.PageView(Guid.NewGuid().ToString("N"), user, now.AddSeconds(v * random.Next(5, 40)), random.Next()), now.AddSeconds(v * 20));
                }

                if (random.Next(100) >= 12)
                {
                    continue;
                }

                DateTimeOffset signedUp = now.AddSeconds(random.Next(20, 180));
                pending.Enqueue(new Event(Guid.NewGuid().ToString("N"), "signup", user, signedUp, [("plan", plans[random.Next(plans.Length)])]), signedUp);

                if (random.Next(100) >= 50)
                {
                    continue;
                }

                DateTimeOffset activated = signedUp.AddSeconds(random.Next(30, 600));
                pending.Enqueue(new Event(Guid.NewGuid().ToString("N"), "activate", user, activated, []), activated);

                if (random.Next(100) < 35)
                {
                    DateTimeOffset purchased = activated.AddSeconds(random.Next(60, 900));
                    pending.Enqueue(new Event(Guid.NewGuid().ToString("N"), "purchase", user, purchased, [("amount", Math.Round(9 + (random.NextDouble() * 90), 2)), ("currency", "EUR")]), purchased);
                }
            }

            while (pending.TryPeek(out Event? next, out DateTimeOffset at) && at <= now)
            {
                pending.Dequeue();
                due.Add(next with { At = at });
            }

            foreach (Event[] batch in due.Chunk(500))
            {
                Delivery delivery = await EventBatches.SendAsync(api.Http, writeKey, batch);
                sent += delivery.Accepted;
            }

            Console.Write($"\r{sent:N0} events sent, {pending.Count:N0} scheduled.   ");
        }

        return 0;
    }
}
