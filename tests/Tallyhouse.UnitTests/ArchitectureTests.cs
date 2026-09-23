using System.Reflection;
using Tallyhouse.Application.Ingestion;
using Tallyhouse.Domain;
using Tallyhouse.Infrastructure;

namespace Tallyhouse.UnitTests;

/// <summary>
/// The layering, checked against what each compiled assembly actually references rather than against the
/// project files. A project reference that nothing uses does not show up here, and a type used through a
/// transitive reference does, which is the dependency that matters.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(Names).Assembly;
    private static readonly Assembly Application = typeof(IngestPipeline).Assembly;
    private static readonly Assembly Infrastructure = typeof(TallyhouseOptions).Assembly;
    private static readonly Assembly Api = Assembly.Load("Tallyhouse.Api");
    private static readonly Assembly Loader = Assembly.Load("Tallyhouse.Loader");

    // What a layer below the adapters must never name: every one of these is a concrete technology.
    private static readonly string[] Technologies =
    [
        "ClickHouse.",
        "Confluent.",
        "Npgsql",
        "Microsoft.EntityFrameworkCore",
        "StackExchange.Redis",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions",
        "OpenTelemetry",
    ];

    private static string[] References(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(name => name.Name!)];

    private static string[] Projects(Assembly assembly) =>
        [.. References(assembly).Where(name => name.StartsWith("Tallyhouse.", StringComparison.Ordinal))];

    [Fact]
    public void The_domain_depends_on_no_other_project_and_nothing_outside_the_base_library() =>
        References(Domain).ShouldAllBe(name => name.StartsWith("System", StringComparison.Ordinal));

    [Fact]
    public void The_application_depends_only_on_the_domain() =>
        Projects(Application).ShouldBe(["Tallyhouse.Domain"]);

    [Fact]
    public void Neither_inner_layer_names_a_concrete_technology()
    {
        foreach (Assembly inner in (Assembly[])[Domain, Application])
        {
            References(inner).ShouldNotContain(
                name => Technologies.Any(technology => name.StartsWith(technology, StringComparison.Ordinal)),
                $"{inner.GetName().Name} references a technology");
        }
    }

    [Fact]
    public void Infrastructure_implements_the_application_and_knows_nothing_of_the_hosts()
    {
        Projects(Infrastructure).ShouldContain("Tallyhouse.Application");
        Projects(Infrastructure).ShouldNotContain("Tallyhouse.Api");
        Projects(Infrastructure).ShouldNotContain("Tallyhouse.Loader");
    }

    [Fact]
    public void The_two_hosts_do_not_depend_on_each_other()
    {
        Projects(Api).ShouldNotContain("Tallyhouse.Loader");
        Projects(Loader).ShouldNotContain("Tallyhouse.Api");
    }
}
