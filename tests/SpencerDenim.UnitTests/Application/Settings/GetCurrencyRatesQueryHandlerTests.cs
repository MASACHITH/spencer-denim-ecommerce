using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Application.Settings.Site;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Application.Settings;

/// <summary>
/// The public list of display currencies (issue #248): which rows become a currency, in what order, and what never
/// does. The real query and the real HTTP shape are covered by <c>CurrencyRatesEndpointTests</c>.
/// </summary>
public sealed class GetCurrencyRatesQueryHandlerTests
{
    private static readonly DateTime Stamp = new(2026, 10, 24, 10, 0, 0, DateTimeKind.Unspecified);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_rate_set_is_an_empty_list_not_an_error()
    {
        var offered = await Handle();

        Assert.Empty(offered);
    }

    [Fact]
    public async Task Only_currencies_with_a_rate_are_listed()
    {
        var offered = await Handle(
            Row(SettingKeys.CurrencyDisplay.GbpLkrPerUnit, "399.5"),
            Row(SettingKeys.CurrencyDisplay.UsdLkrPerUnit, null),
            Row(SettingKeys.CurrencyDisplay.EurLkrPerUnit, string.Empty));

        var gbp = Assert.Single(offered);
        Assert.Equal("GBP", gbp.Code);
        Assert.Equal("£", gbp.Symbol);
        Assert.Equal(399.5m, gbp.LkrPerUnit);
    }

    /// <summary>The order is the table's, whatever order the database returned the rows in.</summary>
    [Fact]
    public async Task Currencies_come_back_in_the_offered_order_whatever_order_the_rows_arrive_in()
    {
        var offered = await Handle(
            Row(SettingKeys.CurrencyDisplay.AedLkrPerUnit, "82.275"),
            Row(SettingKeys.CurrencyDisplay.AudLkrPerUnit, "205"),
            Row(SettingKeys.CurrencyDisplay.EurLkrPerUnit, "345"),
            Row(SettingKeys.CurrencyDisplay.GbpLkrPerUnit, "399.5"),
            Row(SettingKeys.CurrencyDisplay.UsdLkrPerUnit, "301.5"));

        Assert.Equal(["USD", "GBP", "EUR", "AUD", "AED"], offered.Select(rate => rate.Code));
        Assert.Equal(["US$", "£", "€", "A$", "AED"], offered.Select(rate => rate.Symbol));
        Assert.Equal([301.5m, 399.5m, 345m, 205m, 82.275m], offered.Select(rate => rate.LkrPerUnit));
    }

    /// <summary>
    /// A row can hold something the form would refuse today. A shopper would see a wrong price for it, so it is
    /// skipped and its neighbours are untouched.
    /// </summary>
    [Theory]
    [InlineData("garbage")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("0.0000")]
    [InlineData("1e3")]
    [InlineData("1,000")]
    [InlineData("399.12345")]
    [InlineData("1000000.01")]
    [InlineData("99999999999999999999999999999999")]
    [InlineData("NaN")]
    [InlineData("399.5 LKR")]
    public async Task A_stored_value_that_is_not_a_usable_rate_is_skipped_and_not_an_error(string stored)
    {
        var offered = await Handle(
            Row(SettingKeys.CurrencyDisplay.UsdLkrPerUnit, stored),
            Row(SettingKeys.CurrencyDisplay.GbpLkrPerUnit, "399.5"));

        var gbp = Assert.Single(offered);
        Assert.Equal("GBP", gbp.Code);
    }

    [Fact]
    public async Task A_stored_value_with_stray_spaces_at_its_ends_is_still_a_rate()
    {
        var offered = await Handle(Row(SettingKeys.CurrencyDisplay.UsdLkrPerUnit, " 301.5 "));

        Assert.Equal(301.5m, Assert.Single(offered).LkrPerUnit);
    }

    [Fact]
    public async Task The_key_is_matched_the_way_the_database_matches_it()
    {
        var offered = await Handle(Row(SettingKeys.CurrencyDisplay.UsdLkrPerUnit.ToUpperInvariant(), "301.5"));

        Assert.Equal("USD", Assert.Single(offered).Code);
    }

    /// <summary>
    /// PayPal's rate and any other row is not a display rate, even if a reader were ever to return it: the list is
    /// built from the table of five, not from whatever came back.
    /// </summary>
    [Fact]
    public async Task A_row_that_is_not_one_of_the_five_is_never_published()
    {
        var offered = await Handle(
            Row(SettingKeys.PayPal.LkrPerUsd, "320"),
            Row(SettingKeys.PayPal.FxBufferPercent, "3"),
            Row(SettingKeys.PayPal.ClientSecret, "secret"),
            Row("Currency.Display.JpyLkrPerUnit", "2.1"),
            Row(SettingKeys.CurrencyDisplay.EurLkrPerUnit, "345"));

        var eur = Assert.Single(offered);
        Assert.Equal("EUR", eur.Code);
    }

    [Fact]
    public async Task The_age_of_a_rate_is_the_rows_own_update_time_in_utc_and_may_be_unknown()
    {
        var offered = await Handle(
            Row(SettingKeys.CurrencyDisplay.UsdLkrPerUnit, "301.5", Stamp),
            Row(SettingKeys.CurrencyDisplay.GbpLkrPerUnit, "399.5", null));

        Assert.Equal(DateTimeKind.Utc, offered[0].UpdatedAtUtc!.Value.Kind);
        Assert.Equal(Stamp, offered[0].UpdatedAtUtc!.Value.ToUniversalTime());
        Assert.Null(offered[1].UpdatedAtUtc);
    }

    /// <summary>A rate is shown however old it is: nothing here ages a rate out (owner decision, 2026-10-24).</summary>
    [Fact]
    public async Task A_very_old_rate_is_still_offered()
    {
        var offered = await Handle(Row(SettingKeys.CurrencyDisplay.UsdLkrPerUnit, "301.5", new DateTime(2020, 1, 1)));

        Assert.Single(offered);
    }

    [Fact]
    public async Task A_row_repeated_by_the_reader_does_not_throw_or_double_a_currency()
    {
        var offered = await Handle(
            Row(SettingKeys.CurrencyDisplay.UsdLkrPerUnit, "301.5"),
            Row(SettingKeys.CurrencyDisplay.UsdLkrPerUnit, "301.5"));

        Assert.Single(offered);
    }

    /// <summary>
    /// <b>No sensitive-change alert</b> is a property of this save, not an omission (docs/02 and doc 08 §3.8: the
    /// rates move no money). The handler takes the store, the audit log, the permissions and the clock, and nothing
    /// that raises an alert, so a dependency added later has to be added to this list on purpose.
    /// </summary>
    [Fact]
    public void The_save_handler_takes_no_alerting_dependency()
    {
        var parameters = typeof(UpdateCurrencyRatesCommandHandler)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType.Name)
            .ToList();

        Assert.Equal(
            ["ISettingsStore", "IAuditLogWriter", "IEffectivePermissionsProvider", "IClock"],
            parameters);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<SpencerDenim.Application.Settings.Contracts.CurrencyRateDto>> Handle(
        params StoredCurrencyRate[] rows) =>
        await new GetCurrencyRatesQueryHandler(new FakeReader(rows)).HandleAsync(new GetCurrencyRatesQuery(), Token);

    private static StoredCurrencyRate Row(string key, string? value, DateTime? updatedAtUtc = null) =>
        new(key, value, updatedAtUtc);

    private sealed class FakeReader(IReadOnlyList<StoredCurrencyRate> rows) : ICurrencyRatesReader
    {
        public Task<IReadOnlyList<StoredCurrencyRate>> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(rows);
    }
}
