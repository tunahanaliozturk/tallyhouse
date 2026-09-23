using System.Text.Json;
using Tallyhouse.Application.Ingestion;
using Tallyhouse.Domain;
using Tallyhouse.Domain.Events;
using Tallyhouse.Domain.Projects;
using Tallyhouse.Domain.Schemas;
using Tallyhouse.Infrastructure.Catalog;
using Tallyhouse.Infrastructure.ClickHouse;

namespace Tallyhouse.Api.Endpoints;

/// <summary>
/// Catalog changes and quarantine replay, for the operator. Every write refreshes this collector's cache
/// before answering, so a schema registered here validates the very next event sent here; other collectors
/// see it within one poll interval.
/// </summary>
internal static class AdminEndpoints
{
    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder operatorGroup = app.MapGroup("/v1/projects").RequireOperator().WithTags("Operator");

        operatorGroup.MapPost("/", (Delegate)CreateProjectAsync)
            .WithName("CreateProject")
            .Produces<CreateProjectResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        operatorGroup.MapPut("/{projectId:guid}/settings", (Delegate)UpdateSettingsAsync)
            .WithName("UpdateProjectSettings")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        operatorGroup.MapPut("/{projectId:guid}/schemas/{eventName}/versions/{version:int}", (Delegate)PutSchemaAsync)
            .WithName("PutSchema")
            .Produces<PutSchemaResponse>(StatusCodes.Status200OK)
            .Produces<PutSchemaResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        operatorGroup.MapPost("/{projectId:guid}/quarantine/{quarantineId:guid}/replay", (Delegate)ReplayAsync)
            .WithName("ReplayQuarantined")
            .Produces<ReplayResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        RouteGroupBuilder readGroup = app.MapGroup("/v1").RequireReadKey().WithTags("Catalog");

        readGroup.MapGet("/schemas", (HttpContext http, CatalogCache cache) =>
            Results.Json<IReadOnlyList<SchemaResponse>>(
                [.. cache.Current.SchemasOf(http.Project().Id).Select(schema => new SchemaResponse(schema.EventName, schema.Version, schema.Spec))],
                ApiJson.Default.IReadOnlyListSchemaResponse))
            .WithName("ListSchemas")
            .Produces<IReadOnlyList<SchemaResponse>>();

        readGroup.MapGet("/quarantine", async (HttpContext http, QuarantineStore quarantine, int? limit, string? cursor, CancellationToken cancellationToken) =>
        {
            QuarantinePage page = await quarantine.ListOpenAsync(http.Project().Id, limit ?? 50, cursor, cancellationToken);
            return Results.Json(QuarantinePageResponse.From(page), ApiJson.Default.QuarantinePageResponse);
        })
            .WithName("ListQuarantine")
            .Produces<QuarantinePageResponse>();
    }

    private static async Task<IResult> CreateProjectAsync(CreateProjectRequest request, CatalogService catalog, CatalogRefresher refresher, CancellationToken cancellationToken)
    {
        ProjectSettings settings = request.Settings ?? ProjectSettings.Default;
        List<string> problems = [.. settings.Problems()];

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100)
        {
            problems.Add("name is required, at most 100 characters");
        }

        if (problems.Count > 0)
        {
            return Invalid(problems);
        }

        CreatedProject created = await catalog.CreateProjectAsync(request.Name!.Trim(), settings, cancellationToken);
        await refresher.RefreshAsync(cancellationToken);

        return Results.Json(
            new CreateProjectResponse(created.Id, created.Name, created.WriteKey, created.ReadKey),
            ApiJson.Default.CreateProjectResponse,
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateSettingsAsync(Guid projectId, ProjectSettings settings, CatalogService catalog, CatalogRefresher refresher, CancellationToken cancellationToken)
    {
        if (settings.Problems() is { Count: > 0 } problems)
        {
            return Invalid(problems);
        }

        if (!await catalog.UpdateSettingsAsync(projectId, settings, cancellationToken))
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project.");
        }

        await refresher.RefreshAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> PutSchemaAsync(
        Guid projectId,
        string eventName,
        int version,
        SchemaSpec spec,
        CatalogService catalog,
        CatalogRefresher refresher,
        CancellationToken cancellationToken)
    {
        List<string> problems = [.. spec.Problems()];

        if (!Names.IsValidEventName(eventName))
        {
            problems.Add("the event name must start with a letter and use only letters, digits and _ . : -");
        }

        if (version is < 1 or > ushort.MaxValue)
        {
            problems.Add("version must be between 1 and 65535");
        }

        if (problems.Count > 0)
        {
            return Invalid(problems);
        }

        PutSchemaResult result = await catalog.PutSchemaAsync(projectId, eventName, version, spec, cancellationToken);

        switch (result.Change)
        {
            case SchemaChange.ProjectNotFound:
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project.");

            case SchemaChange.Breaking:
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: $"This would break {eventName}@{version}. Register it as version {version + 1} instead.",
                    extensions: new Dictionary<string, object?> { ["breakingChanges"] = result.BreakingChanges });
        }

        await refresher.RefreshAsync(cancellationToken);

        PutSchemaResponse response = new(eventName, version, result.Change.ToString().ToLowerInvariant());
        return Results.Json(response, ApiJson.Default.PutSchemaResponse, statusCode: result.Change == SchemaChange.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    private static async Task<IResult> ReplayAsync(
        Guid projectId,
        Guid quarantineId,
        CatalogCache cache,
        QuarantineStore quarantine,
        IngestPipeline pipeline,
        CancellationToken cancellationToken)
    {
        Project? project = cache.Current.FindProject(projectId);
        QuarantinedEvent? quarantined = project is null ? null : await quarantine.FindAsync(projectId, quarantineId, cancellationToken);

        if (project is null || quarantined is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such quarantined event.");
        }

        if (quarantined.Replayed)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This event has already been replayed.");
        }

        using JsonDocument payload = JsonDocument.Parse(quarantined.Payload);

        EventOutcome outcome;

        try
        {
            outcome = await pipeline.ReplayAsync(project, payload.RootElement, quarantined.ReceivedAt, cancellationToken);
        }
        catch (EventLogUnavailableException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The event log is not accepting writes right now.");
        }

        if (outcome.Status == EventStatus.Quarantined)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The event is still invalid and stays in quarantine.",
                detail: outcome.Reason);
        }

        await quarantine.MarkReplayedAsync(projectId, quarantined, cancellationToken);
        return Results.Json(new ReplayResponse(quarantineId, IngestPipeline.StatusTag(outcome.Status), outcome.Reason), ApiJson.Default.ReplayResponse);
    }

    private static IResult Invalid(IReadOnlyList<string> problems) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [.. problems] });
}
