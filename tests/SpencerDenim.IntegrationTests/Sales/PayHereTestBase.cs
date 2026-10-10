using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Real PayHere orders against a real SQL Server, with no network at all (doc 11 P8b, issue #141).
/// <para>
/// <b>The orders are placed through the real checkout, over HTTP, choosing PayHere.</b> Nothing is
/// re-labelled: the provider row is switched on, the merchant account is saved the way the settings panel
/// saves it (the secret encrypted by the <i>host's</i> protector, which is the one that will decrypt it), and
/// the shop's public address is configured on the host — so the whole chain under test is the one a shopper
/// uses: the list of methods, the placement, the Initiated payment, the order page's signed form, and then
/// PayHere's notification posted to the real endpoint with a signature computed the way PayHere computes it.
/// </para>
/// <para>
/// <b>Everything a test changes outside the rows it owns is put back in <see cref="DisposeAsync"/></b> — the
/// provider row to disabled, the five settings to the catalogue's resting values, the alerts it caused — for
/// the reason <c>BankTransferTestBase.ClearBankDetailsAsync</c> gives: a left-behind enabled provider is a
/// neighbour's fixture.
/// </para>
/// </summary>
public abstract class PayHereTestBase(SqlServerDatabaseFixture sqlServer) : BankTransferTestBase(sqlServer)
{
    protected const string Origin = "https://shop.example.test";
    protected const string MerchantId = "1211149";
    protected const string Secret = "IntegrationTestSecret-Not-A-Real-One";
    protected const string NotifyPath = "/api/payments/payhere/notify";

    private static readonly string[] PayHereKeys =
    [
        SettingKeys.PayHere.Mode,
        SettingKeys.PayHere.MerchantId,
        SettingKeys.PayHere.MerchantSecret,
        SettingKeys.PayHere.PendingMaxDays,
        SettingKeys.PayHere.ExpiryMinutes,
    ];

    private bool _touchedPayHere;

    /// <summary>
    /// The shop's public address as the server is configured with it, or <see langword="null"/> for a host
    /// that was never told (PayHere then cannot be given a return or notify address).
    /// </summary>
    protected virtual string? PublicOrigin => Origin;

    /// <summary>The host's configuration: the public address, blank when there is none.</summary>
    protected override IReadOnlyDictionary<string, string?>? AdditionalHostConfiguration =>
        new Dictionary<string, string?>(StringComparer.Ordinal) { ["Email:StorefrontBaseUrl"] = PublicOrigin ?? string.Empty };

    /// <summary>One placed PayHere order: its public token, number and the payment checkout opened for it.</summary>
    protected sealed record PayHereOrder(
        Guid PublicId,
        string OrderNumber,
        int OrderId,
        int PaymentId,
        int VariantId,
        decimal GrandTotal,
        OrderConfirmationDto Confirmation)
    {
        /// <summary>The <c>order_id</c> PayHere is given for the first attempt.</summary>
        public string OrderReference => PayHereOrderReference.Format(OrderNumber, PaymentId);
    }

    // ---- arranging the shop ------------------------------------------------------------------------

    /// <summary>
    /// Switches PayHere on and fills the merchant account in, as an administrator would have.
    /// </summary>
    protected async Task ConfigurePayHereAsync(
        bool enabled = true,
        string? mode = SettingKeys.PayHere.ModeSandbox,
        string? merchantId = MerchantId,
        string? secret = Secret,
        string? pendingMaxDays = null,
        string? expiryMinutes = null)
    {
        _touchedPayHere = true;

        using var scope = CreateApplicationScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISettingSecretProtector>();

        await SetSettingAsync(db, SettingKeys.PayHere.Mode, mode);
        await SetSettingAsync(db, SettingKeys.PayHere.MerchantId, merchantId);
        await SetSettingAsync(db, SettingKeys.PayHere.MerchantSecret, secret is null ? null : protector.Protect(secret));

        if (pendingMaxDays is not null)
        {
            await SetSettingAsync(db, SettingKeys.PayHere.PendingMaxDays, pendingMaxDays);
        }

        if (expiryMinutes is not null)
        {
            await SetSettingAsync(db, SettingKeys.PayHere.ExpiryMinutes, expiryMinutes);
        }

        var switched = await db.PaymentProviders
            .Where(provider => provider.Code == PaymentProviderCodes.PayHere)
            .ExecuteUpdateAsync(provider => provider.SetProperty(row => row.IsEnabled, enabled), Token);

        Assert.Equal(1, switched);
    }

    private static async Task SetSettingAsync(AppDbContext db, string key, string? value)
    {
        var moved = await db.Settings
            .Where(setting => setting.Key == key)
            .ExecuteUpdateAsync(setting => setting.SetProperty(row => row.Value, value), CancellationToken.None);

        Assert.Equal(1, moved);
    }

    /// <summary>
    /// For a test that changes the PayHere settings through the admin endpoints rather than through
    /// <see cref="ConfigurePayHereAsync"/>: registers the restore without writing anything.
    /// </summary>
    protected void WillRestorePayHere() => _touchedPayHere = true;

    // ---- placing an order --------------------------------------------------------------------------

    /// <summary>
    /// Places an order that pays with PayHere through the real checkout, and returns it with the payment row
    /// the placement opened.
    /// </summary>
    protected async Task<PayHereOrder> PlacePayHereOrderAsync(int quantity = 1, int onHand = 10)
    {
        var (order, variantId) = await PlaceOrderWithAsync(PaymentProviderCodes.PayHere, quantity, onHand);

        return await DescribeAsync(order, variantId);
    }

    /// <summary>
    /// <see cref="BankTransferTestBase.PlaceOrderAsync"/> with a payment method of the test's choosing, so a
    /// refusal is observable as well as a success.
    /// </summary>
    protected async Task<(OrderConfirmationDto Order, int VariantId)> PlaceOrderWithAsync(
        string paymentMethodCode,
        int quantity = 1,
        int onHand = 10)
    {
        var (response, order, variantId) = await TryPlaceOrderWithAsync(paymentMethodCode, quantity, onHand);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(order);

        return (order, variantId);
    }

    protected async Task<(HttpResponseMessage Response, OrderConfirmationDto? Order, int VariantId)> TryPlaceOrderWithAsync(
        string paymentMethodCode,
        int quantity = 1,
        int onHand = 10)
    {
        // The same arrangement as BankTransferTestBase.PlaceOrderAsync: a variant of this test's own.
        _variantsUsed++;
        var variantId = (await CheckoutVariantIdsAsync(_variantsUsed))[_variantsUsed - 1];
        await SetStockAsync(variantId, onHand);

        var (_, _, api) = await NewBagAsync(variantId, quantity);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);

        var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));
        body["paymentMethodCode"] = paymentMethodCode;

        var (response, order) = await api.PlaceAsync(body, Token);

        return (response, order, variantId);
    }

    private int _variantsUsed;

    /// <summary>The methods checkout offers a shopper right now.</summary>
    protected async Task<IReadOnlyList<string>> OfferedMethodCodesAsync()
    {
        var variantId = await CheckoutVariantIdAsync();
        var (_, _, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (response, session) = await api.GetAsync(colombo, standard, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return session!.PaymentMethods.Select(method => method.Code).ToList();
    }

    private async Task<PayHereOrder> DescribeAsync(OrderConfirmationDto order, int variantId)
    {
        var orderId = await OrderIdAsync(order.PublicId);

        using var scope = CreateScope();
        var paymentId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .AsNoTracking()
            .Where(candidate => candidate.OrderId == orderId)
            .OrderBy(candidate => candidate.Id)
            .Select(candidate => candidate.Id)
            .FirstAsync(Token);

        return new PayHereOrder(order.PublicId, order.OrderNumber, orderId, paymentId, variantId, order.GrandTotal, order);
    }

    // ---- PayHere's notification --------------------------------------------------------------------

    /// <summary>
    /// The posted form of a notification, signed the way PayHere signs it. Every argument can be bent to
    /// make the request an attack; the signature is computed over what is passed, unless one is given.
    /// </summary>
    protected static Dictionary<string, string> NotifyForm(
        string orderReference,
        decimal amount,
        string status = "2",
        string currency = "LKR",
        string? payHerePaymentId = null,
        string merchantId = MerchantId,
        string secret = Secret,
        string? signature = null,
        string? amountText = null)
    {
        var shown = amountText ?? PayHereHash.FormatAmount(amount);
        var paymentId = payHerePaymentId ?? NewPayHerePaymentId();

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["merchant_id"] = merchantId,
            ["order_id"] = orderReference,
            ["payment_id"] = paymentId,
            ["payhere_amount"] = shown,
            ["payhere_currency"] = currency,
            ["status_code"] = status,
            ["md5sig"] = signature
                ?? PayHereHash.NotificationSignature(merchantId, orderReference, shown, currency, status, secret),
            ["status_message"] = status == "2" ? "Successfully received the payment" : "Payment not completed",
            ["method"] = "VISA",
            ["card_holder_name"] = "Kamal Perera",
            ["card_no"] = "************1234",
            ["card_expiry"] = "12/30",
        };
    }

    protected static string NewPayHerePaymentId() => "32" + Guid.NewGuid().ToString("N")[..14];

    /// <summary>POSTs a notification to the real endpoint, as PayHere's server would.</summary>
    protected async Task<HttpResponseMessage> PostNotifyAsync(IDictionary<string, string> form)
    {
        using var client = NewNotifyClient();

        return await client.PostAsync(
            new Uri(NotifyPath, UriKind.Relative),
            new FormUrlEncodedContent(form),
            Token);
    }

    protected HttpClient NewNotifyClient() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = BaseAddress, HandleCookies = false });

    // ---- reading what happened ---------------------------------------------------------------------

    protected async Task<List<PaymentEvent>> EventsAsync(int? paymentId = null, string? providerPaymentId = null)
    {
        using var scope = CreateScope();

        var query = scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentEvents
            .AsNoTracking()
            .Where(candidate => candidate.ProviderCode == PaymentProviderCodes.PayHere);

        if (paymentId is { } id)
        {
            query = query.Where(candidate => candidate.PaymentId == id);
        }

        if (providerPaymentId is not null)
        {
            query = query.Where(candidate => candidate.ProviderEventId == providerPaymentId);
        }

        return await query.OrderBy(candidate => candidate.Id).ToListAsync(Token);
    }

    protected async Task<int> PayHereEventCountAsync()
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentEvents
            .AsNoTracking()
            .CountAsync(candidate => candidate.ProviderCode == PaymentProviderCodes.PayHere, Token);
    }

    protected async Task<List<EmailOutboxMessage>> OutboxAsync(Guid publicId, string templateCode)
    {
        using var scope = CreateScope();
        var id = publicId.ToString("D");

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailOutbox
            .AsNoTracking()
            .Where(message => message.RelatedEntityId == id && message.TemplateCode == templateCode)
            .OrderBy(message => message.Id)
            .ToListAsync(Token);
    }

    protected async Task<List<AdminAlert>> AlertsAsync(Guid publicId)
    {
        using var scope = CreateScope();
        var id = publicId.ToString("D");

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminAlerts
            .AsNoTracking()
            .Where(alert => alert.RelatedEntityId == id)
            .OrderBy(alert => alert.Id)
            .ToListAsync(Token);
    }

    /// <summary>Removes the banners this test caused for one order, so a neighbour does not count them.</summary>
    protected async Task DeleteAlertsAsync(Guid publicId)
    {
        if (!SqlServer.IsAvailable)
        {
            return;
        }

        using var scope = SqlServer.CreateScope();
        var id = publicId.ToString("D");

        await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminAlerts
            .Where(alert => alert.RelatedEntityId == id)
            .ExecuteDeleteAsync(CancellationToken.None);
    }

    protected async Task<List<AdminAlert>> SettingsAlertsAsync()
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminAlerts
            .AsNoTracking()
            .Where(alert => alert.AlertType == AdminAlertTypes.SensitiveSettingsChanged)
            .OrderBy(alert => alert.Id)
            .ToListAsync(Token);
    }

    protected async Task<List<Payment>> PaymentsAsync(Guid publicId)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .AsNoTracking()
            .Where(payment => payment.Order!.PublicId == publicId)
            .OrderBy(payment => payment.Id)
            .ToListAsync(Token);
    }

    protected async Task<List<AuditLog>> SettingAuditAsync()
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .Where(log => log.EntityType == SpencerDenim.Application.Settings.SettingsAuditTrail.EntityType
                && log.EntityId!.StartsWith("Payments.PayHere."))
            .OrderBy(log => log.Id)
            .ToListAsync(Token);
    }

    protected async Task<int> OrderDueMinutesFromNowAsync(Guid publicId)
    {
        var order = await ReadOrderAsync(publicId);

        return (int)Math.Round((order.PaymentDueAtUtc - DateTime.UtcNow).TotalMinutes);
    }

    protected async Task<JsonElement> OrderJsonAsync(Guid publicId)
    {
        using var client = NewNotifyClient();
        using var response = await client.GetAsync(new Uri($"/api/orders/{publicId:D}", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    // ---- putting it back ---------------------------------------------------------------------------

    public override async ValueTask DisposeAsync()
    {
        if (_touchedPayHere && SqlServer.IsAvailable)
        {
            using var scope = SqlServer.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await db.PaymentProviders
                .Where(provider => provider.Code == PaymentProviderCodes.PayHere)
                .ExecuteUpdateAsync(
                    provider => provider.SetProperty(row => row.IsEnabled, false),
                    CancellationToken.None);

            // The catalogue's resting values, not a guessed NULL: the seeder gives three of these a value.
            foreach (var key in PayHereKeys)
            {
                var resting = SettingCatalog.Find(key)!.SeedValue;

                await db.Settings
                    .Where(setting => setting.Key == key)
                    .ExecuteUpdateAsync(
                        setting => setting.SetProperty(row => row.Value, resting),
                        CancellationToken.None);
            }

            await db.AdminAlerts
                .Where(alert => alert.AlertType == AdminAlertTypes.SensitiveSettingsChanged
                    && alert.Title.StartsWith("PayHere payment settings"))
                .ExecuteDeleteAsync(CancellationToken.None);

            // The payment-exception banners these orders caused. Matched on the order's token, which is how
            // the alert names it; the two lists are small (a test's own orders and the earlier PayHere ones).
            var orderTokens = (await db.Orders
                    .AsNoTracking()
                    .Where(order => order.PaymentProviderCode == PaymentProviderCodes.PayHere)
                    .Select(order => order.PublicId)
                    .ToListAsync(CancellationToken.None))
                .Select(token => token.ToString("D"))
                .ToList();

            await db.AdminAlerts
                .Where(alert => alert.AlertType == AdminAlertTypes.PaymentException
                    && orderTokens.Contains(alert.RelatedEntityId!))
                .ExecuteDeleteAsync(CancellationToken.None);

            // And the one that names no order: a notification that could not be applied.
            await db.AdminAlerts
                .Where(alert => alert.AlertType == AdminAlertTypes.PaymentException
                    && alert.RelatedEntityId == null
                    && alert.Title.StartsWith("A PayHere notification"))
                .ExecuteDeleteAsync(CancellationToken.None);
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
