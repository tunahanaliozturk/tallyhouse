using Tallyhouse.Application.Queries;
using Tallyhouse.Infrastructure.ClickHouse.Queries;

namespace Tallyhouse.Api.Endpoints;

/// <summary>Analytics queries, scoped to the project whose read key made the request.</summary>
internal static class QueryEndpoints
{
    public static void MapQueries(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/v1/queries").RequireReadKey().WithTags("Queries");

        group.MapPost("/funnel", async (FunnelQuery query, HttpContext http, AnalyticsQueries queries, CancellationToken cancellationToken) =>
            query.Problems() is { Count: > 0 } problems
                ? Invalid(problems)
                : Results.Json(await queries.FunnelAsync(http.Project().Id, query, cancellationToken), ApiJson.Default.FunnelResult))
            .WithName("FunnelQuery")
            .Produces<FunnelResult>()
            .ProducesValidationProblem();

        group.MapPost("/retention", async (RetentionQuery query, HttpContext http, AnalyticsQueries queries, CancellationToken cancellationToken) =>
            query.Problems() is { Count: > 0 } problems
                ? Invalid(problems)
                : Results.Json(await queries.RetentionAsync(http.Project().Id, query, cancellationToken), ApiJson.Default.RetentionResult))
            .WithName("RetentionQuery")
            .Produces<RetentionResult>()
            .ProducesValidationProblem();

        group.MapPost("/segment", async (SegmentQuery query, HttpContext http, AnalyticsQueries queries, CancellationToken cancellationToken) =>
            query.Problems() is { Count: > 0 } problems
                ? Invalid(problems)
                : Results.Json(await queries.SegmentAsync(http.Project().Id, query, cancellationToken), ApiJson.Default.SegmentResult))
            .WithName("SegmentQuery")
            .Produces<SegmentResult>()
            .ProducesValidationProblem();

        group.MapPost("/sessions", async (SessionsQuery query, HttpContext http, AnalyticsQueries queries, CancellationToken cancellationToken) =>
            query.Problems() is { Count: > 0 } problems
                ? Invalid(problems)
                : Results.Json(await queries.SessionsAsync(http.Project().Id, query, cancellationToken), ApiJson.Default.SessionsResult))
            .WithName("SessionsQuery")
            .Produces<SessionsResult>()
            .ProducesValidationProblem();
    }

    private static IResult Invalid(IReadOnlyList<string> problems) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = [.. problems] });
}
