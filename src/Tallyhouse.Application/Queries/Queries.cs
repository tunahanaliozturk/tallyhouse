using System.Globalization;
using System.Text.Json.Serialization;
using Tallyhouse.Application.Ingestion;
using Tallyhouse.Domain;
using Tallyhouse.Domain.Events;

namespace Tallyhouse.Application.Queries;

/// <summary>
/// What a caller may ask. Each query checks its own bounds here, before any SQL exists, so a request that
/// would scan a year of data or join ten thousand values is refused with a reason instead of timing out.
/// Dates are whole UTC days, both ends inclusive.
/// </summary>
public static class QueryLimits
{
    public const int MaxRangeDays = 366;
    public const int MaxFunnelSteps = 8;

    /// <summary>A user's returns are one 64-bit mask counted from their first day, so at most 63 days follow it.</summary>
    public const int MaxRetentionDays = 63;

    /// <summary>First cohort to last day followed. The per-user day masks are 256 bits wide.</summary>
    public const int MaxRetentionSpanDays = 256;

    public const int MaxFilters = 10;
    public const int MaxFilterValues = 100;
    public static readonly TimeSpan MaxFunnelWindow = TimeSpan.FromDays(90);

    internal static void CheckRange(DateOnly from, DateOnly to, List<string> problems)
    {
        if (to < from)
        {
            problems.Add("'to' must not be before 'from'");
        }
        else if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
        {
            problems.Add($"a query may span at most {MaxRangeDays} days");
        }
    }

    internal static void CheckEvent(string? name, string field, List<string> problems)
    {
        if (name is null || !Names.IsValidEventName(name))
        {
            problems.Add($"'{field}' must be an event name");
        }
    }
}

/// <summary>An ordered funnel: users who did every step in order, the whole chain within the window.</summary>
/// <param name="WindowSeconds">The longest time from the first step to the last.</param>
public sealed record FunnelQuery(IReadOnlyList<string> Steps, DateOnly From, DateOnly To, long WindowSeconds)
{
    public IReadOnlyList<string> Problems()
    {
        List<string> problems = [];

        if (Steps is null || Steps.Count is < 2 or > QueryLimits.MaxFunnelSteps)
        {
            problems.Add($"a funnel has between 2 and {QueryLimits.MaxFunnelSteps} steps");
            return problems;
        }

        for (int i = 0; i < Steps.Count; i++)
        {
            QueryLimits.CheckEvent(Steps[i], $"steps[{i}]", problems);
        }

        // A repeated step would let one event satisfy two steps at once. Funnels like "viewed, viewed again"
        // need an ordinal that names cannot express, and that is not what this endpoint models.
        if (Steps.Distinct(StringComparer.Ordinal).Count() != Steps.Count)
        {
            problems.Add("each funnel step must be a different event");
        }

        if (WindowSeconds <= 0 || WindowSeconds > QueryLimits.MaxFunnelWindow.TotalSeconds)
        {
            problems.Add($"windowSeconds must be between 1 and {(long)QueryLimits.MaxFunnelWindow.TotalSeconds}");
        }

        QueryLimits.CheckRange(From, To, problems);
        return problems;
    }
}

/// <param name="Users">Users who reached this step (estimated when the steps are sampled).</param>
/// <param name="MedianSecondsFromStart">For users who reached this step, the median time since their first step.</param>
public sealed record FunnelStep(string Event, long Users, double ConversionFromPrevious, double ConversionFromStart, double? MedianSecondsFromStart);

public sealed record FunnelResult(IReadOnlyList<FunnelStep> Steps, Sampled Sampling);

/// <summary>
/// N-day retention: users grouped by the day they first did <see cref="StartEvent"/> in the range, and for
/// each following day, how many of them did <see cref="ReturnEvent"/> on it.
/// </summary>
/// <param name="Source">Where to read from. The rollup is the default and the fast path; <c>events</c> reads
/// the fact table and exists so the two can be compared, in tests and in the published benchmark.</param>
public sealed record RetentionQuery(string StartEvent, string ReturnEvent, DateOnly From, DateOnly To, int Days, RetentionSource Source = RetentionSource.Rollup)
{
    public IReadOnlyList<string> Problems()
    {
        List<string> problems = [];
        QueryLimits.CheckEvent(StartEvent, "startEvent", problems);
        QueryLimits.CheckEvent(ReturnEvent, "returnEvent", problems);
        QueryLimits.CheckRange(From, To, problems);

        if (Days is < 1 or > QueryLimits.MaxRetentionDays)
        {
            problems.Add($"days must be between 1 and {QueryLimits.MaxRetentionDays}");
        }
        else if (To >= From && To.DayNumber - From.DayNumber + Days >= QueryLimits.MaxRetentionSpanDays)
        {
            problems.Add($"from the first cohort to the last day followed may span at most {QueryLimits.MaxRetentionSpanDays} days");
        }

        return problems;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<RetentionSource>))]
public enum RetentionSource
{
    [JsonStringEnumMemberName("rollup")]
    Rollup,

    [JsonStringEnumMemberName("events")]
    Events,
}

/// <param name="Retained">Retained[k] is how many of the cohort did the return event k days after their start day.</param>
public sealed record RetentionCohort(DateOnly Day, long Users, IReadOnlyList<long> Retained);

public sealed record RetentionResult(IReadOnlyList<RetentionCohort> Cohorts, Sampled Sampling);

[JsonConverter(typeof(JsonStringEnumConverter<FilterOperator>))]
public enum FilterOperator
{
    [JsonStringEnumMemberName("eq")]
    Equal,

    [JsonStringEnumMemberName("neq")]
    NotEqual,

    [JsonStringEnumMemberName("in")]
    In,

    [JsonStringEnumMemberName("gt")]
    GreaterThan,

    [JsonStringEnumMemberName("lt")]
    LessThan,
}

public sealed record PropertyFilter(string Property, FilterOperator Operator, IReadOnlyList<string> Values);

/// <summary>Daily event and user counts, optionally for one event and narrowed by property filters.</summary>
public sealed record SegmentQuery(string? Event, DateOnly From, DateOnly To, IReadOnlyList<PropertyFilter>? Filters)
{
    public IReadOnlyList<string> Problems()
    {
        List<string> problems = [];

        if (Event is not null)
        {
            QueryLimits.CheckEvent(Event, "event", problems);
        }

        QueryLimits.CheckRange(From, To, problems);

        IReadOnlyList<PropertyFilter> filters = Filters ?? [];

        if (filters.Count > QueryLimits.MaxFilters)
        {
            problems.Add($"at most {QueryLimits.MaxFilters} filters");
        }

        foreach (PropertyFilter filter in filters)
        {
            if (filter?.Property is null || !Names.IsValidPropertyName(filter.Property))
            {
                problems.Add("each filter needs a valid property name");
                continue;
            }

            int count = filter.Values?.Count ?? 0;
            bool isIn = filter.Operator == FilterOperator.In;

            if ((!isIn && count != 1) || (isIn && count is < 1 or > QueryLimits.MaxFilterValues))
            {
                problems.Add($"filter on '{filter.Property}' needs {(isIn ? $"between 1 and {QueryLimits.MaxFilterValues} values" : "exactly one value")}");
            }
            else if (filter.Operator is FilterOperator.GreaterThan or FilterOperator.LessThan
                && !double.TryParse(filter.Values![0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                problems.Add($"filter on '{filter.Property}' compares numerically, so its value must be a number");
            }
        }

        return problems;
    }
}

public sealed record SegmentDay(DateOnly Day, long Events, long Users);

/// <param name="LateEvents">Events in the range that arrived after the watermark. They are not in the daily
/// buckets, which were closed when they arrived, and are reported here instead of being dropped.</param>
public sealed record SegmentResult(IReadOnlyList<SegmentDay> Days, long LateEvents, Sampled Sampling);

public sealed record SessionsQuery(DateOnly From, DateOnly To)
{
    public IReadOnlyList<string> Problems()
    {
        List<string> problems = [];
        QueryLimits.CheckRange(From, To, problems);
        return problems;
    }
}

public sealed record SessionsDay(DateOnly Day, long Sessions, long Users, double MedianDurationSeconds, double EventsPerSession);

public sealed record SessionsResult(IReadOnlyList<SessionsDay> Days);

/// <summary>
/// How a result was scaled. When any event involved is sampled, user counts come from the smallest kept
/// population and are multiplied by <see cref="Scale"/>; event counts are each weighted by their own rate.
/// </summary>
public sealed record Sampled(ushort Threshold, double Scale)
{
    public static Sampled None { get; } = new(Sampling.Buckets, 1.0);

    public bool IsEstimate => Threshold < Sampling.Buckets;

    public static Sampled At(ushort threshold) =>
        threshold is 0 or >= Sampling.Buckets ? None : new Sampled(threshold, Sampling.WeightOf(threshold));
}
