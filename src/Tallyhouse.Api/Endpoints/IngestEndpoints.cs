using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tallyhouse.Infrastructure;
using Tallyhouse.Kernel.Ingestion;

namespace Tallyhouse.Api.Endpoints;

/// <summary>
/// The hot path. The body is parsed into a pooled <see cref="JsonDocument"/> and each event handed to the
/// pipeline as a raw element, so a malformed event is one quarantined event rather than a failed request.
/// A 202 means every event in the request is durable in the log, not that it is queryable yet.
/// </summary>
internal static class IngestEndpoints
{
    // Generous for 500 events of ordinary size. Beyond it Kestrel answers 413 before any parsing.
    private const long MaxBatchBytes = 4L * 1024 * 1024;
    private const long MaxEventBytes = 64L * 1024;

    public static void MapIngest(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/v1/events").RequireWriteKey().WithTags("Ingest");

        group.MapPost("/", (Delegate)IngestOneAsync)
            .WithName("IngestEvent")
            .WithMetadata(new RequestSizeLimitAttribute(MaxEventBytes))
            .Produces<IngestResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/batch", (Delegate)IngestBatchAsync)
            .WithName("IngestBatch")
            .WithMetadata(new RequestSizeLimitAttribute(MaxBatchBytes))
            .Produces<IngestResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> IngestOneAsync(HttpContext http, IngestPipeline pipeline, CancellationToken cancellationToken)
    {
        using JsonDocument? document = await ParseAsync(http, cancellationToken);

        return document is null
            ? NotJson()
            : await IngestAsync(http, pipeline, [document.RootElement], cancellationToken);
    }

    private static async Task<IResult> IngestBatchAsync(HttpContext http, IngestPipeline pipeline, TallyhouseOptions options, CancellationToken cancellationToken)
    {
        using JsonDocument? document = await ParseAsync(http, cancellationToken);

        if (document is null)
        {
            return NotJson();
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("events", out JsonElement events)
            || events.ValueKind != JsonValueKind.Array)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The body must be an object with an \"events\" array.");
        }

        int count = events.GetArrayLength();

        if (count == 0 || count > options.Ingestion.MaxBatchSize)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"A batch holds between 1 and {options.Ingestion.MaxBatchSize} events.");
        }

        List<JsonElement> list = new(count);

        foreach (JsonElement element in events.EnumerateArray())
        {
            list.Add(element);
        }

        return await IngestAsync(http, pipeline, list, cancellationToken);
    }

    private static async Task<IResult> IngestAsync(HttpContext http, IngestPipeline pipeline, IReadOnlyList<JsonElement> events, CancellationToken cancellationToken)
    {
        try
        {
            IngestResult result = await pipeline.IngestAsync(http.Project(), events, cancellationToken);
            return Results.Json(IngestResponse.From(result), ApiJson.Default.IngestResponse, statusCode: StatusCodes.Status202Accepted);
        }
        catch (EventLogUnavailableException)
        {
            // Nothing in the request is acknowledged. Some of it may have reached the log anyway; the retry
            // carries the same message ids and deduplication drops whatever got through the first time.
            http.Response.Headers.RetryAfter = "1";
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The event log is not accepting writes right now.",
                detail: "None of these events were acknowledged. Retry the same request; events already stored are recognised by messageId and not counted twice.");
        }
    }

    private static async Task<JsonDocument?> ParseAsync(HttpContext http, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult NotJson() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The body is not valid JSON.");
}
