using System.Collections.Frozen;
using Tallyhouse.Application.Ingestion;
using Tallyhouse.Domain.Events;
using Tallyhouse.Domain.Projects;
using Tallyhouse.Domain.Schemas;

namespace Tallyhouse.Infrastructure.Catalog;

/// <summary>
/// The whole catalog at one revision, frozen for lookups. It is replaced wholesale when the revision moves,
/// never mutated, so the ingest path reads it without a lock and never sees a project from one revision with
/// the schemas of another.
/// </summary>
public sealed class CatalogSnapshot
{
    private readonly FrozenDictionary<Guid, Project> projects;
    private readonly FrozenDictionary<string, Project> byWriteKeyHash;
    private readonly FrozenDictionary<string, Project> byReadKeyHash;
    private readonly FrozenDictionary<(Guid, string), CompiledSchema[]> schemas;

    public CatalogSnapshot(long revision, IEnumerable<StoredProject> projects, IEnumerable<CompiledSchemaRow> schemas)
    {
        List<StoredProject> projectList = [.. projects];

        Revision = revision;
        this.projects = projectList.ToFrozenDictionary(stored => stored.Project.Id, stored => stored.Project);
        byWriteKeyHash = projectList.ToFrozenDictionary(stored => stored.WriteKeyHashHex, stored => stored.Project, StringComparer.Ordinal);
        byReadKeyHash = projectList.ToFrozenDictionary(stored => stored.ReadKeyHashHex, stored => stored.Project, StringComparer.Ordinal);
        this.schemas = schemas
            .GroupBy(row => (row.ProjectId, row.Schema.EventName))
            .ToFrozenDictionary(group => group.Key, group => group.Select(row => row.Schema).OrderBy(schema => schema.Version).ToArray());
    }

    public static CatalogSnapshot Empty { get; } = new(-1, [], []);

    public long Revision { get; }

    public Project? FindProject(Guid id) => projects.GetValueOrDefault(id);

    public Project? FindByWriteKey(ReadOnlySpan<char> key) => byWriteKeyHash.GetValueOrDefault(ApiKeys.HashHex(key));

    public Project? FindByReadKey(ReadOnlySpan<char> key) => byReadKeyHash.GetValueOrDefault(ApiKeys.HashHex(key));

    public CompiledSchema? Find(Guid projectId, string eventName, int? version)
    {
        if (!schemas.TryGetValue((projectId, eventName), out CompiledSchema[]? versions))
        {
            return null;
        }

        if (version is null)
        {
            return versions[^1];
        }

        foreach (CompiledSchema schema in versions)
        {
            if (schema.Version == version.Value)
            {
                return schema;
            }
        }

        return null;
    }

    public IEnumerable<CompiledSchema> SchemasOf(Guid projectId) =>
        schemas.Where(pair => pair.Key.Item1 == projectId).SelectMany(pair => pair.Value).OrderBy(schema => schema.EventName, StringComparer.Ordinal).ThenBy(schema => schema.Version);
}

public sealed record StoredProject(Project Project, string WriteKeyHashHex, string ReadKeyHashHex);

public sealed record CompiledSchemaRow(Guid ProjectId, CompiledSchema Schema);

/// <summary>The live catalog every request reads. Swapped atomically by <see cref="CatalogRefresher"/>.</summary>
public sealed class CatalogCache : ISchemaLookup
{
    private CatalogSnapshot current = CatalogSnapshot.Empty;

    public CatalogSnapshot Current => Volatile.Read(ref current);

    public void Replace(CatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Two refreshes can race; a slower one must not put an older revision back.
        CatalogSnapshot seen;

        do
        {
            seen = Volatile.Read(ref current);

            if (snapshot.Revision <= seen.Revision)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref current, snapshot, seen) != seen);
    }

    public CompiledSchema? Find(Guid projectId, string eventName, int? version) => Current.Find(projectId, eventName, version);
}
