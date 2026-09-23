using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Tallyhouse.Core;

/// <summary>
/// One activity source and one meter for the whole pipeline, so a trace follows an event from the HTTP
/// request through the log to the insert under a single name, and every instrument is found under one
/// prefix.
/// </summary>
public static class Telemetry
{
    public const string Name = "Tallyhouse";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> IngestedEvents = Meter.CreateCounter<long>(
        "tallyhouse.ingest.events", "{event}", "Events received by the collector, tagged by outcome.");

    public static readonly Counter<long> LateEvents = Meter.CreateCounter<long>(
        "tallyhouse.ingest.late_events", "{event}", "Accepted events older than the watermark on arrival.");
}
