using Tallyhouse.Api;
using Tallyhouse.Api.Endpoints;
using Tallyhouse.Infrastructure.Catalog;
using Tallyhouse.Infrastructure.Hosting;
using Tallyhouse.Infrastructure.Kafka;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJson.Default));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddTallyhouseCollector(builder.Configuration);
builder.AddTallyhouseTelemetry("tallyhouse-api");

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapOpenApi();

app.MapGet("/health/live", () => Results.Ok());

// Ready means this collector can acknowledge events: it has a catalog to validate against and a broker to
// write to. Redis and ClickHouse are deliberately absent. Ingest works without either, and taking the
// collector out of the load balancer because the analytics store is down would turn a query outage into
// data loss at the client.
app.MapGet("/health/ready", async (CatalogCache cache, KafkaHealth kafka, CancellationToken cancellationToken) =>
    cache.Current.Revision >= 0 && await kafka.IsHealthyAsync(cancellationToken)
        ? Results.Ok()
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapIngest();
app.MapAdmin();
app.MapQueries();

await app.RunAsync();

/// <summary>The entry point, visible to the integration suite's host factory.</summary>
public partial class Program;
