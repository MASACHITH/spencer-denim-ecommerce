using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Admin.Reporting;

namespace SpencerDenim.UnitTests.Application.Admin.Reporting;

/// <summary>
/// The period every dashboard widget and report is asked about (doc 11 P16; doc 08 §1.4, §8.1): Colombo days,
/// both ends included, and a previous period of the same number of days immediately before.
/// </summary>
public sealed class ReportPeriodTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    [Fact]
    public void A_Colombo_day_runs_from_18_30_UTC_the_day_before_to_18_30_UTC()
    {
        var period = new ReportPeriod(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15));

        Assert.Equal(new DateTime(2026, 10, 14, 18, 30, 0, DateTimeKind.Utc), period.FromUtc);
        Assert.Equal(new DateTime(2026, 10, 15, 18, 30, 0, DateTimeKind.Utc), period.ToUtcExclusive);
        Assert.Equal(DateTimeKind.Utc, period.FromUtc.Kind);
    }

    [Theory]
    [InlineData("2026-10-14T18:29:59Z", "2026-10-14")]
    [InlineData("2026-10-14T18:30:00Z", "2026-10-15")]
    [InlineData("2026-12-31T18:30:00Z", "2027-01-01")]
    public void The_Colombo_day_of_an_instant(string instant, string day) =>
        Assert.Equal(
            DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture),
            ReportPeriod.ColomboDayOf(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Same length, immediately before — never "last month".</summary>
    [Theory]
    [InlineData("2026-10-01", "2026-10-31", "2026-08-31", "2026-09-30")] // 31 days against 31, not September's 30
    [InlineData("2026-03-01", "2026-03-31", "2026-01-29", "2026-02-28")] // February has 28 days in 2026
    [InlineData("2024-03-01", "2024-03-31", "2024-01-30", "2024-02-29")] // and 29 in 2024
    [InlineData("2026-10-09", "2026-10-15", "2026-10-02", "2026-10-08")] // the last 7 days against the 7 before
    [InlineData("2026-10-15", "2026-10-15", "2026-10-14", "2026-10-14")] // one day against the day before
    [InlineData("2026-01-01", "2026-12-31", "2025-01-01", "2025-12-31")] // 365 days
    public void The_previous_period_is_the_same_number_of_days_immediately_before(
        string from,
        string to,
        string previousFrom,
        string previousTo)
    {
        var period = new ReportPeriod(D(from), D(to));
        var previous = period.Previous;

        Assert.Equal(D(previousFrom), previous.From);
        Assert.Equal(D(previousTo), previous.To);
        Assert.Equal(period.Days, previous.Days);
        Assert.Equal(period.From.AddDays(-1), previous.To);
    }

    [Fact]
    public void With_no_dates_the_period_is_the_last_30_days_including_today()
    {
        var period = ReportPeriod.Parse(null, null, Today, new List<ValidationError>());

        Assert.NotNull(period);
        Assert.Equal(Today, period.To);
        Assert.Equal(new DateOnly(2026, 9, 16), period.From);
        Assert.Equal(30, period.Days);
    }

    [Fact]
    public void With_only_an_end_date_the_period_is_the_30_days_ending_there()
    {
        var period = ReportPeriod.Parse(null, "2026-02-28", Today, new List<ValidationError>());

        Assert.Equal(new DateOnly(2026, 1, 30), period!.From);
    }

    [Theory]
    [InlineData("2026-10-10", "2026-10-01", "to")]
    [InlineData("2025-10-14", "2026-10-15", "from")] // 367 days
    [InlineData("1/10/2026", null, "from")]
    [InlineData(null, "2026-02-30", "to")]
    [InlineData("1999-12-31", "2000-01-01", "from")]
    [InlineData("2099-12-31", "2100-01-01", "to")]
    [InlineData("0001-01-01", "0001-01-01", "from")]
    [InlineData("9999-12-31", "9999-12-31", "to")]
    public void A_period_that_cannot_be_answered_is_refused_naming_the_field(string? from, string? to, string field)
    {
        var errors = new List<ValidationError>();

        Assert.Null(ReportPeriod.Parse(from, to, Today, errors));
        Assert.Contains(errors, error => error.Field == field);
    }

    [Theory]
    [InlineData("2025-10-15", "2026-10-15", 366)]
    [InlineData("2000-01-01", "2000-01-01", 1)]
    [InlineData("2099-01-01", "2099-12-31", 365)]
    public void The_longest_and_the_edge_periods_are_answered(string from, string to, int days)
    {
        var errors = new List<ValidationError>();
        var period = ReportPeriod.Parse(from, to, Today, errors);

        Assert.Empty(errors);
        Assert.Equal(days, period!.Days);

        // The previous period converts to UTC without falling off the calendar.
        Assert.True(period.Previous.FromUtc < period.FromUtc);
    }

    [Fact]
    public void A_period_cannot_end_before_it_starts() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportPeriod(new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 1)));

    private static DateOnly D(string value) => DateOnly.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
