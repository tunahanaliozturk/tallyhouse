using System.Globalization;
using System.Text;

namespace Tallyhouse.Infrastructure.ClickHouse.Queries;

/// <summary>
/// Builds the funnel query for a given number of steps.
/// </summary>
/// <remarks>
/// <para>ClickHouse's <c>windowFunnel</c> computes how far each user got, but not how long it took, and time
/// to convert is half of what a funnel is for. So the same algorithm is written out here as an
/// <c>arrayFold</c> over each user's step events, carrying for every level the start of the chain that
/// reached it and the time it was reached. The integration suite checks the levels against
/// <c>windowFunnel</c> itself on randomised data, so the two cannot drift apart silently.</para>
/// <para>The algorithm, per user, over events sorted by time (and by step for simultaneous events):</para>
/// <list type="bullet">
/// <item>a first-step event inside the date range starts a chain at its own time, replacing any earlier
/// start, because a later start leaves more of the window for the remaining steps;</item>
/// <item>an event for step i extends the chain that reached step i - 1, if there is one and the event is
/// within the window of that chain's start;</item>
/// <item>the user's level is the deepest step any chain reached.</item>
/// </list>
/// <para>What made it fast enough, measured on 80 million step events (docs/benchmark-results): each event
/// is one UInt64, <c>ts_ms * 8 + step</c>, instead of a (timestamp, step) tuple, which halves what the
/// per-user aggregation carries and makes sorting a plain integer sort. And first-step events outside the
/// range are dropped before grouping, after which a run of consecutive first-step events can be cut to its
/// last element, because each one only ever replaces the one before it. For a funnel headed by page views
/// that shrinks the array a fold walks from dozens of elements per user to a handful.</para>
/// <para>The state is a flat tuple of 2k UInt64s: (start, reached) per level. Zero means "not reached";
/// no event is stamped at the Unix epoch.</para>
/// </remarks>
internal static class FunnelSql
{
    /// <summary>Three bits of step index in the packed value, which is why a funnel has at most eight steps.</summary>
    private const int StepBits = 8;

    public static string Build(int steps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(steps, StepBits);

        StringBuilder sql = new();
        sql.AppendLine("SELECT");

        List<string> outer = [];

        for (int level = 1; level <= steps; level++)
        {
            outer.Add(Invariant($"    countIf(level >= {level}) AS reached_{level}"));
        }

        for (int level = 2; level <= steps; level++)
        {
            outer.Add(Invariant($"    quantileExactIf(0.5)(duration_{level}, level >= {level}) AS median_ms_{level}"));
        }

        sql.AppendLine(string.Join(",\n", outer));
        sql.AppendLine("FROM");
        sql.AppendLine("(");
        sql.AppendLine("    SELECT");

        List<string> middle = [];
        middle.Add("        " + string.Join(" + ", Enumerable.Range(1, steps).Select(level => Invariant($"(state.{Start(level)} > 0)"))) + " AS level");

        for (int level = 2; level <= steps; level++)
        {
            middle.Add(Invariant($"        state.{Reached(level)} - state.{Start(level)} AS duration_{level}"));
        }

        sql.AppendLine(string.Join(",\n", middle));
        sql.AppendLine("    FROM");
        sql.AppendLine("    (");
        sql.AppendLine("        SELECT");
        sql.AppendLine("            arrayFold(");
        sql.AppendLine("                (acc, x) -> tuple(");

        List<string> next = [];

        for (int level = 1; level <= steps; level++)
        {
            string condition = level == 1
                ? Invariant($"x % {StepBits} = 0")
                : Invariant($"x % {StepBits} = {level - 1} AND acc.{Start(level - 1)} > 0 AND intDiv(x, {StepBits}) <= acc.{Start(level - 1)} + {{windowMs:UInt64}}");

            string startValue = level == 1 ? Invariant($"intDiv(x, {StepBits})") : Invariant($"acc.{Start(level - 1)}");

            next.Add(Invariant($"                    if({condition}, {startValue}, acc.{Start(level)})"));
            next.Add(Invariant($"                    if({condition}, intDiv(x, {StepBits}), acc.{Reached(level)})"));
        }

        sql.AppendLine(string.Join(",\n", next));
        sql.AppendLine("                ),");
        sql.AppendLine(Invariant($"                arrayFilter((x, following) -> NOT (x % {StepBits} = 0 AND following % {StepBits} = 0), sorted, arrayPushBack(arrayPopFront(sorted), toUInt64(1))),"));
        sql.AppendLine("                tuple(" + string.Join(", ", Enumerable.Repeat("toUInt64(0)", steps * 2)) + ")");
        sql.AppendLine("            ) AS state");
        sql.AppendLine("        FROM");
        sql.AppendLine("        (");
        sql.AppendLine(Invariant($"            SELECT arraySort(groupArray(toUInt64(toUnixTimestamp64Milli(ts)) * {StepBits} + toUInt64(indexOf({{steps:Array(String)}}, event_name) - 1))) AS sorted"));
        sql.AppendLine("            FROM events");
        sql.AppendLine("            WHERE project_id = {project:UUID}");
        sql.AppendLine("              AND event_name IN {steps:Array(String)}");
        sql.AppendLine("              AND ts >= {from:DateTime64(3, 'UTC')}");
        sql.AppendLine("              AND ts < {scanTo:DateTime64(3, 'UTC')}");
        sql.AppendLine("              AND (event_name != {steps:Array(String)}[1] OR ts < {to:DateTime64(3, 'UTC')})");
        sql.AppendLine("              AND NOT is_late");
        sql.AppendLine("              AND sample_bucket < {threshold:UInt16}");
        sql.AppendLine("            GROUP BY user_key");
        sql.AppendLine("        )");
        sql.AppendLine("    )");
        sql.AppendLine(")");

        return sql.ToString();
    }

    private static int Start(int level) => (2 * level) - 1;

    private static int Reached(int level) => 2 * level;

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
