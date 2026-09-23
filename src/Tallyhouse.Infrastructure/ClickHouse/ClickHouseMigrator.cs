using System.Reflection;
using System.Text.RegularExpressions;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.ADO.Readers;
using ClickHouse.Driver.Utility;
using Microsoft.Extensions.Logging;

namespace Tallyhouse.Infrastructure.ClickHouse;

/// <summary>
/// Applies the numbered SQL files under <c>ClickHouse/Schema</c>, each once, in order. ClickHouse has no
/// transactional DDL, so every statement in them is written to be safe to run twice (<c>IF NOT EXISTS</c>)
/// and a file is recorded as applied only after all of its statements succeeded.
/// </summary>
public sealed partial class ClickHouseMigrator(ClickHouseClient client, ILogger<ClickHouseMigrator> logger)
{
    private const string ResourcePrefix = "clickhouse/";

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await client.ExecuteNonQueryAsync(
            "CREATE TABLE IF NOT EXISTS schema_migrations (name String, applied_at DateTime64(3, 'UTC') DEFAULT now64(3)) ENGINE = MergeTree ORDER BY name",
            cancellationToken: cancellationToken);

        HashSet<string> applied = [];

        using (ClickHouseDataReader reader = await client.ExecuteReaderAsync("SELECT name FROM schema_migrations", cancellationToken: cancellationToken))
        {
            while (reader.Read())
            {
                applied.Add(reader.GetString(0));
            }
        }

        foreach ((string name, string script) in Scripts())
        {
            if (applied.Contains(name))
            {
                continue;
            }

            foreach (string statement in Statements(script))
            {
                await client.ExecuteNonQueryAsync(statement, cancellationToken: cancellationToken);
            }

            ClickHouseParameterCollection parameters = new();
            parameters.AddParameter("name", name);
            await client.ExecuteNonQueryAsync("INSERT INTO schema_migrations (name) VALUES ({name:String})", parameters, cancellationToken: cancellationToken);

            LogApplied(logger, name);
        }
    }

    internal static IReadOnlyList<(string Name, string Sql)> Scripts()
    {
        Assembly assembly = typeof(ClickHouseMigrator).Assembly;

        return [.. assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using Stream stream = assembly.GetManifestResourceStream(name)!;
                using StreamReader reader = new(stream);
                return (name[ResourcePrefix.Length..], reader.ReadToEnd());
            })];
    }

    /// <summary>
    /// ClickHouse's HTTP interface runs one statement per request. The scripts contain no semicolons inside
    /// literals or at the end of a comment line, so a semicolon ending a line is a statement boundary.
    /// </summary>
    internal static IEnumerable<string> Statements(string script) =>
        StatementBoundary().Split(script)
            .Select(statement => statement.Trim())
            .Where(statement => statement.Split('\n').Any(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    [GeneratedRegex(@";[ \t]*(?:\r?\n|$)")]
    private static partial Regex StatementBoundary();

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied ClickHouse migration {Name}")]
    private static partial void LogApplied(ILogger logger, string name);
}
