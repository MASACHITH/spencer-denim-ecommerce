using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales.PayPal;

/// <summary>
/// Everything a PayPal test needs: the API hosted with a <see cref="FakePayPalGateway"/> and a clock the test
/// moves, PayPal configured and switched on in the shared database, and real orders placed through real
/// checkout (PAY-02, doc 11 P8).
/// <para>
/// <b>The host is the real API with three substitutions</b> - the gateway, the clock and the background jobs.
/// The gateway is the fake, so nothing touches a network. The clock is ours, so a three-hour lock and a
/// twenty-four-hour recovery window are crossed with one call. The hosted services are removed, so no sweep
/// runs behind the test's back: the recovery job and the event-retry job are driven by calling their runners
/// directly, which is also what makes "two overlapping passes" a thing a test can arrange.
/// </para>
/// <para>
/// <b>Every test puts PayPal back as the seeder leaves it</b> (switched off, credentials and rate empty,
/// every number at its seeded value) in <see cref="DisposeAsync"/>, because the assembly shares one database
/// and a PayPal left switched on would be offered at the next class's checkout.
/// </para>
/// </summary>
public abstract class PayPalTestBase(SqlServerDatabaseFixture sqlServer) : BankTransferTestBase(sqlServer)
{
    /// <summary>What the storefront is, for the links the shop builds.</summary>
    protected const string Origin = "https://shop.test";

    internal const string ClientSecret = "TEST-CLIENT-SECRET-do-not-leak";

    private readonly List<HttpClient> _payPalClients = [];
    private readonly Dictionary<string, string?> _originalSettings = new(StringComparer.OrdinalIgnoreCase);

    private WebApplicationFactory<Program>? _payPalHost;
    private bool _providerSwitched;
    private int _payPalOrdersPlaced;

    internal FakePayPalGateway PayPal { get; } = new();

    internal TestClock Clock { get; } = new(DateTime.UtcNow);

    /// <summary>The API, with the fake PayPal, our clock and no background jobs.</summary>
    protected WebApplicationFactory<Program> PayPalHost =>
        _payPalHost ??= Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Email:StorefrontBaseUrl"] = Origin,
                }));

            builder.ConfigureTestServices(services =>
            {
                // No sweep runs behind a test's back; the runners are called by hand.
                services.RemoveAll<IHostedService>();

                services.RemoveAll<IPayPalGateway>();
                services.AddSingleton<IPayPalGateway>(PayPal);

                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(Clock);
            });
        });

    /// <summary>A browser: its own cookies, and redirects are reported and not followed.</summary>
    protected HttpClient NewPayPalClient()
    {
        var client = PayPalHost.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = BaseAddress,
            HandleCookies = true,
            AllowAutoRedirect = false,
        });

        _payPalClients.Add(client);
        return client;
    }

    /// <summary>Runs <paramref name="act"/> in a scope of the PayPal host (the fake gateway, our clock).</summary>
    protected async Task<T> InPayPalScopeAsync<T>(Func<IServiceProvider, Task<T>> act)
    {
        using var scope = PayPalHost.Services.CreateScope();
        return await act(scope.ServiceProvider);
    }

    /// <summary>
    /// PayPal as a Super Admin leaves it when it is ready: credentials, a webhook id, a rate set now, and the
    /// provider switched on.
    /// </summary>
    protected async Task ConfigurePayPalAsync(
        decimal? rate = 300m,
        DateTime? rateUpdatedAtUtc = null,
        bool credentials = true,
        bool enabled = true,
        decimal buffer = 3m,
        int? expiryMinutes = null)
    {
        await SetSettingAsync(SettingKeys.PayPal.Mode, "sandbox");

        if (credentials)
        {
            await SetSettingAsync(SettingKeys.PayPal.ClientId, "test-client-id");
            await SetSettingAsync(SettingKeys.PayPal.ClientSecret, await ProtectAsync(ClientSecret));
            await SetSettingAsync(SettingKeys.PayPal.WebhookId, "TEST-WEBHOOK-ID");
        }

        await SetSettingAsync(
            SettingKeys.PayPal.LkrPerUsd,
            rate?.ToString("0.00####", CultureInfo.InvariantCulture));
        await SetSettingAsync(
            SettingKeys.PayPal.RateUpdatedAtUtc,
            rate is null ? null : (rateUpdatedAtUtc ?? Clock.Now).ToString("O", CultureInfo.InvariantCulture));
        await SetSettingAsync(SettingKeys.PayPal.FxBufferPercent, buffer.ToString("0.00", CultureInfo.InvariantCulture));

        if (expiryMinutes is { } minutes)
        {
            await SetSettingAsync(SettingKeys.PayPal.ExpiryMinutes, minutes.ToString(CultureInfo.InvariantCulture));
        }

        await SwitchProviderAsync(enabled);
    }

    /// <summary>Writes one settings value, remembering what was there so <see cref="DisposeAsync"/> can put it back.</summary>
    protected async Task SetSettingAsync(string key, string? value)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!_originalSettings.ContainsKey(key))
        {
            _originalSettings[key] = await db.Settings
                .AsNoTracking()
                .Where(setting => setting.Key == key)
                .Select(setting => setting.Value)
                .SingleAsync(Token);
        }

        var updated = await db.Settings
            .Where(setting => setting.Key == key)
            .ExecuteUpdateAsync(setting => setting.SetProperty(row => row.Value, value), Token);

        Assert.Equal(1, updated);
    }

    /// <summary>Switches the PayPal row of <c>sales.PaymentProviders</c> on or off.</summary>
    protected async Task SwitchProviderAsync(bool enabled)
    {
        using var scope = CreateScope();

        var updated = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentProviders
            .Where(provider => provider.Code == PaymentProviderCodes.PayPal)
            .ExecuteUpdateAsync(provider => provider.SetProperty(row => row.IsEnabled, enabled), Token);

        Assert.Equal(1, updated);
        _providerSwitched = true;
    }

    /// <summary>The ciphertext the settings table holds for <paramref name="plaintext"/>, from the host's own protector.</summary>
    protected Task<string> ProtectAsync(string plaintext) =>
        InPayPalScopeAsync(services =>
            Task.FromResult(services.GetRequiredService<ISettingSecretProtector>().Protect(plaintext)));

    /// <summary>A placed PayPal order and its one payment.</summary>
    protected sealed record PayPalOrder(
        Guid PublicId,
        string OrderNumber,
        int OrderId,
        int PaymentId,
        int VariantId,
        decimal GrandTotal,
        decimal UsdAmount);

    /// <summary>
    /// Places a real order through real checkout, paying by PayPal. PayPal must be configured and switched on
    /// (<see cref="ConfigurePayPalAsync"/>) or checkout will refuse the method, which is the point of the
    /// tests that arrange it that way.
    /// </summary>
    protected async Task<PayPalOrder> PlacePayPalOrderAsync(int quantity = 1, int onHand = 10)
    {
        var (response, order, variantId) = await TryPlacePayPalOrderAsync(quantity, onHand);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(order);

        return await ReadPayPalOrderAsync(order.PublicId, variantId);
    }

    /// <summary>The same, handing back the raw response so a test can assert a refusal.</summary>
    protected async Task<(HttpResponseMessage Response, SpencerDenim.Application.Sales.Checkout.Contracts.OrderConfirmationDto? Order, int VariantId)>
        TryPlacePayPalOrderAsync(int quantity = 1, int onHand = 10)
    {
        var variantId = (await CheckoutVariantIdsAsync(++_payPalOrdersPlaced))[_payPalOrdersPlaced - 1];
        await SetStockAsync(variantId, onHand);

        var client = NewPayPalClient();
        var cart = new CartApiClient(client);

        var (added, _) = await cart.AddAsync(variantId, quantity, Token);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var api = new CheckoutApiClient(client);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);

        var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));
        body["paymentMethodCode"] = PaymentProviderCodes.PayPal;

        var (response, order) = await api.PlaceAsync(body, Token);

        return (response, order, variantId);
    }

    /// <summary>The checkout session as a shopper sees it, for the methods it offers.</summary>
    protected async Task<IReadOnlyList<string>> OfferedMethodsAsync()
    {
        var client = NewPayPalClient();
        var cart = new CartApiClient(client);
        var variantId = (await CheckoutVariantIdsAsync(1))[0];

        await cart.AddAsync(variantId, 1, Token);

        var (_, session) = await new CheckoutApiClient(client).GetAsync(
            await DistrictIdAsync("Colombo"),
            await DeliveryMethodIdAsync(DeliveryMethod.StandardCode),
            Token);

        return session!.PaymentMethods.Select(method => method.Code).ToList();
    }

    private async Task<PayPalOrder> ReadPayPalOrderAsync(Guid publicId, int variantId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.Order!.PublicId == publicId)
            .Select(payment => new
            {
                payment.Id,
                payment.OrderId,
                payment.Order!.OrderNumber,
                payment.Order.GrandTotal,
                payment.ProviderAmount,
            })
            .SingleAsync(Token);

        return new PayPalOrder(
            publicId,
            row.OrderNumber,
            row.OrderId,
            row.Id,
            variantId,
            row.GrandTotal,
            row.ProviderAmount ?? 0m);
    }

    // ------------------------------------------------------------------------------ the endpoints

    protected static string StartPath(Guid publicId) => $"/api/payments/paypal/start/{publicId:D}";

    protected static string ReturnPath(Guid publicId) => $"/api/payments/paypal/return/{publicId:D}";

    protected static string CancelPath(Guid publicId) => $"/api/payments/paypal/cancel/{publicId:D}";

    protected const string WebhookPath = "/api/payments/paypal/webhook";

    /// <summary>GET a path without following the redirect it answers.</summary>
    protected async Task<HttpResponseMessage> GetAsync(string path, HttpClient? client = null) =>
        await (client ?? NewPayPalClient()).GetAsync(new Uri(path, UriKind.Relative), Token);

    /// <summary>The <c>pay</c> hint on the order-page redirect, or null when the response is not one.</summary>
    protected static string? HintOf(HttpResponseMessage response)
    {
        Assert.True(
            response.StatusCode == HttpStatusCode.Redirect,
            $"Expected a redirect, got {(int)response.StatusCode}: {response.Content.ReadAsStringAsync(Token).GetAwaiter().GetResult()}");

        var location = response.Headers.Location?.OriginalString ?? string.Empty;
        var query = location.IndexOf('?', StringComparison.Ordinal);

        if (query < 0)
        {
            return null;
        }

        return location[(query + 1)..]
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair[0] == PayPalPageHints.Parameter)
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .SingleOrDefault();
    }

    /// <summary>The buyer clicks "Pay now": the shop creates the PayPal order and redirects to PayPal.</summary>
    protected async Task<string> StartAsync(PayPalOrder order, HttpClient? client = null)
    {
        using var response = await GetAsync(StartPath(order.PublicId), client);

        Assert.True(
            response.StatusCode == HttpStatusCode.Redirect,
            $"Start answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Token)}");

        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("https://www.sandbox.paypal.com/checkoutnow?token=", location, StringComparison.Ordinal);

        return location["https://www.sandbox.paypal.com/checkoutnow?token=".Length..];
    }

    /// <summary>A webhook delivery, with the signature header the fake treats as genuine unless told otherwise.</summary>
    protected async Task<HttpResponseMessage> PostWebhookAsync(
        string body,
        string signature = FakePayPalGateway.ValidSignature,
        HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(WebhookPath, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add("PAYPAL-AUTH-ALGO", "SHA256withRSA");
        request.Headers.Add("PAYPAL-CERT-URL", "https://api.sandbox.paypal.com/v1/notifications/certs/CERT-TEST");
        request.Headers.Add("PAYPAL-TRANSMISSION-ID", Guid.NewGuid().ToString("D"));
        request.Headers.Add("PAYPAL-TRANSMISSION-SIG", signature);
        request.Headers.Add("PAYPAL-TRANSMISSION-TIME", "2026-10-10T09:08:45Z");

        return await (client ?? NewPayPalClient()).SendAsync(request, Token);
    }

    /// <summary>A PAYMENT.CAPTURE.* notification body, shaped as PayPal sends one.</summary>
    protected static string CaptureEvent(
        string eventType,
        string eventId,
        string captureId,
        string paypalOrderId,
        int paymentId,
        decimal amount,
        string currency = "USD") =>
        $$"""
        {
          "id": "{{eventId}}",
          "event_version": "1.0",
          "create_time": "2026-10-10T09:08:45Z",
          "resource_type": "capture",
          "event_type": "{{eventType}}",
          "summary": "x",
          "resource": {
            "id": "{{captureId}}",
            "amount": { "currency_code": "{{currency}}", "value": "{{amount.ToString("0.00", CultureInfo.InvariantCulture)}}" },
            "custom_id": "{{PayPalRequestIds.CustomId(paymentId)}}",
            "supplementary_data": { "related_ids": { "order_id": "{{paypalOrderId}}" } },
            "status_details": { "reason": "ECHECK" }
          },
          "links": []
        }
        """;

    // ------------------------------------------------------------------------------ the database

    protected async Task<Payment> PaymentAsync(int paymentId)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == paymentId, Token);
    }

    protected async Task<List<Payment>> PaymentsOfAsync(int orderId)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == orderId)
            .OrderBy(payment => payment.Id)
            .ToListAsync(Token);
    }

    protected async Task<Order> OrderRowAsync(int orderId)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .AsNoTracking()
            .Include(order => order.StatusHistory)
            .SingleAsync(order => order.Id == orderId, Token);
    }

    protected async Task<List<PaymentEvent>> EventsOfAsync(int paymentId)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentEvents
            .AsNoTracking()
            .Where(candidate => candidate.PaymentId == paymentId)
            .OrderBy(candidate => candidate.Id)
            .ToListAsync(Token);
    }

    /// <summary>How many events the whole log holds - compared before and after, never against zero.</summary>
    protected async Task<int> EventCountAsync()
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentEvents.CountAsync(Token);
    }

    protected async Task<List<AdminAlert>> AlertsOfTypeAsync(string type, Guid? orderPublicId = null)
    {
        using var scope = CreateScope();
        var id = orderPublicId?.ToString("D");

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminAlerts
            .AsNoTracking()
            .Where(alert => alert.AlertType == type && (id == null || alert.RelatedEntityId == id))
            .OrderBy(alert => alert.Id)
            .ToListAsync(Token);
    }

    protected async Task<int> OutboxCountAsync(Guid orderPublicId, string templateCode)
    {
        using var scope = CreateScope();
        var id = orderPublicId.ToString("D");

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailOutbox
            .AsNoTracking()
            .CountAsync(message => message.RelatedEntityId == id && message.TemplateCode == templateCode, Token);
    }

    /// <summary>Removes the alerts a test raised for its own orders, so a neighbour's banner count is its own.</summary>
    protected async Task DeleteAlertsAsync(params Guid[] orderPublicIds)
    {
        if (!SqlServer.IsAvailable)
        {
            return;
        }

        using var scope = SqlServer.CreateScope();
        var ids = orderPublicIds.Select(id => id.ToString("D")).ToList();

        await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminAlerts
            .Where(alert => alert.RelatedEntityId != null && ids.Contains(alert.RelatedEntityId))
            .ExecuteDeleteAsync(CancellationToken.None);
    }

    /// <summary>
    /// Finishes whatever an earlier test left unfinished: PayPal payments still Capturing, and PayPal webhook
    /// events still Received or Failed. The recovery and retry jobs look at <i>every</i> such row in the shared
    /// database, so a neighbour's leftover would otherwise change a count that is the point of this test.
    /// </summary>
    protected async Task QuietLeftoversAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Payments
            .Where(payment => payment.ProviderCode == PaymentProviderCodes.PayPal
                && payment.Status == PaymentStatus.Capturing)
            .ExecuteUpdateAsync(
                payment => payment
                    .SetProperty(row => row.Status, PaymentStatus.Failed)
                    .SetProperty(row => row.FailureCode, "TEST_CLEANUP"),
                CancellationToken.None);

        await db.PaymentEvents
            .Where(candidate => candidate.ProviderCode == PaymentProviderCodes.PayPal
                && (candidate.ProcessingStatus == PaymentEventProcessingStatus.Received
                    || candidate.ProcessingStatus == PaymentEventProcessingStatus.Failed))
            .ExecuteUpdateAsync(
                candidate => candidate
                    .SetProperty(row => row.ProcessingStatus, PaymentEventProcessingStatus.Ignored)
                    .SetProperty(row => row.NextAttemptAtUtc, (DateTime?)null),
                CancellationToken.None);
    }

    /// <summary>Changes the order's payment due time directly - an arrangement, asserted by its caller.</summary>
    protected async Task SetOrderDueAsync(int orderId, DateTime dueUtc)
    {
        using var scope = CreateScope();

        var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .Where(order => order.Id == orderId)
            .ExecuteUpdateAsync(order => order.SetProperty(row => row.PaymentDueAtUtc, dueUtc), Token);

        Assert.Equal(1, moved);
    }

    /// <summary>Expires the order through the real store with "now" at the test clock.</summary>
    protected Task<bool> ExpireAtAsync(int orderId, DateTime nowUtc) =>
        InPayPalScopeAsync(services => services
            .GetRequiredService<IOrderExpiryStore>()
            .ExpireAsync(orderId, nowUtc, Token));

    /// <summary>
    /// Puts PayPal back as the seeder leaves it, and the PayPal provider switched off, whatever the test did.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        foreach (var client in _payPalClients)
        {
            client.Dispose();
        }

        _payPalClients.Clear();

        if (SqlServer.IsAvailable)
        {
            // Nothing this test left half-done may be picked up by the next class's jobs.
            await QuietLeftoversAsync();

            using var scope = SqlServer.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            foreach (var (key, value) in _originalSettings)
            {
                await db.Settings
                    .Where(setting => setting.Key == key)
                    .ExecuteUpdateAsync(
                        setting => setting.SetProperty(row => row.Value, value),
                        CancellationToken.None);
            }

            if (_providerSwitched)
            {
                await db.PaymentProviders
                    .Where(provider => provider.Code == PaymentProviderCodes.PayPal)
                    .ExecuteUpdateAsync(
                        provider => provider.SetProperty(row => row.IsEnabled, false),
                        CancellationToken.None);
            }
        }

        _originalSettings.Clear();

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
