using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.UnitTests.Domain.Analytics;

/// <summary>
/// The precedence rule for the traffic purge's retention periods (issue #193): a settings row inside the form's
/// bounds wins; anything else falls back to configuration; bots are never kept longer than people.
/// </summary>
public sealed class TrafficRetentionPeriodsTests
{
    [Fact]
    public void Valid_rows_win_over_configuration()
    {
        var periods = TrafficRetentionPeriods.Resolve("120", "14", 90, 7);

        Assert.Equal(
            new TrafficRetentionPeriods(120, 14, TrafficRetentionSource.Settings, TrafficRetentionSource.Settings),
            periods);
    }

    /// <summary>A row equal to the configured figure is still reported as the row: the source is the row, not the number.</summary>
    [Fact]
    public void A_row_equal_to_configuration_is_still_the_rows_value()
    {
        var periods = TrafficRetentionPeriods.Resolve("90", "7", 90, 7);

        Assert.Equal(TrafficRetentionSource.Settings, periods.RawSource);
        Assert.Equal(TrafficRetentionSource.Settings, periods.BotSource);
    }

    [Fact]
    public void Missing_rows_fall_back_to_configuration()
    {
        var periods = TrafficRetentionPeriods.Resolve(null, null, 60, 5);

        Assert.Equal(
            new TrafficRetentionPeriods(60, 5, TrafficRetentionSource.Configuration, TrafficRetentionSource.Configuration),
            periods);
    }

    [Fact]
    public void Each_period_falls_back_on_its_own()
    {
        var periods = TrafficRetentionPeriods.Resolve("200", null, 90, 7);

        Assert.Equal(
            new TrafficRetentionPeriods(200, 7, TrafficRetentionSource.Settings, TrafficRetentionSource.Configuration),
            periods);

        periods = TrafficRetentionPeriods.Resolve(null, "30", 90, 7);

        Assert.Equal(
            new TrafficRetentionPeriods(90, 30, TrafficRetentionSource.Configuration, TrafficRetentionSource.Settings),
            periods);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ninety")]
    [InlineData("90 ")]
    [InlineData(" 90")]
    [InlineData("+90")]
    [InlineData("-90")]
    [InlineData("90.0")]
    [InlineData("9,0")]
    [InlineData("1e2")]
    [InlineData("٩٠")]
    [InlineData("99999999999")]
    [InlineData("29")]
    [InlineData("366")]
    [InlineData("4")]
    [InlineData("0")]
    public void A_raw_row_that_is_not_a_number_of_days_in_bounds_falls_back(string row)
    {
        var periods = TrafficRetentionPeriods.Resolve(row, null, 75, 7);

        Assert.Equal(75, periods.RawDays);
        Assert.Equal(TrafficRetentionSource.Configuration, periods.RawSource);
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("31", 31)]
    [InlineData("364", 364)]
    [InlineData("365", 365)]
    [InlineData("0090", 90)]
    public void A_raw_row_on_or_inside_the_bounds_is_used(string row, int expected)
    {
        var periods = TrafficRetentionPeriods.Resolve(row, null, 75, 7);

        Assert.Equal(expected, periods.RawDays);
        Assert.Equal(TrafficRetentionSource.Settings, periods.RawSource);
    }

    [Theory]
    [InlineData("6")]
    [InlineData("91")]
    [InlineData("4")]
    [InlineData("")]
    [InlineData("seven")]
    public void A_bot_row_outside_its_bounds_falls_back(string row)
    {
        var periods = TrafficRetentionPeriods.Resolve(null, row, 90, 5);

        Assert.Equal(5, periods.BotDays);
        Assert.Equal(TrafficRetentionSource.Configuration, periods.BotSource);
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("8", 8)]
    [InlineData("89", 89)]
    [InlineData("90", 90)]
    public void A_bot_row_on_or_inside_its_bounds_is_used(string row, int expected)
    {
        var periods = TrafficRetentionPeriods.Resolve(null, row, 90, 5);

        Assert.Equal(expected, periods.BotDays);
        Assert.Equal(TrafficRetentionSource.Settings, periods.BotSource);
    }

    /// <summary>
    /// The form refuses this pair, but the purge can still meet it: a valid bot row beside a raw row that fell back
    /// to a shorter configured figure. Bots are cut to the raw period rather than kept longer than people.
    /// </summary>
    [Fact]
    public void Bots_are_cut_to_the_raw_period_when_the_pair_would_keep_them_longer()
    {
        var periods = TrafficRetentionPeriods.Resolve("bad", "60", 40, 7);

        Assert.Equal(40, periods.RawDays);
        Assert.Equal(40, periods.BotDays);
    }

    [Fact]
    public void Bots_equal_to_people_are_left_alone()
    {
        var periods = TrafficRetentionPeriods.Resolve("60", "60", 90, 7);

        Assert.Equal((60, 60), (periods.RawDays, periods.BotDays));
    }

    // ---- independent tester (issue #193) ---------------------------------------------------------------

    /// <summary>
    /// The cap is against the <b>resolved</b> raw period, not the configured one. A valid raw row of 30 beside a
    /// valid bot row of 60 (scripted in by hand; the form refuses it) must purge bots at 30 — capping against the
    /// configured 90 would keep bot rows twice as long as people's. The developer's cap test had the raw period
    /// falling back to configuration, so it could not tell the two apart.
    /// </summary>
    [Fact]
    public void Bots_are_cut_to_the_raw_row_when_the_raw_row_is_what_is_in_force()
    {
        var periods = TrafficRetentionPeriods.Resolve("30", "60", 90, 7);

        Assert.Equal((30, 30), (periods.RawDays, periods.BotDays));
    }

    /// <summary>
    /// The same cap when the bot period itself comes from configuration: a configured 60 bot days (valid against
    /// the configured 90 raw at start-up) is cut to the 30 the raw row now says.
    /// </summary>
    [Fact]
    public void A_configured_bot_period_is_cut_to_a_shorter_raw_row()
    {
        var periods = TrafficRetentionPeriods.Resolve("30", null, 90, 60);

        Assert.Equal(
            new TrafficRetentionPeriods(30, 30, TrafficRetentionSource.Settings, TrafficRetentionSource.Configuration),
            periods);
    }

    /// <summary>Full-width and other non-ASCII digits are not invariant digits; a hand-written one falls back.</summary>
    [Theory]
    [InlineData("６０")]
    [InlineData("6 0")]
    [InlineData("\t60")]
    [InlineData("60\n")]
    [InlineData("2147483648")]
    public void A_raw_row_with_non_ascii_digits_or_control_characters_falls_back(string row)
    {
        var periods = TrafficRetentionPeriods.Resolve(row, null, 75, 7);

        Assert.Equal((75, TrafficRetentionSource.Configuration), (periods.RawDays, periods.RawSource));
    }

    /// <summary>
    /// <b>No row can push the purge below the form's floor.</b> Whatever the rows hold, the resolved raw period is
    /// either inside 30–365 or exactly the configured figure, and bots never exceed raw.
    /// </summary>
    [Theory]
    [InlineData("0", "0")]
    [InlineData("-1", "-1")]
    [InlineData("1", "1")]
    [InlineData("29", "6")]
    [InlineData("3650", "3650")]
    [InlineData("2147483647", "2147483647")]
    [InlineData("30", "365")]
    public void No_row_value_drives_either_period_outside_its_bounds_or_bots_past_raw(string raw, string bot)
    {
        var periods = TrafficRetentionPeriods.Resolve(raw, bot, 90, 7);

        Assert.True(periods.RawDays == 90 || periods.RawDays is >= 30 and <= 365, $"raw {periods.RawDays}");
        Assert.True(periods.BotDays == 7 || periods.BotDays is >= 7 and <= 90, $"bot {periods.BotDays}");
        Assert.True(periods.BotDays <= periods.RawDays);
    }

    [Theory]
    [InlineData(null, 4, 4, null)]
    [InlineData("4", 4, 4, 4)]
    [InlineData("3", 4, 4, null)]
    [InlineData("5", 4, 4, null)]
    [InlineData("5", 4, 5, 5)]
    public void TryReadDays_reads_digits_inside_inclusive_bounds(string? value, int min, int max, int? expected)
    {
        Assert.Equal(expected, TrafficRetentionPeriods.TryReadDays(value, min, max));
    }
}
