using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Sales.Payments.PayHere;
using SpencerDenim.UnitTests.Application.Sales.PayHere;

namespace SpencerDenim.UnitTests.Infrastructure.Sales;

/// <summary>
/// <c>PayHereProvider</c> against the PAY-03 questions (doc 11 P8b): when it is offered, what the payment row
/// looks like, and what the order page is told - including that it answers nothing at placement, offers no
/// button for a payment PayHere is already holding, and refuses to open a payment it could not collect.
/// </summary>
public sealed class PayHereProviderTests
{
    private const string Origin = "https://shop.example.test";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static PayHereCheckoutContext Context(PaymentStatus status = PaymentStatus.Initiated) =>
        new(
            PayHereSample.PaymentId,
            status,
            PayHereSample.OrderNumber,
            PayHereSample.OrderPublicId,
            1_500.50m,
            "Nimal",
            "Perera",
            "nimal@example.test",
            "0771234567",
            "12 Galle Road",
            "Colombo");

    private static PayHereProvider Provider(
        PayHereSettings? settings = null,
        PayHereCheckoutContext? context = null,
        string? origin = Origin,
        ListLogger? logger = null,
        PayHereWarningThrottle? throttle = null) =>
        new(
            new FakePayHereSettings(settings ?? PayHereSample.Settings()),
            new FakePayHerePayments { Context = context },
            new EmailOptions { StorefrontBaseUrl = origin },
            throttle ?? new PayHereWarningThrottle(),
            new PayHereTestClock(PayHereSample.Now),
            logger ?? new ListLogger());

    private static PaymentInstructionsRequest Request(int? paymentId = PayHereSample.PaymentId) =>
        new(
            PayHereSample.OrderNumber,
            1_500.50m,
            PayHereSample.Now.AddHours(1),
            OrderUrl: null,
            PaymentId: paymentId);

    // ---- availability ------------------------------------------------------------------------------

    [Fact]
    public void The_code_is_the_seeded_one()
    {
        Assert.Equal("PayHere", Provider().Code);
        Assert.Equal(PaymentProviderCodes.PayHere, Provider().Code);
    }

    [Fact]
    public async Task A_configured_account_with_a_public_origin_is_available()
    {
        Assert.True(await Provider().IsAvailableAsync(Token));
    }

    [Theory]
    [InlineData(null, "1211149", "secret")]
    [InlineData("sandbox", null, "secret")]
    [InlineData("sandbox", "1211149", null)]
    [InlineData("sandbox", "", "secret")]
    public async Task An_account_with_a_gap_is_not_available(string? mode, string? merchantId, string? secret)
    {
        Assert.False(await Provider(PayHereSample.Settings(mode, merchantId, secret)).IsAvailableAsync(Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public async Task Without_a_public_origin_it_is_not_available(string? origin)
    {
        Assert.False(await Provider(origin: origin).IsAvailableAsync(Token));
    }

    // ---- the public address ------------------------------------------------------------------------

    [Theory]
    [InlineData("https://shop.example.test?x=1")]
    [InlineData("https://shop.example.test/?x=1")]
    [InlineData("https://shop.example.test#frag")]
    [InlineData("https://shop.example.test/shop")]
    [InlineData("https://user:pw@shop.example.test")]
    public async Task A_public_address_that_is_not_just_scheme_and_host_makes_it_unavailable(string origin)
    {
        var logger = new ListLogger();
        var provider = Provider(origin: origin, logger: logger);

        Assert.False(await provider.IsAvailableAsync(Token));

        // And it says so: this question is only asked of a row the shop has switched on.
        var line = Assert.Single(logger.Lines);

        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("Email__StorefrontBaseUrl", line.Message, StringComparison.Ordinal);
        Assert.Contains("hidden at checkout", line.Message, StringComparison.Ordinal);

        // What is configured is never printed - the sentences name the fault, not the value.
        Assert.DoesNotContain(origin, line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PayHereSample.Secret, line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_http_is_unavailable_in_live_mode_and_fine_in_sandbox()
    {
        Assert.False(await Provider(PayHereSample.Settings(mode: "live"), origin: "http://shop.example.test")
            .IsAvailableAsync(Token));

        Assert.True(await Provider(PayHereSample.Settings(mode: "sandbox"), origin: "http://shop.example.test")
            .IsAvailableAsync(Token));
    }

    [Fact]
    public async Task A_loopback_address_is_fine_over_http_even_in_live_mode()
    {
        Assert.True(await Provider(PayHereSample.Settings(mode: "live"), origin: "http://localhost:4200")
            .IsAvailableAsync(Token));
    }

    [Fact]
    public async Task The_warning_is_written_once_per_window_however_often_checkout_asks()
    {
        var logger = new ListLogger();
        var throttle = new PayHereWarningThrottle();
        var provider = Provider(PayHereSample.Settings(secret: null), logger: logger, throttle: throttle);

        for (var view = 0; view < 25; view++)
        {
            Assert.False(await provider.IsAvailableAsync(Token));
        }

        Assert.Single(logger.Lines);
    }

    [Fact]
    public async Task A_provider_that_can_take_orders_writes_nothing()
    {
        var logger = new ListLogger();

        Assert.True(await Provider(logger: logger).IsAvailableAsync(Token));
        Assert.Empty(logger.Lines);
    }

    // ---- the payment row ---------------------------------------------------------------------------

    [Fact]
    public async Task It_opens_an_initiated_payment_in_lkr_due_after_the_configured_window()
    {
        var placed = new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

        var initiation = await Provider(PayHereSample.Settings(expiryMinutes: 45))
            .InitiateAsync(new PaymentInitiationRequest(1_500.50m, placed), Token);

        Assert.Equal(PaymentStatus.Initiated, initiation.Status);
        Assert.Equal(placed.AddMinutes(45), initiation.PaymentDueAtUtc);

        // LKR: nothing was converted, so none of the conversion columns is set.
        Assert.Null(initiation.ProviderCurrency);
        Assert.Null(initiation.ProviderAmount);
        Assert.Null(initiation.ExchangeRate);
        Assert.Null(initiation.FxBufferPercent);
    }

    [Fact]
    public async Task The_default_window_is_an_hour()
    {
        var placed = new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

        var initiation = await Provider().InitiateAsync(new PaymentInitiationRequest(10m, placed), Token);

        Assert.Equal(placed.AddMinutes(60), initiation.PaymentDueAtUtc);
    }

    [Fact]
    public async Task It_refuses_to_open_a_payment_it_could_not_collect()
    {
        var provider = Provider(PayHereSample.Settings(secret: null));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.InitiateAsync(
                new PaymentInitiationRequest(10m, PayHereSample.Now),
                Token));

        Assert.Contains("merchant secret", thrown.Message, StringComparison.Ordinal);
    }

    // ---- the description ---------------------------------------------------------------------------

    [Fact]
    public async Task At_placement_there_is_no_payment_yet_so_it_says_nothing()
    {
        Assert.Null(await Provider(context: Context()).DescribeAsync(Request(paymentId: null), Token));
    }

    [Fact]
    public async Task For_an_initiated_attempt_the_answer_is_a_signed_form_and_no_proof_upload()
    {
        var instructions = await Provider(context: Context()).DescribeAsync(Request(), Token);

        Assert.NotNull(instructions);
        Assert.Equal("PayHere", instructions.ProviderCode);
        Assert.False(instructions.AcceptsProof);
        Assert.Null(instructions.RedirectUrl);
        Assert.Null(instructions.Reference);
        Assert.Empty(instructions.Fields);
        Assert.Equal(1_500.50m, instructions.AmountLkr);

        var form = instructions.RedirectForm!;
        Assert.Equal("POST", form.Method);
        Assert.Equal("https://sandbox.payhere.lk/pay/checkout", form.Action);
        Assert.Equal("hash", form.Fields[^1].Name);
        Assert.Equal("B0454AFEFC38780441C1C85D1B4A1912", form.Fields[^1].Value);
    }

    [Fact]
    public async Task The_live_mode_posts_to_the_live_host()
    {
        var instructions = await Provider(PayHereSample.Settings(mode: "live"), Context()).DescribeAsync(Request(), Token);

        Assert.Equal("https://www.payhere.lk/pay/checkout", instructions!.RedirectForm!.Action);
    }

    /// <summary>
    /// PayHere has the payment and has not finished with it. The page must not invite a second payment: no
    /// form, no link, a plain notice.
    /// </summary>
    [Fact]
    public async Task For_a_pending_payment_there_is_a_notice_and_no_way_to_pay_again()
    {
        var instructions = await Provider(context: Context(PaymentStatus.Pending)).DescribeAsync(Request(), Token);

        Assert.NotNull(instructions);
        Assert.Null(instructions.RedirectForm);
        Assert.Null(instructions.RedirectUrl);
        Assert.False(instructions.AcceptsProof);
        Assert.Empty(instructions.Fields);
        Assert.Equal(PayHereProvider.PendingNotice, instructions.Notes);
        Assert.Contains("do not pay again", instructions.Notes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Cancelled)]
    [InlineData(PaymentStatus.Expired)]
    [InlineData(PaymentStatus.Succeeded)]
    [InlineData(PaymentStatus.CapturedRequiresRefund)]
    public async Task For_any_other_status_there_is_nothing_to_pay(PaymentStatus status)
    {
        Assert.Null(await Provider(context: Context(status)).DescribeAsync(Request(), Token));
    }

    [Fact]
    public async Task For_an_order_that_is_not_waiting_for_money_there_is_nothing_to_describe()
    {
        // The store answers null for a payment whose order is no longer PendingPayment.
        Assert.Null(await Provider(context: null).DescribeAsync(Request(), Token));
    }

    [Fact]
    public async Task An_unconfigured_account_describes_nothing_rather_than_a_form_that_cannot_work()
    {
        var provider = Provider(PayHereSample.Settings(secret: null), Context());

        Assert.Null(await provider.DescribeAsync(Request(), Token));
    }

    [Fact]
    public async Task Without_a_public_origin_it_describes_nothing()
    {
        Assert.Null(await Provider(context: Context(), origin: null).DescribeAsync(Request(), Token));
    }

    /// <summary>A logger that keeps what it was given, so a test can read the line and its level.</summary>
    internal sealed class ListLogger : ILogger<PayHereProvider>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }
}
