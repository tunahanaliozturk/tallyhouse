using Tallyhouse.Load;

string command = args.Length > 0 ? args[0] : "help";
Dictionary<string, string> options = Arguments.Parse(args);

return command switch
{
    "seed" => await QueryBenchmark.SeedAsync(options),
    "queries" => await QueryBenchmark.RunAsync(options),
    "soak" => await IngestSoak.RunAsync(options),
    "exactly-once" => await ExactlyOnce.RunAsync(options),
    "sampling" => await SamplingAccuracy.RunAsync(options),
    "traffic" => await Traffic.RunAsync(options),
    _ => Help(),
};

static int Help()
{
    Console.WriteLine("""
        Tallyhouse measurement harness. Point it at a running stack (defaults match compose.yaml).

          seed          [--events 100000000] [--users 2000000] [--days 30]   seed the query benchmark dataset
          queries       [--runs 30] [--warmup 3] [--out file.md]              time the query suite over it
          soak          [--rate 20000] [--seconds 600] [--batch 100]          sustained ingest, then reconcile
          exactly-once  [--events 1000000] [--duplicates 0.2]                 duplicate delivery against ground truth
          sampling      [--users 200000]                                      10% sample against the full count
          traffic       [--rate 30]                                           a live-looking stream for the dashboard
        """);
    return 1;
}
