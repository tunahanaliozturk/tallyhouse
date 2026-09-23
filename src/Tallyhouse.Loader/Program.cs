using Tallyhouse.Application;
using Tallyhouse.Infrastructure.Hosting;
using Tallyhouse.Infrastructure.Kafka;

// The loader is a web host only so it can answer health probes and expose its lag. Everything it does
// happens in the two hosted services: the Kafka-to-ClickHouse loader and the sessionizer.
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddTallyhouseLoader(builder.Configuration);
builder.AddTallyhouseTelemetry("tallyhouse-loader");

WebApplication app = builder.Build();

KafkaLoader loader = app.Services.GetRequiredService<KafkaLoader>();

// The number to alert on: it grows when ClickHouse is down or cannot keep up, and it shrinking afterwards is
// the backlog draining. Growing lag is back-pressure working, not data being lost.
Telemetry.Meter.CreateObservableGauge("tallyhouse.loader.lag", () => loader.Lag, "{message}", "Messages in the log not yet written to ClickHouse.");

app.MapGet("/health/live", () => Results.Ok());
app.MapGet("/health/ready", () => loader.Assigned ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
app.MapGet("/lag", () => Results.Text(loader.Lag.ToString(System.Globalization.CultureInfo.InvariantCulture)));

await app.RunAsync();
