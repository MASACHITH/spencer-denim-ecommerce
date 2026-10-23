using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Delivery.Admin;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// The admin Overseas delivery screen (owner decision 2026-10-21) over HTTP (#126): <c>Settings.View</c> reads,
/// <c>Settings.Edit</c> changes, every change is audited with its before and after values, a stale screen is
/// refused, and what is saved is what checkout charges.
/// </summary>
public sealed class AdminOverseasDeliveryEndpointTests(SqlServerDatabaseFixture sqlServer) : CheckoutTestBase(sqlServer)
{
    private const string Url = "/api/admin/delivery/overseas";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DateTime _startedAt = DateTime.UtcNow.AddSeconds(-1);
    private readonly List<Func<AppDbContext, Task>> _restore = [];

    [Fact]
    public async Task Settings_View_reads_the_switch_the_six_regions_and_every_overseas_country()
    {
        using var viewer = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.View);

        var screen = await viewer.GetFromJsonAsync<OverseasDeliveryAdminDto>(Url, Json, Token);

        Assert.NotNull(screen);
        Assert.True(screen.InternationalEnabled);
        Assert.False(string.IsNullOrEmpty(screen.InternationalRowVersion));
        Assert.Equal(
            [DeliveryRegion.Asia, DeliveryRegion.MiddleEast, DeliveryRegion.Europe, DeliveryRegion.NorthAmerica, DeliveryRegion.Oceania, DeliveryRegion.RestOfWorld],
            screen.Regions.Select(region => region.Code));
        Assert.Equal(248, screen.Countries.Count);
        Assert.DoesNotContain(screen.Countries, country => country.Code == "LK");
        Assert.Equal(248, screen.Regions.Sum(region => region.CountryCount));

        var europe = screen.Regions.Single(region => region.Code == DeliveryRegion.Europe);
        var gb = screen.Countries.Single(country => country.Code == "GB");
        Assert.Equal((DeliveryRegion.Europe, true, europe.Fee), (gb.RegionCode, gb.IsAvailable, gb.EffectiveFee));
        Assert.Equal("None", gb.Refusal);

        var aq = screen.Countries.Single(country => country.Code == "AQ");
        Assert.False(aq.IsAvailable);
        Assert.Equal("CountryDisabled", aq.Refusal);
    }

    [Fact]
    public async Task Without_a_settings_permission_the_screen_and_its_writes_are_refused()
    {
        using var staff = await MediaTestHost.SignInAsync(Factory, Permissions.Orders.View);
        using var viewer = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.View);

        using var read = await staff.GetAsync(new Uri(Url, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

        var region = await RegionAsync(viewer, DeliveryRegion.Asia);
        using var write = await viewer.PutAsJsonAsync(
            $"{Url}/regions/{DeliveryRegion.Asia}",
            new { fee = 1m, estimatedDaysMin = 1, estimatedDaysMax = 2, isEnabled = true, rowVersion = region.RowVersion },
            Json,
            Token);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        using var anonymous = Factory.CreateClient();
        using var unauthenticated = await anonymous.GetAsync(new Uri(Url, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task A_region_change_is_saved_audited_and_charged_at_checkout()
    {
        using var editor = await EditorAsync();
        await RememberRegionAsync(DeliveryRegion.Asia);
        var region = await RegionAsync(editor, DeliveryRegion.Asia);

        using var response = await editor.PutAsJsonAsync(
            $"{Url}/regions/{DeliveryRegion.Asia}",
            new { fee = 7_250.50m, estimatedDaysMin = 4, estimatedDaysMax = 9, isEnabled = true, rowVersion = region.RowVersion },
            Json,
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var screen = await response.Content.ReadFromJsonAsync<OverseasDeliveryAdminDto>(Json, Token);
        var saved = screen!.Regions.Single(row => row.Code == DeliveryRegion.Asia);
        Assert.Equal((7_250.50m, (byte)4, (byte)9), (saved.Fee!.Value, saved.EstimatedDaysMin, saved.EstimatedDaysMax));
        Assert.NotEqual(region.RowVersion, saved.RowVersion);

        var audit = Assert.Single(await AuditAsync("DeliveryRegion", DeliveryRegion.Asia));
        Assert.Equal(AuditActions.SettingChange, audit.Action);
        Assert.Contains("\"fee\":\"7250.50\"", audit.AfterJson, StringComparison.Ordinal);
        Assert.Contains("\"estimatedDaysMin\":4", audit.AfterJson, StringComparison.Ordinal);

        var option = await QuoteOptionAsync("SG");
        Assert.Equal((7_250.50m, (byte?)4, (byte?)9), (option.Fee!.Value, option.EstimatedDaysMin, option.EstimatedDaysMax));
    }

    [Fact]
    public async Task A_country_override_and_a_country_switch_are_saved_audited_and_charged()
    {
        using var editor = await EditorAsync();
        await RememberCountryAsync("CA");
        var country = await CountryAsync(editor, "CA");

        using var overridden = await editor.PutAsJsonAsync(
            $"{Url}/countries/ca",
            new { isShippingEnabled = true, deliveryFeeOverride = 15_000m, estimatedDaysMinOverride = 10, estimatedDaysMaxOverride = 20, rowVersion = country.RowVersion },
            Json,
            Token);

        Assert.Equal(HttpStatusCode.OK, overridden.StatusCode);
        var screen = await overridden.Content.ReadFromJsonAsync<OverseasDeliveryAdminDto>(Json, Token);
        var saved = screen!.Countries.Single(row => row.Code == "CA");
        Assert.Equal((15_000m, (byte?)10, (byte?)20), (saved.EffectiveFee!.Value, saved.EffectiveDaysMin, saved.EffectiveDaysMax));
        Assert.Equal(15_000m, (await QuoteOptionAsync("CA")).Fee);

        using var disabled = await editor.PutAsJsonAsync(
            $"{Url}/countries/CA",
            new { isShippingEnabled = false, deliveryFeeOverride = 15_000m, estimatedDaysMinOverride = 10, estimatedDaysMaxOverride = 20, rowVersion = saved.RowVersion },
            Json,
            Token);

        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Contains(CheckoutBlocker.CountryNotServed, (await QuoteAsync("CA")).Blockers);
        Assert.Equal(2, (await AuditAsync("Country", "CA")).Count);
    }

    [Fact]
    public async Task International_delivery_switched_off_closes_every_country_and_is_audited()
    {
        using var editor = await EditorAsync();
        await RememberInternationalAsync();
        var screen = await editor.GetFromJsonAsync<OverseasDeliveryAdminDto>(Url, Json, Token);

        using var response = await editor.PutAsJsonAsync(
            $"{Url}/international",
            new { isEnabled = false, rowVersion = screen!.InternationalRowVersion },
            Json,
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await response.Content.ReadFromJsonAsync<OverseasDeliveryAdminDto>(Json, Token);
        Assert.False(after!.InternationalEnabled);
        Assert.All(after.Countries, country => Assert.Equal("MethodDisabled", country.Refusal));
        Assert.Contains(CheckoutBlocker.CountryNotServed, (await QuoteAsync("GB")).Blockers);
        Assert.Single(await AuditAsync("DeliveryMethod", DeliveryMethod.InternationalCode));
    }

    [Theory]
    [InlineData(-1, 1, 2, "fee")]
    [InlineData(1_000_000.01, 1, 2, "fee")]
    [InlineData(1.005, 1, 2, "fee")]
    [InlineData(100, 0, 2, "estimatedDays")]
    [InlineData(100, 5, 91, "estimatedDays")]
    [InlineData(100, 6, 5, "estimatedDays")]
    public async Task An_invalid_region_is_refused_by_field_and_writes_nothing(double fee, int min, int max, string field)
    {
        using var editor = await EditorAsync();
        var region = await RegionAsync(editor, DeliveryRegion.Europe);

        using var response = await editor.PutAsJsonAsync(
            $"{Url}/regions/{DeliveryRegion.Europe}",
            new { fee = (decimal)fee, estimatedDaysMin = min, estimatedDaysMax = max, isEnabled = true, rowVersion = region.RowVersion },
            Json,
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(region.Fee, (await RegionAsync(editor, DeliveryRegion.Europe)).Fee);
        Assert.Empty(await AuditAsync("DeliveryRegion", DeliveryRegion.Europe));
    }

    [Fact]
    public async Task A_half_days_override_is_refused()
    {
        using var editor = await EditorAsync();
        var country = await CountryAsync(editor, "DE");

        using var response = await editor.PutAsJsonAsync(
            $"{Url}/countries/DE",
            new { isShippingEnabled = true, estimatedDaysMinOverride = 3, rowVersion = country.RowVersion },
            Json,
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_stale_or_missing_version_is_a_409_and_an_unknown_code_or_Sri_Lanka_is_a_404()
    {
        using var editor = await EditorAsync();
        await RememberRegionAsync(DeliveryRegion.RestOfWorld);
        var stale = await RegionAsync(editor, DeliveryRegion.RestOfWorld);

        using (var first = await editor.PutAsJsonAsync(
            $"{Url}/regions/{DeliveryRegion.RestOfWorld}",
            new { fee = 13_000m, estimatedDaysMin = 10, estimatedDaysMax = 21, isEnabled = true, rowVersion = stale.RowVersion },
            Json,
            Token))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var second = await editor.PutAsJsonAsync(
            $"{Url}/regions/{DeliveryRegion.RestOfWorld}",
            new { fee = 1m, estimatedDaysMin = 10, estimatedDaysMax = 21, isEnabled = true, rowVersion = stale.RowVersion },
            Json,
            Token);
        using var missing = await editor.PutAsJsonAsync(
            $"{Url}/regions/{DeliveryRegion.RestOfWorld}",
            new { fee = 1m, estimatedDaysMin = 10, estimatedDaysMax = 21, isEnabled = true },
            Json,
            Token);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal(13_000m, (await RegionAsync(editor, DeliveryRegion.RestOfWorld)).Fee);

        using var unknown = await editor.PutAsJsonAsync(
            $"{Url}/regions/MARS",
            new { fee = 1m, estimatedDaysMin = 1, estimatedDaysMax = 2, isEnabled = true, rowVersion = stale.RowVersion },
            Json,
            Token);
        using var sriLanka = await editor.PutAsJsonAsync(
            $"{Url}/countries/LK",
            new { isShippingEnabled = false, rowVersion = stale.RowVersion },
            Json,
            Token);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, sriLanka.StatusCode);
    }

    [Fact]
    public async Task A_write_without_the_antiforgery_header_is_refused()
    {
        using var editor = await EditorAsync();
        var region = await RegionAsync(editor, DeliveryRegion.Asia);
        editor.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);

        using var response = await editor.PutAsJsonAsync(
            $"{Url}/regions/{DeliveryRegion.Asia}",
            new { fee = 1m, estimatedDaysMin = 1, estimatedDaysMax = 2, isEnabled = true, rowVersion = region.RowVersion },
            Json,
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------- helpers

    private Task<HttpClient> EditorAsync() =>
        MediaTestHost.SignInAsync(Factory, Permissions.Settings.View, Permissions.Settings.Edit);

    private static async Task<DeliveryRegionAdminDto> RegionAsync(HttpClient client, string code) =>
        (await client.GetFromJsonAsync<OverseasDeliveryAdminDto>(Url, Json, Token))!.Regions.Single(region => region.Code == code);

    private static async Task<CountryDeliveryAdminDto> CountryAsync(HttpClient client, string code) =>
        (await client.GetFromJsonAsync<OverseasDeliveryAdminDto>(Url, Json, Token))!.Countries.Single(country => country.Code == code);

    private async Task<CheckoutSessionDto> QuoteAsync(string countryCode)
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        var (_, _, api) = await NewBagAsync(variantId);
        var (_, session) = await api.GetAsync(null, await DeliveryMethodIdAsync(DeliveryMethod.InternationalCode), Token, countryCode: countryCode);

        return session!;
    }

    private async Task<DeliveryOptionDto> QuoteOptionAsync(string countryCode) =>
        Assert.Single((await QuoteAsync(countryCode)).DeliveryOptions);

    private async Task<List<AuditLog>> AuditAsync(string entityType, string entityId)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .Where(log => log.EntityType == entityType && log.EntityId == entityId && log.OccurredAtUtc >= _startedAt)
            .OrderBy(log => log.Id)
            .ToListAsync(Token);
    }

    private async Task RememberRegionAsync(string code)
    {
        using var scope = CreateScope();
        var original = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DeliveryRegions
            .AsNoTracking()
            .SingleAsync(region => region.Code == code, Token);

        _restore.Add(db => db.DeliveryRegions.Where(region => region.Code == code).ExecuteUpdateAsync(
            update => update
                .SetProperty(region => region.Fee, original.Fee)
                .SetProperty(region => region.EstimatedDaysMin, original.EstimatedDaysMin)
                .SetProperty(region => region.EstimatedDaysMax, original.EstimatedDaysMax)
                .SetProperty(region => region.IsEnabled, original.IsEnabled),
            CancellationToken.None));
    }

    private async Task RememberCountryAsync(string code)
    {
        using var scope = CreateScope();
        var original = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Countries
            .AsNoTracking()
            .SingleAsync(country => country.Code == code, Token);

        _restore.Add(db => db.Countries.Where(country => country.Code == code).ExecuteUpdateAsync(
            update => update
                .SetProperty(country => country.IsShippingEnabled, original.IsShippingEnabled)
                .SetProperty(country => country.DeliveryFeeOverride, original.DeliveryFeeOverride)
                .SetProperty(country => country.EstimatedDaysMinOverride, original.EstimatedDaysMinOverride)
                .SetProperty(country => country.EstimatedDaysMaxOverride, original.EstimatedDaysMaxOverride),
            CancellationToken.None));
    }

    private Task RememberInternationalAsync()
    {
        _restore.Add(db => db.DeliveryMethods
            .Where(method => method.Code == DeliveryMethod.InternationalCode)
            .ExecuteUpdateAsync(update => update.SetProperty(method => method.IsEnabled, true), CancellationToken.None));

        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_restore.Count > 0 && SqlServer.IsAvailable)
        {
            using var scope = SqlServer.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            foreach (var restore in _restore)
            {
                await restore(db);
            }

            _restore.Clear();
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
