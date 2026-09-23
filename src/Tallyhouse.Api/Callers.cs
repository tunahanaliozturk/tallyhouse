using Tallyhouse.Domain.Projects;
using Tallyhouse.Infrastructure;
using Tallyhouse.Infrastructure.Catalog;

namespace Tallyhouse.Api;

/// <summary>
/// Who is calling. Three kinds of credential, each good for one thing: a project write key ingests, a project
/// read key queries, and the operator token changes the catalog. Every route group states which it needs,
/// and a request without it never reaches a handler.
/// </summary>
internal static class Callers
{
    private const string ProjectItem = "tallyhouse.project";

    public static RouteGroupBuilder RequireWriteKey(this RouteGroupBuilder group) =>
        group.AddEndpointFilter((context, next) => ResolveProject(context, next, ApiKeys.WritePrefix, (snapshot, key) => snapshot.FindByWriteKey(key)));

    public static RouteGroupBuilder RequireReadKey(this RouteGroupBuilder group) =>
        group.AddEndpointFilter((context, next) => ResolveProject(context, next, ApiKeys.ReadPrefix, (snapshot, key) => snapshot.FindByReadKey(key)));

    public static RouteGroupBuilder RequireOperator(this RouteGroupBuilder group) =>
        group.AddEndpointFilter(async (context, next) =>
        {
            string? configured = context.HttpContext.RequestServices.GetRequiredService<TallyhouseOptions>().OperatorToken;

            if (string.IsNullOrEmpty(configured))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Operator endpoints are disabled because no operator token is configured.");
            }

            return ApiKeys.FixedTimeEquals(Bearer(context.HttpContext), configured) ? await next(context) : Unauthorized(context.HttpContext);
        });

    /// <summary>The project a key filter resolved. Only valid inside a group that requires a project key.</summary>
    public static Project Project(this HttpContext http) =>
        http.Items[ProjectItem] as Project ?? throw new InvalidOperationException("No project key filter ran for this endpoint.");

    private static async ValueTask<object?> ResolveProject(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string prefix,
        Func<CatalogSnapshot, string, Project?> find)
    {
        HttpContext http = context.HttpContext;
        string? key = Bearer(http);

        if (key is null || !key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Unauthorized(http);
        }

        Project? project = find(http.RequestServices.GetRequiredService<CatalogCache>().Current, key);

        if (project is null)
        {
            return Unauthorized(http);
        }

        http.Items[ProjectItem] = project;
        return await next(context);
    }

    private static string? Bearer(HttpContext http)
    {
        string header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    }

    private static IResult Unauthorized(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "A valid key for this endpoint is required.");
    }
}
