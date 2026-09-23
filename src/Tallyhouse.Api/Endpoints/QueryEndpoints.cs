using Tallyhouse.Infrastructure.ClickHouse.Queries;
using Tallyhouse.Kernel.Queries;

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
                : Results.Json(await queries.FunnelAsync(http.Project().Id, query, cancellationToken), ApiJson.Default.FunnelResult));

        group.MapPost("/retention", async (RetentionQuery query, HttpContext http, AnalyticsQueries queries, CancellationToken cancellationToken) =>
            query.Problems() is { Count: > 0 } problems
                ? Invalid(problems)
                : Results.Json(await queries.RetentionAsync(http.Project().Id, query, cancellationToken), ApiJson.Default.RetentionResult));

        group.MapPost("/segment", async (SegmentQuery query, HttpContext http, AnalyticsQueries queries, CancellationToken cancellationToken) =>
            query.Problems() is { Count: > 0 } problems
                ? Invalid(problems)
                : Results.Json(await queries.SegmentAsync(http.Project().Id, query, cancellationToken), ApiJson.Default.SegmentResult));

        group.MapPost("/sessions", async (SessionsQuery query, HttpContext http, AnalyticsQueries queries, CancellationToken cancellationToken) =>
            query.Problems() is { Count: > 0 } problems
                ? Invalid(problems)
                : Results.Json(await queries.SessionsAsync(http.Project().Id, query, cancellationToken), ApiJson.Default.SessionsResult));
    }

    private static IResult Invalid(IReadOnlyList<string> problems) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = [.. problems] });
}
