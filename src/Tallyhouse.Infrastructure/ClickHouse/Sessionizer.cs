using System.Diagnostics.Metrics;
using System.Globalization;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.ADO.Readers;
using ClickHouse.Driver.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tallyhouse.Kernel;

namespace Tallyhouse.Infrastructure.ClickHouse;

/// <summary>
/// Stitches events into sessions by inactivity gap. A session is a function of a user's events on one day,
/// so instead of maintaining session state incrementally as events stream past, the sessionizer recomputes
/// every (project, day) that received events and swaps the result in whole. A late event inside the
/// watermark that splits or merges two sessions is then handled by the same code as any other event,
/// because there is no state for it to corrupt.
/// </summary>
/// <remarks>
/// <para>Sessions end at midnight UTC, the way Google Analytics has always cut them. That is what makes a
/// day an independent unit of work; without it a session could chain across days indefinitely and no day
/// could be recomputed on its own.</para>
/// <para>Events arriving past the watermark are kept out. A closed day's sessions do not change.</para>
/// <para>Run one sessionizer per deployment. Two would recompute the same days and race each other's
/// partition swap; the result is still correct after the next pass, but it is wasted work.</para>
/// </remarks>
public sealed partial class Sessionizer(ClickHouseClient client, SessionOptions options, TimeProvider time, ILogger<Sessionizer> logger)
    : BackgroundService
{
    // A marker becomes visible to readers a little after its marked_at: the insert has to commit. Reading only
    // up to a few seconds ago means no marker can appear later behind the cursor.
    private static readonly TimeSpan VisibilityMargin = TimeSpan.FromSeconds(5);

    // session_dirty keeps three days. A restart re-reads all of it, which recomputes a little more than
    // necessary and misses nothing.
    private static readonly TimeSpan DirtyRetention = TimeSpan.FromDays(3);

    private static readonly Counter<long> Recomputed = Telemetry.Meter.CreateCounter<long>(
        "tallyhouse.sessions.recomputed_days", "{day}", "Project days whose sessions were recomputed.");

    private DateTime? cursor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.Interval, time);

        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogPassFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Recomputes every day marked dirty since the last pass. Returns how many days it recomputed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        // The upper bound comes from the ClickHouse clock, the same clock that stamped the markers. It travels
        // as Unix milliseconds: a zone-less DateTime64 comes back with an unspecified kind, and converting that
        // "to UTC" on a machine that is not on UTC silently moves the cursor by the local offset.
        long upToMs = Convert.ToInt64(await client.ExecuteScalarAsync(
            $"SELECT toUnixTimestamp64Milli(now64(3)) - {(long)VisibilityMargin.TotalMilliseconds}",
            cancellationToken: cancellationToken), CultureInfo.InvariantCulture);
        DateTime upTo = DateTime.UnixEpoch.AddMilliseconds(upToMs);

        DateTime from = cursor ?? upTo - DirtyRetention;

        ClickHouseParameterCollection parameters = new();
        parameters.AddParameter("from", from);
        parameters.AddParameter("upTo", upTo);

        List<(Guid Project, DateOnly Day)> dirty = [];

        using (ClickHouseDataReader reader = await client.ExecuteReaderAsync(
            "SELECT DISTINCT project_id, day FROM session_dirty WHERE marked_at > {from:DateTime64(3, 'UTC')} AND marked_at <= {upTo:DateTime64(3, 'UTC')}",
            parameters,
            cancellationToken: cancellationToken))
        {
            while (reader.Read())
            {
                dirty.Add((reader.GetGuid(0), DateOnly.FromDateTime(reader.GetDateTime(1))));
            }
        }

        foreach ((Guid project, DateOnly day) in dirty)
        {
            await RecomputeAsync(project, day, cancellationToken);
        }

        cursor = upTo;
        Recomputed.Add(dirty.Count);
        return dirty.Count;
    }

    /// <summary>
    /// Rebuilds one project's sessions for one day in a staging table and swaps the partition in atomically.
    /// Readers see the old sessions or the new ones, never a mixture.
    /// </summary>
    public async Task RecomputeAsync(Guid projectId, DateOnly day, CancellationToken cancellationToken)
    {
        // Partition expressions cannot take bound parameters. Both values are typed and formatted here, so
        // nothing from a request reaches this string.
        string partition = string.Create(CultureInfo.InvariantCulture, $"tuple(toUUID('{projectId:D}'), toDate('{day:yyyy-MM-dd}'))");

        ClickHouseParameterCollection parameters = new();
        parameters.AddParameter("project", projectId);
        parameters.AddParameter("start", day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        parameters.AddParameter("end", day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        parameters.AddParameter("day", day);
        parameters.AddParameter("gap", (ulong)options.InactivityGap.TotalMilliseconds);

        await client.ExecuteNonQueryAsync($"ALTER TABLE sessions_staging DROP PARTITION {partition}", cancellationToken: cancellationToken);
        await client.ExecuteNonQueryAsync(SessionsSql, parameters, cancellationToken: cancellationToken);
        await client.ExecuteNonQueryAsync($"ALTER TABLE sessions REPLACE PARTITION {partition} FROM sessions_staging", cancellationToken: cancellationToken);
        await client.ExecuteNonQueryAsync($"ALTER TABLE sessions_staging DROP PARTITION {partition}", cancellationToken: cancellationToken);
    }

    // FINAL because a duplicate that has not been merged away yet would add one to event_count. It never
    // changes where a session starts or ends: a duplicate has the same timestamp as its original.
    private const string SessionsSql = """
        INSERT INTO sessions_staging (project_id, day, user_key, user_id, session_start, session_end, event_count)
        SELECT
            {project:UUID},
            {day:Date},
            user_key,
            any(display_id),
            min(ts),
            max(ts),
            toUInt32(count())
        FROM
        (
            SELECT
                user_key,
                display_id,
                ts,
                sum(starts_session) OVER (PARTITION BY user_key ORDER BY ts ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS session_number
            FROM
            (
                SELECT
                    user_key,
                    if(user_id != '', user_id, anonymous_id) AS display_id,
                    ts,
                    dateDiff('millisecond',
                             lagInFrame(ts, 1, toDateTime64(0, 3, 'UTC')) OVER (PARTITION BY user_key ORDER BY ts ROWS BETWEEN 1 PRECEDING AND CURRENT ROW),
                             ts) > {gap:UInt64} AS starts_session
                FROM events FINAL
                WHERE project_id = {project:UUID}
                  AND ts >= {start:DateTime64(3, 'UTC')}
                  AND ts < {end:DateTime64(3, 'UTC')}
                  AND NOT is_late
            )
        )
        GROUP BY user_key, session_number
        """;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sessionizer pass failed; the same days will be picked up again on the next pass")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
