using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tallyhouse.Infrastructure.Catalog;

/// <summary>
/// The catalog: projects, their key hashes and settings, and the event schemas the ingest path validates
/// against. Small, transactional, and read far more often than it is written, which is why it lives in
/// Postgres and not next to the events. The ingest path never touches this context; it reads the frozen
/// snapshot in <see cref="CatalogCache"/>.
/// </summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();

    public DbSet<EventSchemaEntity> EventSchemas => Set<EventSchemaEntity>();

    public DbSet<CatalogRevisionEntity> CatalogRevision => Set<CatalogRevisionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ProjectEntity>(project =>
        {
            project.ToTable("projects", table => table.HasCheckConstraint("ck_projects_name_length", "length(name) BETWEEN 1 AND 100"));
            project.HasKey(p => p.Id);
            project.Property(p => p.Id).ValueGeneratedNever();
            project.Property(p => p.Name).HasMaxLength(100);

            // SHA-256 of each key. The key itself is shown once, at creation, and never stored.
            project.Property(p => p.WriteKeyHash).HasMaxLength(32);
            project.Property(p => p.ReadKeyHash).HasMaxLength(32);
            project.HasIndex(p => p.WriteKeyHash).IsUnique();
            project.HasIndex(p => p.ReadKeyHash).IsUnique();

            project.Property(p => p.Settings).HasColumnType("jsonb");
            project.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<EventSchemaEntity>(schema =>
        {
            schema.ToTable("event_schemas", table => table.HasCheckConstraint("ck_event_schemas_version", "version BETWEEN 1 AND 65535"));
            schema.HasKey(s => new { s.ProjectId, s.EventName, s.Version });
            schema.Property(s => s.EventName).HasMaxLength(128);
            schema.Property(s => s.Spec).HasColumnType("jsonb");
            schema.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            schema.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");

            // Postgres's xmin as the concurrency token. Two edits of one version that both passed the evolution
            // check against the same old spec cannot both save; the loser re-reads and is judged again.
            schema.Property(s => s.RowVersion).IsRowVersion();

            schema.HasOne<ProjectEntity>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CatalogRevisionEntity>(revision =>
        {
            revision.ToTable("catalog_revision", table => table.HasCheckConstraint("ck_catalog_revision_singleton", "singleton"));
            revision.HasKey(r => r.Singleton);
            revision.HasData(new CatalogRevisionEntity { Singleton = true, Revision = 0 });
        });
    }
}

public sealed class ProjectEntity
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required byte[] WriteKeyHash { get; set; }

    public required byte[] ReadKeyHash { get; set; }

    /// <summary><see cref="Domain.Projects.ProjectSettings"/> as JSON.</summary>
    public required string Settings { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class EventSchemaEntity
{
    public Guid ProjectId { get; set; }

    public required string EventName { get; set; }

    public int Version { get; set; }

    /// <summary><see cref="Domain.Schemas.SchemaSpec"/> as JSON.</summary>
    public required string Spec { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint RowVersion { get; set; }
}

/// <summary>
/// One row holding one number. Every collector keeps the whole catalog in memory and polls this to learn
/// that it changed. Triggers bump it in the same transaction as any change to the catalog, so no code path
/// can write the catalog and forget to announce it.
/// </summary>
public sealed class CatalogRevisionEntity
{
    public bool Singleton { get; set; }

    public long Revision { get; set; }
}

/// <summary>Lets <c>dotnet ef migrations add</c> build the model without a running host or database.</summary>
internal sealed class CatalogDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=tallyhouse")
            .UseSnakeCaseNamingConvention()
            .Options);
}
