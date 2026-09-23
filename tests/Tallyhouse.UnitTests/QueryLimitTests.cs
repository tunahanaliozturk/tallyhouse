using Tallyhouse.Application.Queries;

namespace Tallyhouse.UnitTests;

public sealed class QueryLimitTests
{
    private static readonly DateOnly Day = new(2026, 9, 1);

    [Fact]
    public void A_sensible_funnel_passes() =>
        new FunnelQuery(["signup", "activate", "purchase"], Day, Day.AddDays(29), 7 * 86_400).Problems().ShouldBeEmpty();

    [Fact]
    public void A_funnel_step_can_appear_only_once() =>
        new FunnelQuery(["signup", "signup"], Day, Day, 60).Problems().ShouldContain(problem => problem.Contains("different event"));

    [Fact]
    public void A_funnel_has_at_most_eight_steps_because_the_step_is_packed_into_three_bits() =>
        new FunnelQuery([.. Enumerable.Range(0, 9).Select(i => $"e{i}")], Day, Day, 60).Problems().ShouldHaveSingleItem();

    [Theory]
    [InlineData(0, 63, true)]
    [InlineData(0, 64, false)]
    [InlineData(192, 63, true)]
    [InlineData(193, 63, false)]
    public void Retention_fits_the_per_user_masks(int rangeDays, int days, bool valid) =>
        new RetentionQuery("signup", "page_view", Day, Day.AddDays(rangeDays), days).Problems().Count.ShouldBe(valid ? 0 : 1);

    [Fact]
    public void A_range_ending_before_it_starts_is_refused() =>
        new SegmentQuery(null, Day, Day.AddDays(-1), null).Problems().ShouldHaveSingleItem().ShouldContain("'to' must not be before 'from'");

    [Theory]
    [InlineData(FilterOperator.Equal, new[] { "a", "b" })]
    [InlineData(FilterOperator.In, new string[0])]
    [InlineData(FilterOperator.GreaterThan, new[] { "not a number" })]
    public void Filters_need_the_values_their_operator_needs(FilterOperator op, string[] values) =>
        new SegmentQuery("purchase", Day, Day, [new PropertyFilter("amount", op, values)]).Problems().ShouldHaveSingleItem();
}
