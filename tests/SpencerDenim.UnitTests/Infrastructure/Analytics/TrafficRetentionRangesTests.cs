using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Infrastructure.Analytics;

namespace SpencerDenim.UnitTests.Infrastructure.Analytics;

/// <summary>
/// #193's purge guard, the pure half: the UTC ranges the purge may delete are everything before the cut-off minus
/// each never-built Colombo day, whole day to whole day.
/// </summary>
public sealed class TrafficRetentionRangesTests
{
    private static readonly DateTime Start = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

    private static DateTime Midnight(int year, int month, int day) => ReportPeriod.ColomboMidnightUtc(new DateOnly(year, month, day));

    [Fact]
    public void With_no_unbuilt_day_there_is_one_range_up_to_the_cutoff()
    {
        var cutoff = Midnight(2026, 9, 1);

        Assert.Equal([(Start, cutoff)], TrafficRetention.Ranges(cutoff, []));
    }

    [Fact]
    public void An_unbuilt_day_is_cut_out_as_a_whole_Colombo_day()
    {
        var cutoff = Midnight(2026, 9, 1);

        var ranges = TrafficRetention.Ranges(cutoff, [new DateOnly(2026, 8, 10)]);

        Assert.Equal(
            [(Start, Midnight(2026, 8, 10)), (Midnight(2026, 8, 11), cutoff)],
            ranges);
    }

    [Fact]
    public void Adjacent_unbuilt_days_leave_no_empty_range_between_them()
    {
        var cutoff = Midnight(2026, 9, 1);

        var ranges = TrafficRetention.Ranges(
            cutoff,
            [new DateOnly(2026, 8, 11), new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 10)]);

        Assert.Equal(
            [(Start, Midnight(2026, 8, 10)), (Midnight(2026, 8, 12), cutoff)],
            ranges);
    }

    [Fact]
    public void An_unbuilt_day_on_or_after_the_cutoff_changes_nothing()
    {
        var cutoff = Midnight(2026, 9, 1);

        Assert.Equal([(Start, cutoff)], TrafficRetention.Ranges(cutoff, [new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 5)]));
    }

    [Fact]
    public void The_day_just_before_the_cutoff_leaves_nothing_after_it()
    {
        var cutoff = Midnight(2026, 9, 1);

        Assert.Equal([(Start, Midnight(2026, 8, 31))], TrafficRetention.Ranges(cutoff, [new DateOnly(2026, 8, 31)]));
    }

    [Fact]
    public void Every_range_is_non_empty_and_ends_by_the_cutoff()
    {
        var cutoff = Midnight(2026, 9, 1);
        var days = Enumerable.Range(0, 40).Select(offset => new DateOnly(2026, 7, 1).AddDays(offset * 2)).ToList();

        var ranges = TrafficRetention.Ranges(cutoff, days);

        Assert.All(ranges, range => Assert.True(range.From < range.To));
        Assert.All(ranges, range => Assert.True(range.To <= cutoff));
        Assert.All(
            days.Where(day => Midnight(day.Year, day.Month, day.Day) < cutoff),
            day => Assert.DoesNotContain(ranges, range => range.From < Midnight(day.Year, day.Month, day.Day).AddHours(12) && Midnight(day.Year, day.Month, day.Day).AddHours(12) < range.To));
    }
}
