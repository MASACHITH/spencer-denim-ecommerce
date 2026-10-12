using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Branding;
using SpencerDenim.Infrastructure.Sales.Payments.PayPal;

namespace SpencerDenim.UnitTests.Infrastructure.Sales.PayPal;

/// <summary>
/// <c>PayPalHttpGateway</c> against a hand-written <see cref="HttpMessageHandler"/>: the exact requests it
/// sends and how it reads what comes back. <b>Nothing here touches a network</b> - the handler is the only
/// "PayPal" there is. Most of the answers it gives are written from PayPal's documentation, so they cannot say
/// whether PayPal still speaks the dialect this class assumes. <b>The section "recorded from the real sandbox"
/// can:</b> its answers are PayPal's own, captured from the sandbox on 2026-10-12, and its request test pins the
/// body PayPal accepted that day. A change that breaks either is a change to something proven, not guessed.
/// </summary>
public sealed class PayPalHttpGatewayTests
{
    private const string Secret = "S3CRET-client-secret-value";
    private const string AccessToken = "A21AAF-access-token-value";
    private static readonly DateTime Start = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public Harness(PayPalSettings? settings = null)
        {
            Settings = settings ?? Configured();
            Clock = new MutableClock(Start);
            Handler = new ScriptedHandler();
            Logs = new CapturingLogger();
            Reader = new FixedSettings(Settings);
            Gateway = new PayPalHttpGateway(
                new HttpClient(Handler) { Timeout = PayPalHttpGateway.RequestTimeout },
                Reader,
                new PayPalTokenCache(),
                Clock,
                Logs);
        }

        public PayPalSettings Settings { get; }

        public MutableClock Clock { get; }

        public ScriptedHandler Handler { get; }

        public CapturingLogger Logs { get; }

        public FixedSettings Reader { get; }

        public PayPalHttpGateway Gateway { get; }
    }

    private static PayPalSettings Configured(PayPalMode mode = PayPalMode.Sandbox, string secret = Secret) =>
        new()
        {
            Mode = mode,
            ClientId = "client-id-123",
            ClientSecret = secret,
            WebhookId = "WH-ID-1",
            LkrPerUsd = 300m,
            RateUpdatedAtUtc = Start,
        };

    private static string TokenJson(int expiresIn = 32400, string token = AccessToken) =>
        $$"""{ "scope": "x", "access_token": "{{token}}", "token_type": "Bearer", "app_id": "APP", "expires_in": {{expiresIn}} }""";

    private static string OrderJson(
        string id = "5O190127TN364715T",
        string status = "COMPLETED",
        string? captureStatus = "COMPLETED",
        string captureId = "3C679366HH908993F",
        string amount = "53.55",
        string payerId = "QYR5Z8XDVJNXQ")
    {
        var capture = captureStatus is null
            ? string.Empty
            : "{ \"id\": \"" + captureId + "\", \"status\": \"" + captureStatus + "\", "
                + "\"amount\": { \"currency_code\": \"USD\", \"value\": \"" + amount + "\" }, "
                + "\"status_details\": { \"reason\": \"ECHECK\" } }";

        return $$"""
            {
              "id": "{{id}}",
              "status": "{{status}}",
              "payer": { "name": { "given_name": "Private", "surname": "Person" }, "email_address": "private@example.com", "payer_id": "{{payerId}}" },
              "purchase_units": [ {
                "reference_id": "default",
                "shipping": { "address": { "address_line_1": "1 Secret Lane" } },
                "payments": { "captures": [ {{capture}} ] }
              } ]
            }
            """;
    }

    private static string ErrorJson(string issue) =>
        $$"""{ "name": "UNPROCESSABLE_ENTITY", "details": [ { "issue": "{{issue}}", "description": "x" } ] }""";

    // ---------------------------------------------------------------- the token

    [Fact]
    public async Task The_token_is_asked_for_with_basic_credentials_and_a_form_body()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.OK, OrderJson(status: "APPROVED", captureStatus: null));

        await h.Gateway.GetOrderAsync("5O190127TN364715T", TestContext.Current.CancellationToken);

        var token = h.Handler.Requests[0];
        Assert.Equal(HttpMethod.Post, token.Method);
        Assert.Equal("https://api-m.sandbox.paypal.com/v1/oauth2/token", token.Uri);
        Assert.Equal("Basic", token.AuthScheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"client-id-123:{Secret}")),
            token.AuthParameter);
        Assert.Equal("grant_type=client_credentials", token.Body);
    }

    [Fact]
    public async Task The_token_is_reused_until_a_minute_before_it_expires()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson(expiresIn: 600));

        for (var i = 0; i < 3; i++)
        {
            h.Handler.Enqueue(HttpStatusCode.OK, OrderJson(status: "APPROVED", captureStatus: null));
        }

        for (var i = 0; i < 3; i++)
        {
            await h.Gateway.GetOrderAsync("O1", TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, h.Handler.Requests.Count(request => request.Uri.EndsWith("/v1/oauth2/token", StringComparison.Ordinal)));

        // 600 s life, 60 s margin: at 9 minutes 30 it is no longer trusted.
        h.Clock.Now = Start.AddSeconds(570);
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson(expiresIn: 600, token: "SECOND-TOKEN"));
        h.Handler.Enqueue(HttpStatusCode.OK, OrderJson(status: "APPROVED", captureStatus: null));

        await h.Gateway.GetOrderAsync("O1", TestContext.Current.CancellationToken);

        Assert.Equal(2, h.Handler.Requests.Count(request => request.Uri.EndsWith("/v1/oauth2/token", StringComparison.Ordinal)));
        Assert.Equal("SECOND-TOKEN", h.Handler.Requests[^1].AuthParameter);
    }

    [Fact]
    public async Task A_changed_credential_never_reuses_a_token_issued_for_the_old_one()
    {
        var settings = Configured();
        var h = new Harness(settings);
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson(token: "OLD"));
        h.Handler.Enqueue(HttpStatusCode.OK, OrderJson(status: "APPROVED", captureStatus: null));
        await h.Gateway.GetOrderAsync("O1", TestContext.Current.CancellationToken);

        // The Super Admin pastes a new secret.
        h.Reader.Current = Configured(secret: "A-DIFFERENT-SECRET");
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson(token: "NEW"));
        h.Handler.Enqueue(HttpStatusCode.OK, OrderJson(status: "APPROVED", captureStatus: null));
        await h.Gateway.GetOrderAsync("O1", TestContext.Current.CancellationToken);

        Assert.Equal(2, h.Handler.Requests.Count(request => request.Uri.EndsWith("/v1/oauth2/token", StringComparison.Ordinal)));
        Assert.Equal("NEW", h.Handler.Requests[^1].AuthParameter);
    }

    [Fact]
    public async Task Switching_to_live_goes_to_the_live_host_with_no_restart()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.OK, OrderJson(status: "APPROVED", captureStatus: null));
        await h.Gateway.GetOrderAsync("O1", TestContext.Current.CancellationToken);
        Assert.StartsWith("https://api-m.sandbox.paypal.com/", h.Handler.Requests[^1].Uri, StringComparison.Ordinal);

        h.Reader.Current = Configured(PayPalMode.Live);
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson(token: "LIVE-TOKEN"));
        h.Handler.Enqueue(HttpStatusCode.OK, OrderJson(status: "APPROVED", captureStatus: null));
        await h.Gateway.GetOrderAsync("O1", TestContext.Current.CancellationToken);

        Assert.Equal("https://api-m.paypal.com/v1/oauth2/token", h.Handler.Requests[^2].Uri);
        Assert.StartsWith("https://api-m.paypal.com/v2/checkout/orders/", h.Handler.Requests[^1].Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_credentials_are_unavailable_and_are_not_cached()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.Unauthorized, """{ "error": "invalid_client" }""");

        var result = await h.Gateway.GetOrderAsync("O1", TestContext.Current.CancellationToken);

        Assert.IsType<PayPalGetOrderResult.Unavailable>(result);

        // The next call asks again rather than remembering a "no".
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.OK, OrderJson(status: "APPROVED", captureStatus: null));

        Assert.IsType<PayPalGetOrderResult.Found>(await h.Gateway.GetOrderAsync("O1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PayPal_being_down_at_the_token_endpoint_is_unavailable_and_not_a_credential_failure()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.ServiceUnavailable, "<html>down</html>");

        Assert.IsType<PayPalCaptureCallResult.Unavailable>(
            await h.Gateway.CaptureOrderAsync("O1", "capture-1", TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------- create order

    [Fact]
    public async Task Create_order_sends_the_amount_the_request_id_and_the_way_back()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(
            HttpStatusCode.Created,
            """
            { "id": "5O190127TN364715T", "status": "PAYER_ACTION_REQUIRED",
              "links": [ { "href": "https://api.sandbox.paypal.com/v2/checkout/orders/5O1", "rel": "self", "method": "GET" },
                         { "href": "https://www.sandbox.paypal.com/checkoutnow?token=5O190127TN364715T", "rel": "payer-action", "method": "GET" } ] }
            """);

        var result = await h.Gateway.CreateOrderAsync(
            new PayPalCreateOrderRequest(
                "create-481",
                53.55m,
                "USD",
                "pay-481",
                "Order CW-2026-000123",
                "https://shop.example.test/api/payments/paypal/return/ORDER",
                "https://shop.example.test/api/payments/paypal/cancel/ORDER"),
            TestContext.Current.CancellationToken);

        var created = Assert.IsType<PayPalCreateOrderResult.Created>(result);
        Assert.Equal("5O190127TN364715T", created.OrderId);
        Assert.Equal("https://www.sandbox.paypal.com/checkoutnow?token=5O190127TN364715T", created.ApprovalUrl);

        var request = h.Handler.Requests[^1];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api-m.sandbox.paypal.com/v2/checkout/orders", request.Uri);
        Assert.Equal("Bearer", request.AuthScheme);
        Assert.Equal(AccessToken, request.AuthParameter);
        Assert.Equal("create-481", request.RequestId);

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("CAPTURE", root.GetProperty("intent").GetString());

        var unit = root.GetProperty("purchase_units")[0];
        Assert.Equal("USD", unit.GetProperty("amount").GetProperty("currency_code").GetString());
        Assert.Equal("53.55", unit.GetProperty("amount").GetProperty("value").GetString());
        Assert.Equal("pay-481", unit.GetProperty("custom_id").GetString());
        Assert.Equal("Order CW-2026-000123", unit.GetProperty("description").GetString());

        // invoice_id is left out: PayPal refuses a second order with an invoice id it has seen, which a retry
        // after a decline would be.
        Assert.False(unit.TryGetProperty("invoice_id", out _));

        var experience = root.GetProperty("payment_source").GetProperty("paypal").GetProperty("experience_context");
        Assert.Equal("NO_SHIPPING", experience.GetProperty("shipping_preference").GetString());
        Assert.Equal("PAY_NOW", experience.GetProperty("user_action").GetString());
        Assert.Equal("https://shop.example.test/api/payments/paypal/return/ORDER", experience.GetProperty("return_url").GetString());
        Assert.Equal("https://shop.example.test/api/payments/paypal/cancel/ORDER", experience.GetProperty("cancel_url").GetString());
    }

    [Fact]
    public async Task The_amount_is_written_with_two_places_whatever_the_server_culture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            var h = new Harness();
            h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
            h.Handler.Enqueue(HttpStatusCode.Created, """{ "id": "ABC", "status": "CREATED" }""");

            await h.Gateway.CreateOrderAsync(
                new PayPalCreateOrderRequest("create-1", 10m, "USD", "pay-1", "d", "https://x.test/r", "https://x.test/c"),
                TestContext.Current.CancellationToken);

            using var body = JsonDocument.Parse(h.Handler.Requests[^1].Body!);
            Assert.Equal(
                "10.00",
                body.RootElement.GetProperty("purchase_units")[0].GetProperty("amount").GetProperty("value").GetString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public async Task Without_a_usable_link_the_approval_address_is_the_documented_checkout_address_for_the_mode()
    {
        var h = new Harness(Configured(PayPalMode.Live));
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.Created, """{ "id": "5O19", "status": "CREATED", "links": [] }""");

        var created = Assert.IsType<PayPalCreateOrderResult.Created>(await h.Gateway.CreateOrderAsync(
            new PayPalCreateOrderRequest("create-1", 10m, "USD", "pay-1", "d", "https://x.test/r", "https://x.test/c"),
            TestContext.Current.CancellationToken));

        Assert.Equal("https://www.paypal.com/checkoutnow?token=5O19", created.ApprovalUrl);
    }

    /// <summary>A link on someone else's domain is never followed, even from a "valid" response.</summary>
    [Fact]
    public async Task An_approval_link_off_PayPals_domain_is_ignored()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(
            HttpStatusCode.Created,
            """{ "id": "5O19", "status": "CREATED", "links": [ { "href": "https://evil.example/phish", "rel": "payer-action" } ] }""");

        var created = Assert.IsType<PayPalCreateOrderResult.Created>(await h.Gateway.CreateOrderAsync(
            new PayPalCreateOrderRequest("create-1", 10m, "USD", "pay-1", "d", "https://x.test/r", "https://x.test/c"),
            TestContext.Current.CancellationToken));

        Assert.Equal("https://www.sandbox.paypal.com/checkoutnow?token=5O19", created.ApprovalUrl);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, typeof(PayPalCreateOrderResult.Rejected))]
    [InlineData(HttpStatusCode.UnprocessableEntity, typeof(PayPalCreateOrderResult.Rejected))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(PayPalCreateOrderResult.Unavailable))]
    [InlineData(HttpStatusCode.BadGateway, typeof(PayPalCreateOrderResult.Unavailable))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(PayPalCreateOrderResult.Unavailable))]
    public async Task Create_order_tells_a_refusal_from_PayPal_being_unwell(HttpStatusCode status, Type expected)
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(status, ErrorJson("DUPLICATE_INVOICE_ID"));

        var result = await h.Gateway.CreateOrderAsync(
            new PayPalCreateOrderRequest("create-1", 10m, "USD", "pay-1", "d", "https://x.test/r", "https://x.test/c"),
            TestContext.Current.CancellationToken);

        Assert.IsType(expected, result);
    }

    [Fact]
    public async Task A_timeout_or_a_refused_connection_is_unavailable_and_never_an_exception()
    {
        foreach (Exception failure in new Exception[]
        {
            new TaskCanceledException("timed out"),
            new HttpRequestException("connection refused"),
            new IOException("reset"),
        })
        {
            var h = new Harness();
            h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
            h.Handler.EnqueueFailure(failure);

            var result = await h.Gateway.CreateOrderAsync(
                new PayPalCreateOrderRequest("create-1", 10m, "USD", "pay-1", "d", "https://x.test/r", "https://x.test/c"),
                TestContext.Current.CancellationToken);

            Assert.IsType<PayPalCreateOrderResult.Unavailable>(result);
        }
    }

    [Fact]
    public async Task A_caller_who_cancels_gets_the_cancellation_and_not_a_fake_answer()
    {
        var h = new Harness();
        using var cancel = new CancellationTokenSource();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.EnqueueFailure(new OperationCanceledException(cancel.Token));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Gateway.GetOrderAsync("O1", cancel.Token));
    }

    [Fact]
    public async Task When_PayPal_is_not_configured_no_request_is_made_at_all()
    {
        var h = new Harness(new PayPalSettings { ClientId = "c" });

        Assert.IsType<PayPalCreateOrderResult.Unavailable>(await h.Gateway.CreateOrderAsync(
            new PayPalCreateOrderRequest("create-1", 10m, "USD", "pay-1", "d", "https://x.test/r", "https://x.test/c"),
            TestContext.Current.CancellationToken));
        Assert.IsType<PayPalGetOrderResult.Unavailable>(await h.Gateway.GetOrderAsync("O", TestContext.Current.CancellationToken));
        Assert.IsType<PayPalCaptureCallResult.Unavailable>(await h.Gateway.CaptureOrderAsync("O", "capture-1", TestContext.Current.CancellationToken));
        Assert.Equal(
            PayPalWebhookVerification.Error,
            await h.Gateway.VerifyWebhookAsync(ValidHeaders(), "{}", TestContext.Current.CancellationToken));

        Assert.Empty(h.Handler.Requests);
    }

    // ---------------------------------------------------------------- get order

    [Fact]
    public async Task Get_order_reads_the_status_the_payer_and_every_capture()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.OK, OrderJson());

        var found = Assert.IsType<PayPalGetOrderResult.Found>(
            await h.Gateway.GetOrderAsync("5O190127TN364715T", TestContext.Current.CancellationToken));

        Assert.Equal(PayPalOrderStatus.Completed, found.Order.Status);
        Assert.Equal("QYR5Z8XDVJNXQ", found.Order.PayerId);

        var capture = Assert.Single(found.Order.Captures);
        Assert.Equal("3C679366HH908993F", capture.CaptureId);
        Assert.Equal(PayPalCaptureStatus.Completed, capture.Status);
        Assert.Equal(53.55m, capture.Amount);
        Assert.Equal("USD", capture.Currency);
        Assert.Equal("ECHECK", capture.StatusReason);

        Assert.Equal(HttpMethod.Get, h.Handler.Requests[^1].Method);
        Assert.Equal("https://api-m.sandbox.paypal.com/v2/checkout/orders/5O190127TN364715T", h.Handler.Requests[^1].Uri);
    }

    [Theory]
    [InlineData("CREATED", PayPalOrderStatus.Created)]
    [InlineData("SAVED", PayPalOrderStatus.Saved)]
    [InlineData("APPROVED", PayPalOrderStatus.Approved)]
    [InlineData("VOIDED", PayPalOrderStatus.Voided)]
    [InlineData("COMPLETED", PayPalOrderStatus.Completed)]
    [InlineData("PAYER_ACTION_REQUIRED", PayPalOrderStatus.PayerActionRequired)]
    [InlineData("SOMETHING_NEW", PayPalOrderStatus.Unknown)]
    public async Task Order_statuses_are_mapped_and_an_unknown_one_is_unknown(string status, PayPalOrderStatus expected)
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.OK, OrderJson(status: status, captureStatus: null));

        var found = Assert.IsType<PayPalGetOrderResult.Found>(
            await h.Gateway.GetOrderAsync("O1", TestContext.Current.CancellationToken));

        Assert.Equal(expected, found.Order.Status);
        Assert.Empty(found.Order.Captures);
    }

    [Theory]
    [InlineData("COMPLETED", PayPalCaptureStatus.Completed)]
    [InlineData("PENDING", PayPalCaptureStatus.Pending)]
    [InlineData("DECLINED", PayPalCaptureStatus.Declined)]
    [InlineData("DENIED", PayPalCaptureStatus.Declined)]
    [InlineData("FAILED", PayPalCaptureStatus.Failed)]
    [InlineData("REFUNDED", PayPalCaptureStatus.Refunded)]
    [InlineData("PARTIALLY_REFUNDED", PayPalCaptureStatus.PartiallyRefunded)]
    [InlineData("WHATEVER", PayPalCaptureStatus.Unknown)]
    public async Task Capture_statuses_are_mapped_and_an_unknown_one_is_never_acted_on(string status, PayPalCaptureStatus expected)
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.OK, OrderJson(captureStatus: status));

        var found = Assert.IsType<PayPalGetOrderResult.Found>(
            await h.Gateway.GetOrderAsync("O1", TestContext.Current.CancellationToken));

        Assert.Equal(expected, found.Order.Captures.Single().Status);
    }

    [Fact]
    public async Task Get_order_404_is_not_found_and_5xx_is_unavailable()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.NotFound, """{ "name": "RESOURCE_NOT_FOUND" }""");
        h.Handler.Enqueue(HttpStatusCode.InternalServerError, "oops");

        Assert.IsType<PayPalGetOrderResult.NotFound>(await h.Gateway.GetOrderAsync("O1", TestContext.Current.CancellationToken));
        Assert.IsType<PayPalGetOrderResult.Unavailable>(await h.Gateway.GetOrderAsync("O1", TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------- capture

    [Fact]
    public async Task Capture_posts_once_with_the_stable_request_id_and_reads_the_capture()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.Created, OrderJson());

        var result = await h.Gateway.CaptureOrderAsync("5O190127TN364715T", "capture-481", TestContext.Current.CancellationToken);

        var captured = Assert.IsType<PayPalCaptureCallResult.Captured>(result);
        Assert.Equal("3C679366HH908993F", captured.Order.Captures.Single().CaptureId);
        Assert.Equal(PayPalCaptureStatus.Completed, captured.Order.Captures.Single().Status);

        var request = h.Handler.Requests[^1];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api-m.sandbox.paypal.com/v2/checkout/orders/5O190127TN364715T/capture", request.Uri);
        Assert.Equal("capture-481", request.RequestId);
        Assert.Equal("{}", request.Body);
    }

    [Fact]
    public async Task A_capture_that_is_PENDING_inside_a_COMPLETED_order_is_reported_as_pending()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.Created, OrderJson(status: "COMPLETED", captureStatus: "PENDING"));

        var captured = Assert.IsType<PayPalCaptureCallResult.Captured>(
            await h.Gateway.CaptureOrderAsync("O1", "capture-1", TestContext.Current.CancellationToken));

        // The decision is on the capture's status, never the order's.
        Assert.Equal(PayPalOrderStatus.Completed, captured.Order.Status);
        Assert.Equal(PayPalCaptureStatus.Pending, captured.Order.Captures.Single().Status);
    }

    /// <summary>A minimal-form answer carries no capture; the order is then read for it - one GET, which is safe.</summary>
    [Fact]
    public async Task A_capture_answered_in_the_minimal_form_is_completed_from_a_read_of_the_order()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.Created, """{ "id": "O1", "status": "COMPLETED", "links": [] }""");
        h.Handler.Enqueue(HttpStatusCode.OK, OrderJson(id: "O1"));

        var captured = Assert.IsType<PayPalCaptureCallResult.Captured>(
            await h.Gateway.CaptureOrderAsync("O1", "capture-1", TestContext.Current.CancellationToken));

        Assert.Equal("3C679366HH908993F", captured.Order.Captures.Single().CaptureId);
        Assert.Equal(1, h.Handler.Requests.Count(request => request.Method == HttpMethod.Post && request.Uri.EndsWith("/capture", StringComparison.Ordinal)));
        Assert.Equal(HttpMethod.Get, h.Handler.Requests[^1].Method);
    }

    [Theory]
    [InlineData("ORDER_ALREADY_CAPTURED", typeof(PayPalCaptureCallResult.AlreadyCaptured))]
    [InlineData("INSTRUMENT_DECLINED", typeof(PayPalCaptureCallResult.Declined))]
    [InlineData("TRANSACTION_REFUSED", typeof(PayPalCaptureCallResult.Declined))]
    [InlineData("PAYER_CANNOT_PAY", typeof(PayPalCaptureCallResult.Declined))]
    [InlineData("ORDER_NOT_APPROVED", typeof(PayPalCaptureCallResult.NotApproved))]
    [InlineData("PAYER_ACTION_REQUIRED", typeof(PayPalCaptureCallResult.NotApproved))]
    [InlineData("PAYEE_ACCOUNT_RESTRICTED", typeof(PayPalCaptureCallResult.Rejected))]
    [InlineData("SOMETHING_ELSE", typeof(PayPalCaptureCallResult.Rejected))]
    public async Task A_422_from_capture_is_read_by_its_issue(string issue, Type expected)
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.UnprocessableEntity, ErrorJson(issue));

        var result = await h.Gateway.CaptureOrderAsync("O1", "capture-1", TestContext.Current.CancellationToken);

        Assert.IsType(expected, result);
    }

    [Fact]
    public async Task Capture_404_is_order_not_found_and_other_4xx_is_a_rejection_that_proves_nothing()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.NotFound, """{ "name": "RESOURCE_NOT_FOUND" }""");
        h.Handler.Enqueue(HttpStatusCode.BadRequest, """{ "name": "INVALID_REQUEST" }""");

        Assert.IsType<PayPalCaptureCallResult.OrderNotFound>(
            await h.Gateway.CaptureOrderAsync("O1", "capture-1", TestContext.Current.CancellationToken));
        Assert.IsType<PayPalCaptureCallResult.Rejected>(
            await h.Gateway.CaptureOrderAsync("O1", "capture-1", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The capture POST is sent <b>once</b>: a 5xx or a timeout is "we do not know", and whether to ask again
    /// is the claim's and the recovery job's decision, not an HTTP retry's.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_5xx_on_capture_is_unavailable_and_the_capture_is_not_retried(HttpStatusCode status)
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(status, "upstream trouble");

        var result = await h.Gateway.CaptureOrderAsync("O1", "capture-1", TestContext.Current.CancellationToken);

        Assert.IsType<PayPalCaptureCallResult.Unavailable>(result);
        Assert.Equal(1, h.Handler.Requests.Count(request => request.Uri.EndsWith("/capture", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_timeout_on_capture_is_unavailable_and_the_capture_is_not_retried()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.EnqueueFailure(new TaskCanceledException("timed out"));

        var result = await h.Gateway.CaptureOrderAsync("O1", "capture-1", TestContext.Current.CancellationToken);

        Assert.IsType<PayPalCaptureCallResult.Unavailable>(result);
        Assert.Equal(1, h.Handler.Requests.Count(request => request.Uri.EndsWith("/capture", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A 401 means PayPal did not process the request, so it is sent again once with a fresh token - under
    /// the same request id, which is what keeps that safe.
    /// </summary>
    [Fact]
    public async Task A_401_is_retried_once_with_a_fresh_token_and_the_same_request_id()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson(token: "STALE"));
        h.Handler.Enqueue(HttpStatusCode.Unauthorized, """{ "name": "AUTHENTICATION_FAILURE" }""");
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson(token: "FRESH"));
        h.Handler.Enqueue(HttpStatusCode.Created, OrderJson());

        var result = await h.Gateway.CaptureOrderAsync("O1", "capture-9", TestContext.Current.CancellationToken);

        Assert.IsType<PayPalCaptureCallResult.Captured>(result);

        var captures = h.Handler.Requests.Where(request => request.Uri.EndsWith("/capture", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, captures.Count);
        Assert.All(captures, request => Assert.Equal("capture-9", request.RequestId));
        Assert.Equal("STALE", captures[0].AuthParameter);
        Assert.Equal("FRESH", captures[1].AuthParameter);
    }

    [Fact]
    public async Task A_second_401_is_unavailable()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.Unauthorized, "{}");
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.Unauthorized, "{}");

        Assert.IsType<PayPalCaptureCallResult.Unavailable>(
            await h.Gateway.CaptureOrderAsync("O1", "capture-1", TestContext.Current.CancellationToken));
        Assert.Equal(2, h.Handler.Requests.Count(request => request.Uri.EndsWith("/capture", StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- webhook verification

    private static PayPalWebhookHeaders ValidHeaders() =>
        new(
            "SHA256withRSA",
            "https://api.sandbox.paypal.com/v1/notifications/certs/CERT-360caa42-fca2a594-1d93a270",
            "b2384410-3dc6-11e2-8f0f-b5b0a0a4c9b3",
            "lmI95Jx3Y9nhR5SJWlHVIWpg4AgWTI...",
            "2026-10-10T09:08:45Z");

    /// <summary>
    /// The notification goes to PayPal <b>as the bytes it arrived as</b>: the signature is over them, and a
    /// parse-and-rewrite would be free to change a number's spelling or a string's escapes.
    /// </summary>
    [Fact]
    public async Task Verification_sends_the_raw_event_byte_for_byte_with_the_headers_and_webhook_id()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.OK, """{ "verification_status": "SUCCESS" }""");

        // Deliberately awkward: trailing zeros, an escape, odd spacing.
        const string raw = """{"id":"WH-1",  "event_type":"PAYMENT.CAPTURE.COMPLETED","resource":{"amount":{"value":"53.50"},"note":"café \"quoted\""},"n":1.10}""";

        var result = await h.Gateway.VerifyWebhookAsync(ValidHeaders(), raw, TestContext.Current.CancellationToken);

        Assert.Equal(PayPalWebhookVerification.Verified, result);

        var request = h.Handler.Requests[^1];
        Assert.Equal("https://api-m.sandbox.paypal.com/v1/notifications/verify-webhook-signature", request.Uri);

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("SHA256withRSA", root.GetProperty("auth_algo").GetString());
        Assert.Equal(ValidHeaders().CertUrl, root.GetProperty("cert_url").GetString());
        Assert.Equal(ValidHeaders().TransmissionId, root.GetProperty("transmission_id").GetString());
        Assert.Equal(ValidHeaders().TransmissionSig, root.GetProperty("transmission_sig").GetString());
        Assert.Equal(ValidHeaders().TransmissionTime, root.GetProperty("transmission_time").GetString());
        Assert.Equal("WH-ID-1", root.GetProperty("webhook_id").GetString());

        // The event text appears in the request exactly as received.
        Assert.Contains(raw, request.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "verification_status": "FAILURE" }""", PayPalWebhookVerification.Invalid)]
    [InlineData("""{ "verification_status": "success" }""", PayPalWebhookVerification.Verified)]
    [InlineData("""{ "verification_status": "" }""", PayPalWebhookVerification.Invalid)]
    [InlineData("""{ }""", PayPalWebhookVerification.Invalid)]
    public async Task Only_a_SUCCESS_verifies(string answer, PayPalWebhookVerification expected)
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.OK, answer);

        Assert.Equal(expected, await h.Gateway.VerifyWebhookAsync(ValidHeaders(), "{}", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, PayPalWebhookVerification.Invalid)]
    [InlineData(HttpStatusCode.UnprocessableEntity, PayPalWebhookVerification.Invalid)]
    [InlineData(HttpStatusCode.Unauthorized, PayPalWebhookVerification.Error)]
    [InlineData(HttpStatusCode.Forbidden, PayPalWebhookVerification.Error)]
    [InlineData(HttpStatusCode.InternalServerError, PayPalWebhookVerification.Error)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PayPalWebhookVerification.Error)]
    public async Task A_refusal_by_PayPal_is_invalid_and_a_failure_to_ask_is_an_error(
        HttpStatusCode status,
        PayPalWebhookVerification expected)
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(status, "{}");

        // A 401 is retried once with a fresh token, so give it one to refuse as well.
        if (status == HttpStatusCode.Unauthorized)
        {
            h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
            h.Handler.Enqueue(status, "{}");
        }

        Assert.Equal(expected, await h.Gateway.VerifyWebhookAsync(ValidHeaders(), "{}", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_timeout_while_verifying_is_an_error_so_the_endpoint_answers_503()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.EnqueueFailure(new TaskCanceledException("timed out"));

        Assert.Equal(
            PayPalWebhookVerification.Error,
            await h.Gateway.VerifyWebhookAsync(ValidHeaders(), "{}", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A request that does not even look like a PayPal notification never costs a call to PayPal, which is what
    /// stops the endpoint being used to spend the shop's rate limit.
    /// </summary>
    [Theory]
    [InlineData("", "https://api.paypal.com/v1/notifications/certs/C", "id", "sig", "time")]
    [InlineData("SHA256withRSA", "", "id", "sig", "time")]
    [InlineData("SHA256withRSA", "https://api.paypal.com/v1/notifications/certs/C", "", "sig", "time")]
    [InlineData("SHA256withRSA", "https://api.paypal.com/v1/notifications/certs/C", "id", "", "time")]
    [InlineData("SHA256withRSA", "https://api.paypal.com/v1/notifications/certs/C", "id", "sig", "")]
    [InlineData("SHA256withRSA", "http://api.paypal.com/v1/notifications/certs/C", "id", "sig", "time")]
    [InlineData("SHA256withRSA", "https://evil.example/v1/notifications/certs/C", "id", "sig", "time")]
    [InlineData("SHA256withRSA", "https://paypal.com.evil.example/certs/C", "id", "sig", "time")]
    [InlineData("SHA256withRSA", "https://notpaypal.com/certs/C", "id", "sig", "time")]
    [InlineData("SHA256withRSA", "not a url", "id", "sig", "time")]
    public async Task Headers_that_cannot_be_PayPals_are_invalid_without_calling_anyone(
        string algo,
        string certUrl,
        string id,
        string sig,
        string time)
    {
        var h = new Harness();

        var result = await h.Gateway.VerifyWebhookAsync(
            new PayPalWebhookHeaders(algo, certUrl, id, sig, time),
            "{}",
            TestContext.Current.CancellationToken);

        Assert.Equal(PayPalWebhookVerification.Invalid, result);
        Assert.Empty(h.Handler.Requests);
    }

    // ---------------------------------------------------------------- secrets

    /// <summary>
    /// Whatever goes wrong, nothing the shop must keep to itself reaches a log: not the client secret, not an
    /// access token, not a payer's name, email or address from a response body.
    /// </summary>
    [Fact]
    public async Task No_log_line_ever_holds_a_secret_a_token_or_a_payload()
    {
        var h = new Harness();

        // A success, a decline, a 5xx, a refused credential and a timeout, all in one run.
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.Created, OrderJson());
        h.Handler.Enqueue(HttpStatusCode.UnprocessableEntity, ErrorJson("INSTRUMENT_DECLINED"));
        h.Handler.Enqueue(HttpStatusCode.InternalServerError, "<html>" + Secret + AccessToken + "</html>");
        h.Handler.EnqueueFailure(new HttpRequestException("failed for " + Secret));
        h.Handler.Enqueue(HttpStatusCode.BadRequest, ErrorJson("INVALID_REQUEST"));

        await h.Gateway.CaptureOrderAsync("O1", "capture-1", TestContext.Current.CancellationToken);
        await h.Gateway.CaptureOrderAsync("O1", "capture-1", TestContext.Current.CancellationToken);
        await h.Gateway.CaptureOrderAsync("O1", "capture-1", TestContext.Current.CancellationToken);
        await h.Gateway.GetOrderAsync("O1", TestContext.Current.CancellationToken);
        await h.Gateway.VerifyWebhookAsync(ValidHeaders(), "{}", TestContext.Current.CancellationToken);

        var everything = string.Join('\n', h.Logs.Messages);

        Assert.NotEmpty(h.Logs.Messages);
        Assert.DoesNotContain(Secret, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("private@example.com", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Lane", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Person", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("client-id-123", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Basic ", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer ", everything, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- recorded from the real sandbox

    // Recorded from api-m.sandbox.paypal.com on 2026-10-12 by creating a USD 10.00 order with the request below,
    // approving it as a sandbox buyer in a browser, and capturing it. Only the buyer's name and email are masked;
    // every id, status, amount and link is as PayPal sent it. Two facts the hand-written answers above get wrong:
    // PayPal answers order creation with 200, not 201, and a repeated capture under the same PayPal-Request-Id
    // answers 200 with the capture it already made, not 201.
    private const string RecordedCreateAnswer = """
        {
          "id": "4GW21777298832935",
          "status": "PAYER_ACTION_REQUIRED",
          "payment_source": { "paypal": {} },
          "links": [
            { "href": "https://api.sandbox.paypal.com/v2/checkout/orders/4GW21777298832935", "rel": "self", "method": "GET" },
            { "href": "https://www.sandbox.paypal.com/checkoutnow?token=4GW21777298832935", "rel": "payer-action", "method": "GET" }
          ]
        }
        """;

    // The answer to the capture. The order read back afterwards (GET) agrees with it on every field the gateway reads.
    private const string RecordedCapturedOrder = """
        {
          "id": "4GW21777298832935",
          "status": "COMPLETED",
          "payment_source": {
            "paypal": {
              "email_address": "REDACTED",
              "account_id": "HCYAB57PXSKYU",
              "account_status": "VERIFIED",
              "name": { "given_name": "REDACTED", "surname": "REDACTED" },
              "address": { "country_code": "US" }
            }
          },
          "purchase_units": [
            {
              "reference_id": "default",
              "shipping": {},
              "payments": {
                "captures": [
                  {
                    "id": "7MK72654UA6613524",
                    "status": "COMPLETED",
                    "amount": { "currency_code": "USD", "value": "10.00" },
                    "final_capture": true,
                    "disbursement_mode": "INSTANT",
                    "seller_protection": {
                      "status": "ELIGIBLE",
                      "dispute_categories": [ "ITEM_NOT_RECEIVED", "UNAUTHORIZED_TRANSACTION" ]
                    },
                    "seller_receivable_breakdown": {
                      "gross_amount": { "currency_code": "USD", "value": "10.00" },
                      "paypal_fee": { "currency_code": "USD", "value": "0.84" },
                      "net_amount": { "currency_code": "USD", "value": "9.16" }
                    },
                    "custom_id": "SD-TEST-ORDER-1",
                    "links": [
                      { "href": "https://api.sandbox.paypal.com/v2/payments/captures/7MK72654UA6613524", "rel": "self", "method": "GET" },
                      { "href": "https://api.sandbox.paypal.com/v2/payments/captures/7MK72654UA6613524/refund", "rel": "refund", "method": "POST" },
                      { "href": "https://api.sandbox.paypal.com/v2/checkout/orders/4GW21777298832935", "rel": "up", "method": "GET" }
                    ],
                    "create_time": "2026-10-12T17:38:53Z",
                    "update_time": "2026-10-12T17:38:53Z"
                  }
                ]
              }
            }
          ],
          "payer": {
            "name": { "given_name": "REDACTED", "surname": "REDACTED" },
            "email_address": "REDACTED",
            "payer_id": "HCYAB57PXSKYU",
            "address": { "country_code": "US" }
          },
          "links": [
            { "href": "https://api.sandbox.paypal.com/v2/checkout/orders/4GW21777298832935", "rel": "self", "method": "GET" }
          ]
        }
        """;

    /// <summary>
    /// The create-order body PayPal's sandbox <b>accepted</b> (200, <c>PAYER_ACTION_REQUIRED</c>, a
    /// <c>payer-action</c> link) on 2026-10-12. Pinned whole, so a change to any field - the experience context, the
    /// amount format, the custom id - fails here and must be re-proven against the sandbox rather than assumed.
    /// The brand is the shop's one constant, not a copy of it, and PayPal showed it on its own login page.
    /// </summary>
    [Fact]
    public async Task The_create_order_body_is_the_one_PayPals_sandbox_accepted()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.OK, RecordedCreateAnswer);

        await h.Gateway.CreateOrderAsync(
            new PayPalCreateOrderRequest(
                "sdtest-create-1",
                10m,
                "USD",
                "SD-TEST-ORDER-1",
                "Sandbox verification order",
                "https://example.com/paypal-return",
                "https://example.com/paypal-cancel"),
            TestContext.Current.CancellationToken);

        var accepted = JsonNode.Parse($$"""
            {
              "intent": "CAPTURE",
              "purchase_units": [
                {
                  "custom_id": "SD-TEST-ORDER-1",
                  "description": "Sandbox verification order",
                  "amount": { "currency_code": "USD", "value": "10.00" }
                }
              ],
              "payment_source": {
                "paypal": {
                  "experience_context": {
                    "brand_name": "{{StoreBrand.Name}}",
                    "landing_page": "NO_PREFERENCE",
                    "shipping_preference": "NO_SHIPPING",
                    "user_action": "PAY_NOW",
                    "return_url": "https://example.com/paypal-return",
                    "cancel_url": "https://example.com/paypal-cancel"
                  }
                }
              }
            }
            """);

        var sent = JsonNode.Parse(h.Handler.Requests[^1].Body!);

        Assert.True(
            JsonNode.DeepEquals(accepted, sent),
            "The create-order body is no longer the one PayPal's sandbox accepted on 2026-10-12.\n"
            + "Expected: " + accepted!.ToJsonString() + "\nSent:     " + sent!.ToJsonString());
    }

    [Fact]
    public async Task PayPals_real_create_answer_is_read_as_an_order_id_and_the_approval_link()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.OK, RecordedCreateAnswer);

        var created = Assert.IsType<PayPalCreateOrderResult.Created>(await h.Gateway.CreateOrderAsync(
            new PayPalCreateOrderRequest("create-1", 10m, "USD", "SD-TEST-ORDER-1", "d", "https://x.test/r", "https://x.test/c"),
            TestContext.Current.CancellationToken));

        Assert.Equal("4GW21777298832935", created.OrderId);
        Assert.Equal("https://www.sandbox.paypal.com/checkoutnow?token=4GW21777298832935", created.ApprovalUrl);
    }

    /// <summary>
    /// The real capture answer is 201 the first time and 200 for a repeat under the same request id. Both are
    /// "the capture happened" and both must read as the same completed capture of the amount asked for - a
    /// gateway that only accepted 201 would have treated the retry that follows a timeout as a failure.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.OK)]
    public async Task PayPals_real_capture_answer_is_read_as_a_completed_capture_of_the_amount_asked_for(HttpStatusCode status)
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(status, RecordedCapturedOrder);

        var captured = Assert.IsType<PayPalCaptureCallResult.Captured>(await h.Gateway.CaptureOrderAsync(
            "4GW21777298832935",
            "capture-sdtest",
            TestContext.Current.CancellationToken));

        Assert.Equal("4GW21777298832935", captured.Order.OrderId);
        Assert.Equal(PayPalOrderStatus.Completed, captured.Order.Status);
        Assert.Equal("HCYAB57PXSKYU", captured.Order.PayerId);

        var capture = Assert.Single(captured.Order.Captures);
        Assert.Equal("7MK72654UA6613524", capture.CaptureId);
        Assert.Equal(PayPalCaptureStatus.Completed, capture.Status);
        Assert.Equal(10.00m, capture.Amount);
        Assert.Equal("USD", capture.Currency);

        // It was one call: the answer carried the capture, so nothing needed reading back.
        Assert.Equal(2, h.Handler.Requests.Count);
        Assert.Equal("{}", h.Handler.Requests[^1].Body);
        Assert.Equal("capture-sdtest", h.Handler.Requests[^1].RequestId);
    }

    [Fact]
    public async Task PayPals_real_read_of_a_captured_order_gives_the_same_capture()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.OK, RecordedCapturedOrder);

        var found = Assert.IsType<PayPalGetOrderResult.Found>(await h.Gateway.GetOrderAsync(
            "4GW21777298832935",
            TestContext.Current.CancellationToken));

        var capture = Assert.Single(found.Order.Captures);
        Assert.Equal("7MK72654UA6613524", capture.CaptureId);
        Assert.Equal(PayPalCaptureStatus.Completed, capture.Status);
        Assert.Equal(10.00m, capture.Amount);
    }

    /// <summary>
    /// The buyer's name and email are in PayPal's real answer and are not in anything the gateway keeps or logs.
    /// </summary>
    [Fact]
    public async Task The_real_answers_personal_fields_reach_neither_the_result_nor_a_log()
    {
        var h = new Harness();
        h.Handler.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Handler.Enqueue(HttpStatusCode.OK, RecordedCapturedOrder.Replace("REDACTED", "Nimal.Perera@buyer.example", StringComparison.Ordinal));

        var found = Assert.IsType<PayPalGetOrderResult.Found>(await h.Gateway.GetOrderAsync(
            "4GW21777298832935",
            TestContext.Current.CancellationToken));

        Assert.DoesNotContain("Nimal.Perera@buyer.example", found.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Nimal.Perera@buyer.example", string.Join('\n', h.Logs.Messages), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- doubles

    private sealed class MutableClock(DateTime start) : IClock
    {
        public DateTime Now { get; set; } = start;

        public DateTimeOffset UtcNow => new(Now, TimeSpan.Zero);
    }

    private sealed class FixedSettings(PayPalSettings initial) : IPayPalSettingsReader
    {
        public PayPalSettings Current { get; set; } = initial;

        public Task<PayPalSettings> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
    }

    internal sealed record SeenRequest(
        HttpMethod Method,
        string Uri,
        string? AuthScheme,
        string? AuthParameter,
        string? RequestId,
        string? Body);

    /// <summary>Answers requests from a queue, in order, and remembers what it was asked.</summary>
    internal sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _script = new();

        public List<SeenRequest> Requests { get; } = [];

        public void Enqueue(HttpStatusCode status, string body) =>
            _script.Enqueue(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        public void EnqueueFailure(Exception exception) =>
            _script.Enqueue(() => throw exception);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new SeenRequest(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("PayPal-Request-Id", out var ids) ? ids.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            if (_script.Count == 0)
            {
                throw new InvalidOperationException(
                    $"The test did not script a response for {request.Method} {request.RequestUri}.");
            }

            return _script.Dequeue()();
        }
    }

    internal sealed class CapturingLogger : ILogger<PayPalHttpGateway>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));

            if (exception is not null)
            {
                Messages.Add(exception.ToString());
            }
        }
    }
}
