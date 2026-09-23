using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Tallyhouse.Load;

/// <summary>Where the stack is. Defaults match the compose file, so on a laptop no flags are needed.</summary>
internal sealed record Endpoints(Uri Api, Uri ClickHouse, string ClickHouseUser, string ClickHousePassword, string ClickHouseDatabase, string OperatorToken)
{
    // 127.0.0.1, not localhost. On Windows, .NET resolves localhost to ::1 first, Docker Desktop publishes
    // ports on IPv4, and the failed IPv6 attempt added about 40 ms to every request the soak measured.
    public static Endpoints FromEnvironment() => new(
        new Uri(Environment.GetEnvironmentVariable("TALLYHOUSE_API") ?? "http://127.0.0.1:5180"),
        new Uri(Environment.GetEnvironmentVariable("CLICKHOUSE_URL") ?? "http://127.0.0.1:8123"),
        Environment.GetEnvironmentVariable("CLICKHOUSE_USER") ?? "tallyhouse",
        Environment.GetEnvironmentVariable("CLICKHOUSE_PASSWORD") ?? "tallyhouse",
        Environment.GetEnvironmentVariable("CLICKHOUSE_DATABASE") ?? "tallyhouse",
        Environment.GetEnvironmentVariable("TALLYHOUSE_OPERATOR_TOKEN") ?? "demo-operator-token");
}

internal sealed record BenchProject(Guid Id, string WriteKey, string ReadKey);

/// <summary>The public API, as a client sees it.</summary>
internal sealed class TallyhouseClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] SchemaDocuments =
    [
        """{"event": "page_view", "properties": {"path": {"type": "string", "required": true}, "referrer": {"type": "string"}}}""",
        """{"event": "signup", "properties": {"plan": {"type": "string", "required": true, "enum": ["free", "pro", "team"]}, "source": {"type": "string"}}}""",
        """{"event": "activate", "properties": {}}""",
        """{"event": "purchase", "properties": {"amount": {"type": "number", "required": true}, "currency": {"type": "string", "enum": ["USD", "EUR", "TRY"]}, "plan": {"type": "string"}}}""",
        """{"event": "feature_used", "properties": {"feature": {"type": "string", "required": true}}}""",
    ];

    public TallyhouseClient(Endpoints endpoints, int connections = 256)
    {
        Http = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = connections,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            BaseAddress = endpoints.Api,
            Timeout = TimeSpan.FromMinutes(2),
        };

        OperatorToken = endpoints.OperatorToken;
    }

    public HttpClient Http { get; }

    private string OperatorToken { get; }

    public async Task<BenchProject> CreateProjectAsync(string name, object settings)
    {
        using HttpRequestMessage create = new(HttpMethod.Post, "/v1/projects") { Content = JsonContent.Create(new { name, settings }, options: Json) };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OperatorToken);

        using HttpResponseMessage created = await Http.SendAsync(create);
        created.EnsureSuccessStatusCode();
        JsonElement body = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        BenchProject project = new(body.GetProperty("id").GetGuid(), body.GetProperty("writeKey").GetString()!, body.GetProperty("readKey").GetString()!);

        foreach (string document in SchemaDocuments)
        {
            using JsonDocument parsed = JsonDocument.Parse(document);
            string eventName = parsed.RootElement.GetProperty("event").GetString()!;
            string spec = $$"""{"properties": {{parsed.RootElement.GetProperty("properties").GetRawText()}}}""";

            using HttpRequestMessage put = new(HttpMethod.Put, $"/v1/projects/{project.Id}/schemas/{eventName}/versions/1")
            {
                Content = new StringContent(spec, Encoding.UTF8, "application/json"),
            };
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OperatorToken);

            using HttpResponseMessage registered = await Http.SendAsync(put);
            registered.EnsureSuccessStatusCode();
        }

        return project;
    }

    public async Task<JsonElement> QueryAsync(BenchProject project, string kind, object query)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"/v1/queries/{kind}") { Content = JsonContent.Create(query, options: Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", project.ReadKey);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"{kind} query failed with {(int)response.StatusCode}: {body}");
        }

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public void Dispose() => Http.Dispose();
}

/// <summary>ClickHouse's HTTP interface, for seeding and for counting what was actually stored.</summary>
internal sealed class ClickHouseHttp(Endpoints endpoints) : IDisposable
{
    private readonly HttpClient http = new() { BaseAddress = endpoints.ClickHouse, Timeout = TimeSpan.FromHours(1) };

    public async Task<string> QueryAsync(string sql, params (string Name, string Value)[] parameters)
    {
        StringBuilder query = new($"?database={Uri.EscapeDataString(endpoints.ClickHouseDatabase)}");

        foreach ((string name, string value) in parameters)
        {
            query.Append(CultureInfo.InvariantCulture, $"&param_{name}={Uri.EscapeDataString(value)}");
        }

        using HttpRequestMessage request = new(HttpMethod.Post, query.ToString()) { Content = new StringContent(sql, Encoding.UTF8) };
        request.Headers.Add("X-ClickHouse-User", endpoints.ClickHouseUser);
        request.Headers.Add("X-ClickHouse-Key", endpoints.ClickHousePassword);

        using HttpResponseMessage response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        return response.IsSuccessStatusCode ? body.Trim() : throw new InvalidOperationException($"ClickHouse: {body}");
    }

    public async Task<long> CountAsync(Guid projectId, string where = "1") =>
        long.Parse(await QueryAsync($"SELECT count() FROM events FINAL WHERE project_id = {{project:UUID}} AND ({where})", ("project", projectId.ToString())), CultureInfo.InvariantCulture);

    public void Dispose() => http.Dispose();
}

internal static class Stats
{
    /// <summary>Nearest-rank percentile over sorted samples.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted.Count == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1)];

    public static string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    public static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}

internal static class Arguments
{
    public static Dictionary<string, string> Parse(string[] args)
    {
        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                options[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            }
        }

        return options;
    }

    public static long Long(this Dictionary<string, string> options, string name, long fallback) =>
        options.TryGetValue(name, out string? value) ? long.Parse(value, CultureInfo.InvariantCulture) : fallback;

    public static double Double(this Dictionary<string, string> options, string name, double fallback) =>
        options.TryGetValue(name, out string? value) ? double.Parse(value, CultureInfo.InvariantCulture) : fallback;

    public static string String(this Dictionary<string, string> options, string name, string fallback) =>
        options.TryGetValue(name, out string? value) ? value : fallback;
}

internal static class BenchState
{
    private const string FileName = ".bench-project.json";

    public static async Task SaveAsync(BenchProject project, DateOnly start, int days) =>
        await File.WriteAllTextAsync(FileName, JsonSerializer.Serialize(new { project.Id, project.WriteKey, project.ReadKey, start, days }, TallyhouseClient.Json));

    public static async Task<(BenchProject Project, DateOnly Start, int Days)> LoadAsync()
    {
        if (!File.Exists(FileName))
        {
            throw new InvalidOperationException($"No {FileName} here. Run the seed command first.");
        }

        JsonElement saved = JsonDocument.Parse(await File.ReadAllTextAsync(FileName)).RootElement;
        return (new BenchProject(saved.GetProperty("id").GetGuid(), saved.GetProperty("writeKey").GetString()!, saved.GetProperty("readKey").GetString()!),
            DateOnly.Parse(saved.GetProperty("start").GetString()!, CultureInfo.InvariantCulture),
            saved.GetProperty("days").GetInt32());
    }
}
