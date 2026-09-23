using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tallyhouse.Infrastructure.Catalog;
using Tallyhouse.Kernel.Projects;
using Tallyhouse.Kernel.Schemas;

namespace Tallyhouse.Infrastructure.Hosting;

/// <summary>
/// A project with known keys and a small product's worth of schemas, so the compose stack can be driven
/// with curl the moment it is up. Off unless <c>Tallyhouse:Demo:ProjectId</c> is configured, and the keys
/// it uses are in the repository, so it is for local use only.
/// </summary>
public static class DemoProject
{
    public static readonly IReadOnlyList<(string Event, SchemaSpec Spec)> Schemas =
    [
        ("page_view", new SchemaSpec(new Dictionary<string, FieldSpec>
        {
            ["path"] = new(FieldType.String, Required: true),
            ["referrer"] = new(FieldType.String),
        })),
        ("signup", new SchemaSpec(new Dictionary<string, FieldSpec>
        {
            ["plan"] = new(FieldType.String, Required: true, Enum: ["free", "pro", "team"]),
            ["source"] = new(FieldType.String),
        })),
        ("activate", new SchemaSpec(new Dictionary<string, FieldSpec>())),
        ("purchase", new SchemaSpec(new Dictionary<string, FieldSpec>
        {
            ["amount"] = new(FieldType.Number, Required: true),
            ["currency"] = new(FieldType.String, Enum: ["USD", "EUR", "TRY"]),
            ["plan"] = new(FieldType.String),
        })),
    ];

    public static IServiceCollection AddTallyhouseDemo(this IServiceCollection services, IConfiguration configuration)
    {
        IConfigurationSection demo = configuration.GetSection("Tallyhouse:Demo");

        if (!Guid.TryParse(demo["ProjectId"], out Guid projectId))
        {
            return services;
        }

        CreatedProject project = new(
            projectId,
            demo["Name"] ?? "demo",
            demo["WriteKey"] ?? throw new InvalidOperationException("Tallyhouse:Demo:WriteKey is required with a demo project."),
            demo["ReadKey"] ?? throw new InvalidOperationException("Tallyhouse:Demo:ReadKey is required with a demo project."));

        ProjectSettings settings = new(["email"], new Dictionary<string, double>());

        return services.AddStartupTask(async (sp, ct) =>
        {
            CatalogService catalog = sp.GetRequiredService<CatalogService>();
            await catalog.EnsureProjectAsync(project, settings, ct);

            foreach ((string name, SchemaSpec spec) in Schemas)
            {
                await catalog.PutSchemaAsync(project.Id, name, 1, spec, ct);
            }

            await sp.GetRequiredService<CatalogRefresher>().RefreshAsync(ct);
        });
    }
}
