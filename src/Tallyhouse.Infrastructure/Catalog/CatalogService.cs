using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tallyhouse.Domain.Projects;
using Tallyhouse.Domain.Schemas;

namespace Tallyhouse.Infrastructure.Catalog;

public enum SchemaChange
{
    Created,
    Updated,
    Unchanged,
    Breaking,
    ProjectNotFound,
}

public sealed record PutSchemaResult(SchemaChange Change, IReadOnlyList<string> BreakingChanges)
{
    public static PutSchemaResult Of(SchemaChange change) => new(change, []);
}

public sealed record CreatedProject(Guid Id, string Name, string WriteKey, string ReadKey);

/// <summary>
/// Changes to the catalog, and the snapshot load the cache is built from. Uses the context directly: the
/// operations here are few, each is one unit of work, and a repository over it would only rename EF.
/// </summary>
/// <remarks>
/// It takes a context factory rather than a context because its callers include a singleton (the
/// refresher), and each operation is short enough that a pooled context per call is the natural lifetime.
/// </remarks>
public sealed class CatalogService(IDbContextFactory<CatalogDbContext> contexts, TimeProvider time)
{
    public async Task<CreatedProject> CreateProjectAsync(string name, ProjectSettings settings, CancellationToken cancellationToken)
    {
        CreatedProject created = new(Guid.CreateVersion7(), name, ApiKeys.NewWriteKey(), ApiKeys.NewReadKey());

        await using CatalogDbContext context = await contexts.CreateDbContextAsync(cancellationToken);
        context.Projects.Add(Entity(created, settings));
        await context.SaveChangesAsync(cancellationToken);
        return created;
    }

    /// <summary>Creates a project with known keys unless it already exists. Only for the demo stack and the test suites.</summary>
    public async Task EnsureProjectAsync(CreatedProject project, ProjectSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        await using CatalogDbContext context = await contexts.CreateDbContextAsync(cancellationToken);

        if (await context.Projects.AnyAsync(p => p.Id == project.Id, cancellationToken))
        {
            return;
        }

        context.Projects.Add(Entity(project, settings));

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Another instance seeded it first.
        }
    }

    public async Task<bool> UpdateSettingsAsync(Guid projectId, ProjectSettings settings, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(settings, CatalogJson.Default.ProjectSettings);

        await using CatalogDbContext context = await contexts.CreateDbContextAsync(cancellationToken);
        int updated = await context.Projects
            .Where(p => p.Id == projectId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.Settings, json), cancellationToken);

        return updated == 1;
    }

    /// <summary>
    /// Registers a version, or grows an existing one. Saving is optimistic on the row's xmin, so of two
    /// concurrent edits judged against the same old spec, one saves and the other is judged again against
    /// what the first one wrote.
    /// </summary>
    public async Task<PutSchemaResult> PutSchemaAsync(Guid projectId, string eventName, int version, SchemaSpec spec, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(spec, CatalogJson.Default.SchemaSpec);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            await using CatalogDbContext context = await contexts.CreateDbContextAsync(cancellationToken);

            EventSchemaEntity? existing = await context.EventSchemas.SingleOrDefaultAsync(
                s => s.ProjectId == projectId && s.EventName == eventName && s.Version == version,
                cancellationToken);

            if (existing is null)
            {
                if (!await context.Projects.AnyAsync(p => p.Id == projectId, cancellationToken))
                {
                    return PutSchemaResult.Of(SchemaChange.ProjectNotFound);
                }

                context.EventSchemas.Add(new EventSchemaEntity { ProjectId = projectId, EventName = eventName, Version = version, Spec = json });
            }
            else
            {
                SchemaSpec current = JsonSerializer.Deserialize(existing.Spec, CatalogJson.Default.SchemaSpec)!;
                IReadOnlyList<string> breaking = SchemaEvolution.BreakingChanges(current, spec);

                if (breaking.Count > 0)
                {
                    return new PutSchemaResult(SchemaChange.Breaking, breaking);
                }

                // Growing in neither direction means the two specs are the same.
                if (SchemaEvolution.BreakingChanges(spec, current).Count == 0)
                {
                    return PutSchemaResult.Of(SchemaChange.Unchanged);
                }

                existing.Spec = json;
                existing.UpdatedAt = time.GetUtcNow();
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return PutSchemaResult.Of(existing is null ? SchemaChange.Created : SchemaChange.Updated);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Somebody changed this version since it was read. Judge the change again against theirs.
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // Somebody registered this version since it was found missing. Same again.
            }
        }

        throw new InvalidOperationException($"Schema {eventName}@{version} kept changing concurrently.");
    }

    public async Task<long> RevisionAsync(CancellationToken cancellationToken)
    {
        await using CatalogDbContext context = await contexts.CreateDbContextAsync(cancellationToken);
        return await context.CatalogRevision.AsNoTracking().Select(r => r.Revision).SingleAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the revision, the projects and the schemas in one repeatable-read transaction, so the snapshot is
    /// one consistent state of the catalog and its revision number describes exactly that state.
    /// </summary>
    public async Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await using CatalogDbContext context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        long revision = await context.CatalogRevision.AsNoTracking().Select(r => r.Revision).SingleAsync(cancellationToken);
        List<ProjectEntity> projects = await context.Projects.AsNoTracking().ToListAsync(cancellationToken);
        List<EventSchemaEntity> schemas = await context.EventSchemas.AsNoTracking().ToListAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new CatalogSnapshot(
            revision,
            projects.Select(p => new StoredProject(
                new Project(p.Id, p.Name, JsonSerializer.Deserialize(p.Settings, CatalogJson.Default.ProjectSettings)!),
                Convert.ToHexString(p.WriteKeyHash),
                Convert.ToHexString(p.ReadKeyHash))),
            schemas.Select(s => new CompiledSchemaRow(
                s.ProjectId,
                CompiledSchema.Compile(s.EventName, s.Version, JsonSerializer.Deserialize(s.Spec, CatalogJson.Default.SchemaSpec)!))));
    }

    private static ProjectEntity Entity(CreatedProject project, ProjectSettings settings) => new()
    {
        Id = project.Id,
        Name = project.Name,
        WriteKeyHash = ApiKeys.Hash(project.WriteKey),
        ReadKeyHash = ApiKeys.Hash(project.ReadKey),
        Settings = JsonSerializer.Serialize(settings, CatalogJson.Default.ProjectSettings),
    };

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SchemaSpec))]
[JsonSerializable(typeof(ProjectSettings))]
internal sealed partial class CatalogJson : JsonSerializerContext;
