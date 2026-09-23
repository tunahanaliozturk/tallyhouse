using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.Utility;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tallyhouse.Infrastructure.ClickHouse;
using Tallyhouse.Infrastructure.Hosting;
using Tallyhouse.Infrastructure.Kafka;
using Testcontainers.ClickHouse;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(Tallyhouse.IntegrationTests.TestRig))]

namespace Tallyhouse.IntegrationTests;

/// <summary>
/// The whole pipeline against real infrastructure: Postgres, Redis, Kafka and ClickHouse in containers, the
/// collector as a real HTTP host, and the loader with its sessionizer in process. Nothing here is a fake;
/// the claims under test are about Kafka acknowledgements and ClickHouse merge semantics, and a substitute
/// for either would only prove that the substitute was configured.
/// </summary>
public sealed class TestRig : IAsyncLifetime
{
    public const string OperatorToken = "integration-operator-token";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly RedisContainer redis = new RedisBuilder("redis:8-alpine").Build();
    // 3.9 rather than the 4.x the compose stack runs: Testcontainers writes advertised.listeners with a
    // trailing comma, which Kafka 4 rejects at startup. The client protocol is the same for both.
    private readonly KafkaContainer kafka = new KafkaBuilder("apache/kafka:3.9.2").Build();

    public ClickHouseContainer ClickHouseContainer { get; } = new ClickHouseBuilder("clickhouse/clickhouse-server:26.8-alpine").Build();

    public ApiFactory Api { get; private set; } = null!;

    public IHost Loader { get; private set; } = null!;

    public ClickHouseClient ClickHouse { get; private set; } = null!;

    public Sessionizer Sessionizer => Loader.Services.GetRequiredService<Sessionizer>();

    public Dictionary<string, string?> Settings { get; private set; } = [];

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync(), kafka.StartAsync(), ClickHouseContainer.StartAsync());

        Settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = postgres.GetConnectionString(),
            ["ConnectionStrings:Redis"] = redis.GetConnectionString(),
            ["ConnectionStrings:ClickHouse"] = ClickHouseContainer.GetConnectionString(),
            ["ConnectionStrings:Kafka"] = new Uri(kafka.GetBootstrapAddress()).Authority,
            ["Tallyhouse:OperatorToken"] = OperatorToken,
            ["Tallyhouse:Kafka:DeliveryTimeout"] = "00:00:05",
            ["Tallyhouse:Loader:MaxBatchDelay"] = "00:00:00.200",

            // The tests run the sessionizer themselves, when the data they want sessionized is in.
            ["Tallyhouse:Sessions:Interval"] = "01:00:00",
        };

        ClickHouse = new ClickHouseClient(ClickHouseContainer.GetConnectionString());

        // The loader owns the ClickHouse schema, so it starts first and the collector can query at once.
        HostApplicationBuilder loader = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });

        // Several hosts share this process. The Windows event log provider breaks later log writes once one
        // host disposes it, so no host here logs anywhere.
        loader.Logging.ClearProviders();
        loader.Configuration.AddInMemoryCollection(Settings);
        loader.Services.AddTallyhouseLoader(loader.Configuration);
        Loader = loader.Build();
        await Loader.StartAsync();

        Api = new ApiFactory(Settings);
        _ = Api.Server;

        await WaitUntilAsync(() => Task.FromResult(Loader.Services.GetRequiredService<KafkaLoader>().Assigned), TimeSpan.FromSeconds(60));
    }

    public async ValueTask DisposeAsync()
    {
        // Any of these is null when a container failed to start; the failure that matters is already reported.
        if (Api is not null)
        {
            await Api.DisposeAsync();
        }

        if (Loader is not null)
        {
            await Loader.StopAsync();
            Loader.Dispose();
        }

        ClickHouse?.Dispose();
        await Task.WhenAll(
            postgres.DisposeAsync().AsTask(),
            redis.DisposeAsync().AsTask(),
            kafka.DisposeAsync().AsTask(),
            ClickHouseContainer.DisposeAsync().AsTask());
    }

    public HttpClient Client(string? bearer)
    {
        HttpClient client = Api.CreateClient();

        if (bearer is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client;
    }

    /// <summary>A fresh project with the schemas every scenario uses. Each test gets its own, so tests never see each other's data.</summary>
    public async Task<TestProject> NewProjectAsync(object? settings = null)
    {
        using HttpClient operatorClient = Client(OperatorToken);

        HttpResponseMessage created = await operatorClient.PostAsJsonAsync("/v1/projects", new { name = $"test-{Guid.NewGuid():N}", settings }, Json);
        created.EnsureSuccessStatusCode();
        JsonElement body = await created.Content.ReadFromJsonAsync<JsonElement>(Json);

        TestProject project = new(body.GetProperty("id").GetGuid(), body.GetProperty("writeKey").GetString()!, body.GetProperty("readKey").GetString()!);

        foreach ((string name, object spec) in Schemas.All)
        {
            HttpResponseMessage registered = await operatorClient.PutAsJsonAsync($"/v1/projects/{project.Id}/schemas/{name}/versions/1", spec, Json);
            registered.EnsureSuccessStatusCode();
        }

        return project;
    }

    public async Task<long> CountAsync(Guid projectId, string where = "1", bool final = true)
    {
        ClickHouseParameterCollection parameters = new();
        parameters.AddParameter("project", projectId);

        object? count = await ClickHouse.ExecuteScalarAsync(
            $"SELECT count() FROM events {(final ? "FINAL" : string.Empty)} WHERE project_id = {{project:UUID}} AND ({where})",
            parameters);

        return Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Waits until the loader has written <paramref name="expected"/> distinct events for the project.</summary>
    public Task WaitForEventsAsync(Guid projectId, long expected, TimeSpan? timeout = null) =>
        WaitUntilAsync(async () => await CountAsync(projectId) >= expected, timeout ?? TimeSpan.FromSeconds(30));

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        using CancellationTokenSource deadline = new(timeout);

        while (!await condition())
        {
            try
            {
                await Task.Delay(100, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Condition not met within {timeout}.");
            }
        }
    }
}

public sealed record TestProject(Guid Id, string WriteKey, string ReadKey);

public sealed class ApiFactory(IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach ((string key, string? value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.ClearProviders());
    }
}

/// <summary>The event types every test project registers, in the shape the operator endpoint takes.</summary>
public static class Schemas
{
    public static readonly (string Name, object Spec)[] All =
    [
        ("page_view", new { properties = new { path = new { type = "string", required = true }, referrer = new { type = "string" } } }),
        ("signup", new { properties = new { plan = new { type = "string", required = true, @enum = new[] { "free", "pro" } } } }),
        ("activate", new { properties = new { } }),
        ("purchase", new { properties = new { amount = new { type = "number", required = true }, currency = new { type = "string", @enum = new[] { "USD", "EUR", "TRY" } }, email = new { type = "string" } } }),
    ];
}
