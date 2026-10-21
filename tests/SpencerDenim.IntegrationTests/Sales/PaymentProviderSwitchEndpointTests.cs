using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Payments.Admin;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Owner decision 2026-10-21: bank transfer switched off, PayPal the only payment method — "block only for now",
/// so behind a Super Admin switch (<c>Settings.Payments</c>), audited and alerting, that can turn it back on.
/// Over HTTP (#126): with bank transfer off, checkout neither offers nor accepts it and, with nothing else on,
/// refuses to take an order at all; orders already placed by bank transfer, and the staff approval queue, keep
/// working; switched back on, it works as before.
/// </summary>
/// <remarks>
/// The shared database has bank transfer switched on (the fixture's documented state). Every test here that
/// switches it puts it back in <see cref="DisposeAsync"/>.
/// </remarks>
public sealed class PaymentProviderSwitchEndpointTests(SqlServerDatabaseFixture sqlServer) : BankTransferTestBase(sqlServer)
{
    private const string ProvidersUrl = "/api/admin/settings/payment-providers";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DateTime _startedAt = DateTime.UtcNow.AddSeconds(-1);
    private bool? _bankOriginal;
    private bool? _payPalOriginal;
    private long? _auditBaseline;
    private bool _bankDetailsSet;
    private List<string>? _extrasSwitchedOff;
    private int? _alertBaseline;

    [Fact]
    public async Task A_Super_Admin_reads_every_method_with_whether_checkout_offers_it()
    {
        using var admin = await SuperAdminAsync();

        var panel = await admin.GetFromJsonAsync<PaymentProvidersAdminDto>(ProvidersUrl, Json, Token);

        Assert.NotNull(panel);
        // Other suites add providers of their own to the shared database; the two real ones come first.
        Assert.Equal(
            [PaymentProviderCodes.BankTransfer, PaymentProviderCodes.PayPal],
            panel.Providers.Select(provider => provider.Code).Where(code => code is PaymentProviderCodes.BankTransfer or PaymentProviderCodes.PayPal));

        var bank = panel.Providers.Single(provider => provider.Code == PaymentProviderCodes.BankTransfer);
        Assert.True(bank.IsEnabled);
        Assert.True(bank.IsOffered);
        Assert.False(string.IsNullOrEmpty(bank.RowVersion));
        Assert.True(panel.AnyOffered);
    }

    [Fact]
    public async Task Only_Settings_Payments_may_read_or_switch_a_method()
    {
        using var manager = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.View, Permissions.Settings.Edit);

        using var read = await manager.GetAsync(new Uri(ProvidersUrl, UriKind.Relative), Token);
        using var write = await manager.PutAsJsonAsync(
            $"{ProvidersUrl}/{PaymentProviderCodes.BankTransfer}",
            new { isEnabled = false, rowVersion = "AAAAAAAAAAA=" },
            Json,
            Token);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.True(await IsEnabledAsync(PaymentProviderCodes.BankTransfer));

        using var anonymous = Factory.CreateClient();
        using var unauthenticated = await anonymous.GetAsync(new Uri(ProvidersUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task Switching_bank_transfer_off_is_audited_alerts_the_Super_Admins_and_removes_it_from_checkout()
    {
        using var admin = await SuperAdminAsync();
        await RememberAsync();

        var panel = await SwitchAsync(admin, PaymentProviderCodes.BankTransfer, enabled: false, HttpStatusCode.OK);

        Assert.False(panel!.Providers.Single(provider => provider.Code == PaymentProviderCodes.BankTransfer).IsEnabled);
        Assert.False(await IsEnabledAsync(PaymentProviderCodes.BankTransfer));

        var audit = Assert.Single(await AuditAsync(PaymentProviderCodes.BankTransfer));
        Assert.Equal(AuditActions.SettingChange, audit.Action);
        Assert.Contains("\"isEnabled\":true", audit.BeforeJson, StringComparison.Ordinal);
        Assert.Contains("\"isEnabled\":false", audit.AfterJson, StringComparison.Ordinal);
        Assert.NotNull(audit.UserId);

        var alert = Assert.Single(await AlertsAsync());
        Assert.Contains("PaymentProviders.BankTransfer.IsEnabled", alert.DetailsJson, StringComparison.Ordinal);

        // The row records who switched it, which is what the seed rule reads as "an admin chose this".
        using var scope = CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentProviders
            .AsNoTracking()
            .SingleAsync(provider => provider.Code == PaymentProviderCodes.BankTransfer, Token);
        Assert.Equal(audit.UserId, row.UpdatedByUserId);

        // Checkout: not offered, and — with PayPal not set up in this database — nothing is.
        var (session, api, _) = await QuoteAsync();
        Assert.DoesNotContain(session.PaymentMethods, method => method.Code == PaymentProviderCodes.BankTransfer);
        Assert.Contains(CheckoutBlocker.NoPaymentMethod, session.Blockers);
    }

    [Fact]
    public async Task With_no_method_switched_on_checkout_refuses_the_order_and_writes_nothing()
    {
        using var admin = await SuperAdminAsync();
        await RememberAsync();
        await SwitchAsync(admin, PaymentProviderCodes.BankTransfer, enabled: false, HttpStatusCode.OK);

        var (session, api, variantId) = await QuoteAsync();
        var before = await OrderCountAsync();
        var reserved = await ReservedAsync(variantId);

        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var (response, _) = await api.PlaceAsync(
            ValidBody(session.CheckoutKey, standard, await DistrictIdAsync("Colombo"), QuotedTotal(session)),
            Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("No payment method is available", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Equal(before, await OrderCountAsync());
        Assert.Equal(reserved, await ReservedAsync(variantId));
    }

    [Fact]
    public async Task Existing_bank_transfer_orders_and_the_approval_queue_keep_working_with_it_switched_off()
    {
        // Placed while bank transfer was on, with the shop's bank details set so the order page has
        // instructions to show (without them it says the details are coming, switch or no switch).
        await SetBankDetailsAsync();
        _bankDetailsSet = true;
        var (waiting, _) = await PlaceOrderAsync();
        var (toApprove, _) = await PlaceOrderAsync();

        using var admin = await SuperAdminAsync();
        await RememberAsync();
        await SwitchAsync(admin, PaymentProviderCodes.BankTransfer, enabled: false, HttpStatusCode.OK);

        // The customer's order page still says how to pay it.
        var page = await NewCheckoutVisitor().Http.GetAsync(new Uri($"/api/orders/{waiting.PublicId:D}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var order = await page.Content.ReadFromJsonAsync<OrderConfirmationDto>(CheckoutApiClient.JsonOptions, Token);
        Assert.Equal(PaymentProviderCodes.BankTransfer, order!.PaymentMethodCode);
        Assert.NotNull(order.Payment);

        // Staff still see it in the queue and can approve another.
        using var staff = await MediaTestHost.SignInAsync(Factory, Permissions.Orders.View, Permissions.Orders.Approve);
        using var queue = await staff.GetAsync(
            new Uri($"/api/admin/orders/bank-transfers?search={waiting.OrderNumber}", UriKind.Relative),
            Token);
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);

        using var approve = await staff.PostAsJsonAsync(
            new Uri($"/api/admin/orders/bank-transfers/{toApprove.PublicId:D}/approve", UriKind.Relative),
            new { note = "Seen in the statement." },
            Json,
            Token);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal(OrderStatus.Paid, (await ReadOrderAsync(toApprove.PublicId)).Status);
    }

    [Fact]
    public async Task Switched_back_on_bank_transfer_works_as_before()
    {
        using var admin = await SuperAdminAsync();
        await RememberAsync();
        await SwitchAsync(admin, PaymentProviderCodes.BankTransfer, enabled: false, HttpStatusCode.OK);
        await SwitchAsync(admin, PaymentProviderCodes.BankTransfer, enabled: true, HttpStatusCode.OK);

        var (order, _) = await PlaceOrderAsync();

        Assert.Equal(PaymentProviderCodes.BankTransfer, order.PaymentMethodCode);
        Assert.Equal(2, (await AuditAsync(PaymentProviderCodes.BankTransfer)).Count);
    }

    [Fact]
    public async Task A_method_that_is_not_ready_cannot_be_switched_on()
    {
        using var admin = await SuperAdminAsync();
        await RememberAsync();

        // PayPal has no credentials or rate in this database.
        using var response = await PutAsync(admin, PaymentProviderCodes.PayPal, enabled: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not ready", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.False(await IsEnabledAsync(PaymentProviderCodes.PayPal));
        Assert.Empty(await AuditAsync(PaymentProviderCodes.PayPal));
    }

    [Fact]
    public async Task A_stale_or_missing_version_is_refused_and_writes_nothing()
    {
        using var admin = await SuperAdminAsync();
        await RememberAsync();

        var stale = (await admin.GetFromJsonAsync<PaymentProvidersAdminDto>(ProvidersUrl, Json, Token))!
            .Providers.Single(provider => provider.Code == PaymentProviderCodes.BankTransfer).RowVersion;

        await SwitchAsync(admin, PaymentProviderCodes.BankTransfer, enabled: false, HttpStatusCode.OK);

        using var withStale = await admin.PutAsJsonAsync(
            $"{ProvidersUrl}/{PaymentProviderCodes.BankTransfer}",
            new { isEnabled = true, rowVersion = stale },
            Json,
            Token);
        using var withNone = await admin.PutAsJsonAsync(
            $"{ProvidersUrl}/{PaymentProviderCodes.BankTransfer}",
            new { isEnabled = true },
            Json,
            Token);

        Assert.Equal(HttpStatusCode.Conflict, withStale.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, withNone.StatusCode);
        Assert.False(await IsEnabledAsync(PaymentProviderCodes.BankTransfer));
        Assert.Single(await AuditAsync(PaymentProviderCodes.BankTransfer));
    }

    [Fact]
    public async Task An_unknown_method_is_404_and_a_write_without_the_antiforgery_header_is_refused()
    {
        using var admin = await SuperAdminAsync();
        await RememberAsync();

        using var unknown = await PutAsync(admin, "Koko", enabled: true, rowVersion: "AAAAAAAAAAA=");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        admin.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);
        using var forged = await PutAsync(admin, PaymentProviderCodes.BankTransfer, enabled: false, rowVersion: "AAAAAAAAAAA=");
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.True(await IsEnabledAsync(PaymentProviderCodes.BankTransfer));
    }

    // ------------------------------------------------------------------------------------------- helpers

    private Task<HttpClient> SuperAdminAsync() =>
        MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments, Permissions.Settings.Edit, Permissions.Settings.View);

    private static async Task<PaymentProvidersAdminDto?> SwitchAsync(HttpClient client, string code, bool enabled, HttpStatusCode expected)
    {
        using var response = await PutAsync(client, code, enabled);

        Assert.True(
            response.StatusCode == expected,
            $"Expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Token)}");

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<PaymentProvidersAdminDto>(Json, Token)
            : null;
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string code, bool enabled, string? rowVersion = null)
    {
        rowVersion ??= (await client.GetFromJsonAsync<PaymentProvidersAdminDto>(ProvidersUrl, Json, Token))!
            .Providers.Single(provider => provider.Code == code).RowVersion;

        return await client.PutAsJsonAsync($"{ProvidersUrl}/{code}", new { isEnabled = enabled, rowVersion }, Json, Token);
    }

    private async Task<(CheckoutSessionDto Session, CheckoutApiClient Api, int VariantId)> QuoteAsync()
    {
        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);
        var (_, _, api) = await NewBagAsync(variantId);
        var (_, session) = await api.GetAsync(
            await DistrictIdAsync("Colombo"),
            await DeliveryMethodIdAsync(DeliveryMethod.StandardCode),
            Token);

        return (session!, api, variantId);
    }

    private async Task RememberAsync()
    {
        _bankOriginal ??= await IsEnabledAsync(PaymentProviderCodes.BankTransfer);
        _payPalOriginal ??= await IsEnabledAsync(PaymentProviderCodes.PayPal);

        // Other suites leave test providers in the shared database; one left switched on would be "offered"
        // and hide the no-method case. Switched off here and put back in DisposeAsync.
        if (_extrasSwitchedOff is null)
        {
            using var scope = CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _extrasSwitchedOff = await db.PaymentProviders
                .Where(provider => provider.IsEnabled
                    && provider.Code != PaymentProviderCodes.BankTransfer
                    && provider.Code != PaymentProviderCodes.PayPal)
                .Select(provider => provider.Code)
                .ToListAsync(Token);
            await db.PaymentProviders
                .Where(provider => _extrasSwitchedOff.Contains(provider.Code))
                .ExecuteUpdateAsync(update => update.SetProperty(provider => provider.IsEnabled, false), Token);
        }

        // Ids, not a time window: the previous test in this class can have written within the same second.
        if (_auditBaseline is null)
        {
            using var scope = CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _auditBaseline = await db.AuditLogs.MaxAsync(log => (long?)log.Id, Token) ?? 0;
            _alertBaseline = await db.AdminAlerts.MaxAsync(alert => (int?)alert.Id, Token) ?? 0;
        }
    }

    private async Task<bool> IsEnabledAsync(string code)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentProviders
            .AsNoTracking()
            .Where(provider => provider.Code == code)
            .Select(provider => provider.IsEnabled)
            .SingleAsync(Token);
    }

    private async Task<List<AuditLog>> AuditAsync(string code)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .Where(log => log.EntityType == "PaymentProvider" && log.EntityId == code && log.Id > (_auditBaseline ?? long.MaxValue))
            .OrderBy(log => log.Id)
            .ToListAsync(Token);
    }

    private async Task<List<AdminAlert>> AlertsAsync()
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminAlerts
            .AsNoTracking()
            .Where(alert => alert.AlertType == AdminAlertTypes.SensitiveSettingsChanged
                && alert.Id > (_alertBaseline ?? int.MaxValue)
                && alert.DetailsJson!.Contains("PaymentProviders."))
            .ToListAsync(Token);
    }

    public override async ValueTask DisposeAsync()
    {
        if (SqlServer.IsAvailable && (_bankOriginal is not null || _payPalOriginal is not null))
        {
            using var scope = SqlServer.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (_bankOriginal is { } bank)
            {
                await db.PaymentProviders
                    .Where(provider => provider.Code == PaymentProviderCodes.BankTransfer)
                    .ExecuteUpdateAsync(update => update.SetProperty(provider => provider.IsEnabled, bank), CancellationToken.None);
            }

            if (_payPalOriginal is { } payPal)
            {
                await db.PaymentProviders
                    .Where(provider => provider.Code == PaymentProviderCodes.PayPal)
                    .ExecuteUpdateAsync(update => update.SetProperty(provider => provider.IsEnabled, payPal), CancellationToken.None);
            }

            if (_extrasSwitchedOff is { Count: > 0 } extras)
            {
                await db.PaymentProviders
                    .Where(provider => extras.Contains(provider.Code))
                    .ExecuteUpdateAsync(update => update.SetProperty(provider => provider.IsEnabled, true), CancellationToken.None);
            }

            // The alerts this class raised would otherwise sit on every later suite's banner reads.
            await db.AdminAlerts
                .Where(alert => alert.AlertType == AdminAlertTypes.SensitiveSettingsChanged
                    && alert.CreatedAtUtc >= _startedAt
                    && alert.DetailsJson!.Contains("PaymentProviders."))
                .ExecuteDeleteAsync(CancellationToken.None);
        }

        if (_bankDetailsSet)
        {
            await ClearBankDetailsAsync();
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
