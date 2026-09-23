using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.ADO.Readers;
using ClickHouse.Driver.Utility;
using Tallyhouse.Kernel;
using Tallyhouse.Kernel.Ingestion;
using Tallyhouse.Kernel.Queries;

namespace Tallyhouse.Infrastructure.ClickHouse.Queries;

/// <summary>
/// Funnel, retention, segment and session queries over ClickHouse. Every value from a request is a bound
/// parameter; the only text built from input is the shape of the query (how many steps, which filter
/// operators), and that comes from validated enums and counts.
/// </summary>
/// <remarks>
/// Duplicates that the fact table has not merged away yet are handled per query, by choosing measures that
/// cannot see them. Distinct users and funnel levels are the same whether an event appears once or twice,
/// so those queries skip FINAL. Event counts are not, so the segment query pays for FINAL. See ADR 0002.
/// </remarks>
public sealed class AnalyticsQueries(ClickHouseClient client)
{
    private static readonly Histogram<double> Duration = Telemetry.Meter.CreateHistogram<double>(
        "tallyhouse.query.duration", "s", "Analytics query time in ClickHouse, tagged by query kind.");

    private static readonly QueryOptions Bounded = new() { MaxExecutionTime = TimeSpan.FromSeconds(30) };

    private readonly ConcurrentDictionary<int, string> funnelSql = new();

    public async Task<FunnelResult> FunnelAsync(Guid projectId, FunnelQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        DateTime from = StartOf(query.From);
        DateTime to = StartOf(query.To.AddDays(1));
        TimeSpan window = TimeSpan.FromSeconds(query.WindowSeconds);

        Sampled sampling = await SamplingForAsync(projectId, query.Steps, query.From, DateOnly.FromDateTime(to + window), cancellationToken);

        ClickHouseParameterCollection parameters = new();
        parameters.AddParameter("project", projectId);
        parameters.AddParameter("steps", query.Steps.ToArray());
        parameters.AddParameter("from", from);
        parameters.AddParameter("scanTo", to + window);
        parameters.AddParameter("toMs", (ulong)new DateTimeOffset(to).ToUnixTimeMilliseconds());
        parameters.AddParameter("windowMs", (ulong)window.TotalMilliseconds);
        parameters.AddParameter("threshold", sampling.Threshold);

        string sql = funnelSql.GetOrAdd(query.Steps.Count, FunnelSql.Build);
        int k = query.Steps.Count;
        long[] reached = new long[k];
        double?[] medians = new double?[k];

        using (Timed("funnel"))
        using (ClickHouseDataReader reader = await client.ExecuteReaderAsync(sql, parameters, Bounded, cancellationToken))
        {
            if (reader.Read())
            {
                for (int level = 0; level < k; level++)
                {
                    reached[level] = (long)Math.Round(Convert.ToUInt64(reader.GetValue(level), CultureInfo.InvariantCulture) * sampling.Scale);
                }

                for (int level = 1; level < k; level++)
                {
                    medians[level] = reached[level] == 0 ? null : Convert.ToDouble(reader.GetValue(k + level - 1), CultureInfo.InvariantCulture) / 1000.0;
                }
            }
        }

        FunnelStep[] steps = new FunnelStep[k];

        for (int level = 0; level < k; level++)
        {
            steps[level] = new FunnelStep(
                query.Steps[level],
                reached[level],
                level == 0 ? 1.0 : Ratio(reached[level], reached[level - 1]),
                Ratio(reached[level], reached[0]),
                level == 0 ? null : medians[level]);
        }

        return new FunnelResult(steps, sampling);
    }

    public async Task<RetentionResult> RetentionAsync(Guid projectId, RetentionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        DateOnly lastReturnDay = query.To.AddDays(query.Days);
        Sampled sampling = await SamplingForAsync(projectId, [query.StartEvent, query.ReturnEvent], query.From, lastReturnDay, cancellationToken);

        ClickHouseParameterCollection parameters = new();
        parameters.AddParameter("project", projectId);
        parameters.AddParameter("start", query.StartEvent);
        parameters.AddParameter("return", query.ReturnEvent);
        parameters.AddParameter("from", query.From);
        parameters.AddParameter("to", query.To);
        parameters.AddParameter("lastReturnDay", lastReturnDay);
        parameters.AddParameter("fromTs", StartOf(query.From));
        parameters.AddParameter("toTs", StartOf(query.To.AddDays(1)));
        parameters.AddParameter("lastReturnTs", StartOf(lastReturnDay.AddDays(1)));
        parameters.AddParameter("days", (ushort)query.Days);
        parameters.AddParameter("threshold", sampling.Threshold);

        string sql = query.Source == RetentionSource.Rollup ? RetentionFromRollup : RetentionFromEvents;
        List<RetentionCohort> cohorts = [];

        using (Timed(query.Source == RetentionSource.Rollup ? "retention" : "retention_events"))
        using (ClickHouseDataReader reader = await client.ExecuteReaderAsync(sql, parameters, Bounded, cancellationToken))
        {
            while (reader.Read())
            {
                ulong[] retained = (ulong[])reader.GetValue(2);
                cohorts.Add(new RetentionCohort(
                    DateOnly.FromDateTime(reader.GetDateTime(0)),
                    Scaled(Convert.ToUInt64(reader.GetValue(1), CultureInfo.InvariantCulture), sampling),
                    [.. retained.Select(count => Scaled(count, sampling))]));
            }
        }

        return new RetentionResult(cohorts, sampling);
    }

    public async Task<SegmentResult> SegmentAsync(Guid projectId, SegmentQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        Sampled sampling = await SamplingForAsync(projectId, query.Event is null ? null : [query.Event], query.From, query.To, cancellationToken);

        ClickHouseParameterCollection parameters = new();
        parameters.AddParameter("project", projectId);
        parameters.AddParameter("from", StartOf(query.From));
        parameters.AddParameter("to", StartOf(query.To.AddDays(1)));
        parameters.AddParameter("threshold", sampling.Threshold);
        parameters.AddParameter("scale", sampling.Scale);

        StringBuilder where = new();

        if (query.Event is not null)
        {
            parameters.AddParameter("event", query.Event);
            where.Append(" AND event_name = {event:String}");
        }

        AppendFilters(where, parameters, query.Filters ?? []);

        // Event counts weight each kept event by the inverse of its own sampling rate. User counts cannot be
        // weighted per event, so they count the smallest kept population and scale it once.
        string sql = $$"""
            SELECT
                toDate(ts) AS day,
                is_late,
                toInt64(round(sum({{Sampling.Buckets}} / sample_threshold))) AS events,
                toInt64(round(uniqExactIf(user_key, sample_bucket < {threshold:UInt16}) * {scale:Float64})) AS users
            FROM events FINAL
            WHERE project_id = {project:UUID}
              AND ts >= {from:DateTime64(3, 'UTC')}
              AND ts < {to:DateTime64(3, 'UTC')}{{where}}
            GROUP BY day, is_late
            ORDER BY day
            """;

        List<SegmentDay> days = [];
        long late = 0;

        using (Timed("segment"))
        using (ClickHouseDataReader reader = await client.ExecuteReaderAsync(sql, parameters, Bounded, cancellationToken))
        {
            while (reader.Read())
            {
                long events = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);

                if (reader.GetBoolean(1))
                {
                    late += events;
                    continue;
                }

                days.Add(new SegmentDay(DateOnly.FromDateTime(reader.GetDateTime(0)), events, Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture)));
            }
        }

        return new SegmentResult(days, late, sampling);
    }

    public async Task<SessionsResult> SessionsAsync(Guid projectId, SessionsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        ClickHouseParameterCollection parameters = new();
        parameters.AddParameter("project", projectId);
        parameters.AddParameter("from", query.From);
        parameters.AddParameter("to", query.To);

        const string sql = """
            SELECT
                day,
                count() AS sessions,
                uniqExact(user_key) AS users,
                quantileExact(0.5)(dateDiff('millisecond', session_start, session_end)) / 1000.0 AS median_seconds,
                avg(event_count) AS events_per_session
            FROM sessions
            WHERE project_id = {project:UUID}
              AND day >= {from:Date}
              AND day <= {to:Date}
            GROUP BY day
            ORDER BY day
            """;

        List<SessionsDay> days = [];

        using (Timed("sessions"))
        using (ClickHouseDataReader reader = await client.ExecuteReaderAsync(sql, parameters, Bounded, cancellationToken))
        {
            while (reader.Read())
            {
                days.Add(new SessionsDay(
                    DateOnly.FromDateTime(reader.GetDateTime(0)),
                    Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture)));
            }
        }

        return new SessionsResult(days);
    }

    /// <summary>
    /// The smallest sampling threshold among the events a query touches, read from the rollup, which is
    /// orders of magnitude smaller than the fact table. It reflects what was stored, not today's settings,
    /// so a rate changed last week still scales last month correctly.
    /// </summary>
    private async Task<Sampled> SamplingForAsync(Guid projectId, IReadOnlyList<string>? events, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        ClickHouseParameterCollection parameters = new();
        parameters.AddParameter("project", projectId);
        parameters.AddParameter("from", from);
        parameters.AddParameter("to", to);

        string filter = string.Empty;

        if (events is not null)
        {
            parameters.AddParameter("events", events.ToArray());
            filter = " AND event_name IN {events:Array(String)}";
        }

        object? threshold = await client.ExecuteScalarAsync(
            $"SELECT min(sample_threshold) FROM user_event_days WHERE project_id = {{project:UUID}} AND day >= {{from:Date}} AND day <= {{to:Date}}{filter}",
            parameters,
            Bounded,
            cancellationToken);

        return Sampled.At(Convert.ToUInt16(threshold, CultureInfo.InvariantCulture));
    }

    private static void AppendFilters(StringBuilder where, ClickHouseParameterCollection parameters, IReadOnlyList<PropertyFilter> filters)
    {
        for (int i = 0; i < filters.Count; i++)
        {
            PropertyFilter filter = filters[i];
            string key = $"f{i}k";
            string value = $"f{i}v";
            parameters.AddParameter(key, filter.Property);

            switch (filter.Operator)
            {
                case FilterOperator.Equal:
                    parameters.AddParameter(value, filter.Values[0]);
                    where.Append(CultureInfo.InvariantCulture, $" AND properties[{{{key}:String}}] = {{{value}:String}}");
                    break;

                case FilterOperator.NotEqual:
                    parameters.AddParameter(value, filter.Values[0]);
                    where.Append(CultureInfo.InvariantCulture, $" AND properties[{{{key}:String}}] != {{{value}:String}}");
                    break;

                case FilterOperator.In:
                    parameters.AddParameter(value, filter.Values.ToArray());
                    where.Append(CultureInfo.InvariantCulture, $" AND properties[{{{key}:String}}] IN {{{value}:Array(String)}}");
                    break;

                case FilterOperator.GreaterThan:
                case FilterOperator.LessThan:
                    parameters.AddParameter(value, double.Parse(filter.Values[0], NumberStyles.Float, CultureInfo.InvariantCulture));
                    string comparison = filter.Operator == FilterOperator.GreaterThan ? ">" : "<";
                    where.Append(CultureInfo.InvariantCulture, $" AND toFloat64OrNull(properties[{{{key}:String}}]) {comparison} {{{value}:Float64}}");
                    break;
            }
        }
    }

    private static DateTime StartOf(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static double Ratio(long part, long whole) => whole == 0 ? 0 : (double)part / whole;

    private static long Scaled(ulong count, Sampled sampling) => (long)Math.Round(count * sampling.Scale);

    private static TimedScope Timed(string kind) => new(kind);

    private readonly struct TimedScope(string kind) : IDisposable
    {
        private readonly long started = System.Diagnostics.Stopwatch.GetTimestamp();

        public void Dispose() =>
            Duration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("kind", kind));
    }

    // A user belongs to the cohort of the first day in the range they did the start event, so every user is
    // counted in exactly one cohort. The return days are collected per user once and tested with has(),
    // which keeps this to a single join however many days are asked for.
    private const string RetentionTemplate = """
        SELECT
            start_day,
            count() AS users,
            sumForEach(arrayMap(k -> toUInt64(has(return_days, start_day + k)), range(toUInt16({days:UInt16}) + 1))) AS retained
        FROM
        (
            SELECT user_key, min(day) AS start_day
            FROM ({START_ROWS})
            GROUP BY user_key
        ) AS cohort
        LEFT JOIN
        (
            SELECT user_key, groupUniqArray(day) AS return_days
            FROM ({RETURN_ROWS})
            GROUP BY user_key
        ) AS returns USING (user_key)
        GROUP BY start_day
        ORDER BY start_day
        """;

    private static readonly string RetentionFromRollup = RetentionTemplate
        .Replace("{START_ROWS}", """
            SELECT user_key, day FROM user_event_days
            WHERE project_id = {project:UUID} AND event_name = {start:String}
              AND day >= {from:Date} AND day <= {to:Date}
              AND sample_bucket < {threshold:UInt16}
            """, StringComparison.Ordinal)
        .Replace("{RETURN_ROWS}", """
            SELECT user_key, day FROM user_event_days
            WHERE project_id = {project:UUID} AND event_name = {return:String}
              AND day >= {from:Date} AND day <= {lastReturnDay:Date}
              AND sample_bucket < {threshold:UInt16}
            """, StringComparison.Ordinal);

    private static readonly string RetentionFromEvents = RetentionTemplate
        .Replace("{START_ROWS}", """
            SELECT user_key, toDate(ts) AS day FROM events
            WHERE project_id = {project:UUID} AND event_name = {start:String}
              AND ts >= {fromTs:DateTime64(3, 'UTC')} AND ts < {toTs:DateTime64(3, 'UTC')}
              AND NOT is_late AND sample_bucket < {threshold:UInt16}
            """, StringComparison.Ordinal)
        .Replace("{RETURN_ROWS}", """
            SELECT user_key, toDate(ts) AS day FROM events
            WHERE project_id = {project:UUID} AND event_name = {return:String}
              AND ts >= {fromTs:DateTime64(3, 'UTC')} AND ts < {lastReturnTs:DateTime64(3, 'UTC')}
              AND NOT is_late AND sample_bucket < {threshold:UInt16}
            """, StringComparison.Ordinal);
}
