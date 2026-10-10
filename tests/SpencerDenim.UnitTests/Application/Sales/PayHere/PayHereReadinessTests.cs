using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Sales.Payments.PayHere;

namespace SpencerDenim.UnitTests.Application.Sales.PayHere;

/// <summary>
/// When PayHere may be offered at checkout (doc 11 P8b preconditions), what its settings mean, and the
/// addresses it is given.
/// </summary>
public sealed class PayHereReadinessTests
{
    private const string Origin = "https://shop.example.test";

    [Fact]
    public void A_complete_account_with_a_public_origin_has_no_problems()
    {
        Assert.Empty(PayHereReadiness.Problems(PayHereSample.Settings(), Origin));
    }

    [Theory]
    [InlineData(null, "1211149", "secret", false)]
    [InlineData("sandbox", null, "secret", false)]
    [InlineData("sandbox", "  ", "secret", false)]
    [InlineData("sandbox", "1211149", null, false)]
    [InlineData("sandbox", "1211149", "", false)]
    [InlineData("live", "1211149", "secret", true)]
    [InlineData("sandbox", "1211149", "secret", true)]
    public void The_account_is_configured_only_with_a_mode_a_merchant_id_and_a_secret(
        string? mode,
        string? merchantId,
        string? secret,
        bool expected)
    {
        Assert.Equal(expected, PayHereSample.Settings(mode, merchantId, secret).IsConfigured);
    }

    [Fact]
    public void Each_missing_piece_is_named_so_an_administrator_can_fix_it()
    {
        var problems = PayHereReadiness.Problems(
            PayHereSample.Settings(mode: null, merchantId: null, secret: null),
            publicOrigin: null);

        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, problem => problem.Contains("mode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(problems, problem => problem.Contains("merchant ID", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(problems, problem => problem.Contains("merchant secret", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(problems, problem => problem.Contains("Email__StorefrontBaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_public_address_PayHere_is_not_ready_however_complete_the_account()
    {
        Assert.Single(PayHereReadiness.Problems(PayHereSample.Settings(), publicOrigin: null));
        Assert.Single(PayHereReadiness.Problems(PayHereSample.Settings(), publicOrigin: "not a url"));
    }

    [Theory]
    [InlineData(null, "sandbox")]
    [InlineData("", "sandbox")]
    [InlineData("   ", "sandbox")]
    [InlineData("sandbox", "sandbox")]
    [InlineData("SANDBOX", "sandbox")]
    [InlineData(" Live ", "live")]
    [InlineData("live", "live")]
    public void The_stored_mode_is_normalised(string? stored, string expected)
    {
        Assert.Equal(expected, PayHereSettings.NormaliseMode(stored));
    }

    /// <summary>
    /// A value that is neither is not guessed at: "live" would be the wrong fallback and "sandbox" would
    /// quietly send real customers to a test page.
    /// </summary>
    [Theory]
    [InlineData("production")]
    [InlineData("test")]
    [InlineData("sandbox!")]
    [InlineData("0")]
    public void An_unknown_mode_is_not_a_mode(string stored)
    {
        Assert.Null(PayHereSettings.NormaliseMode(stored));
        Assert.Null(PayHereSample.Settings(mode: PayHereSettings.NormaliseMode(stored)).CheckoutUrl);
    }

    [Fact]
    public void The_settings_never_print_the_secret()
    {
        var text = PayHereSample.Settings(secret: "super-secret-value").ToString();

        Assert.DoesNotContain("super-secret-value", text, StringComparison.Ordinal);
        Assert.Contains("MerchantSecret = set", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://shop.example.test", "https://shop.example.test/api/payments/payhere/notify")]
    [InlineData("https://shop.example.test/", "https://shop.example.test/api/payments/payhere/notify")]
    [InlineData("  https://shop.example.test//  ", "https://shop.example.test/api/payments/payhere/notify")]
    [InlineData("http://localhost:4200", "http://localhost:4200/api/payments/payhere/notify")]
    public void The_notify_address_is_the_public_origin_plus_the_api_path(string origin, string expected)
    {
        Assert.Equal(expected, PayHereRoutes.NotifyUrl(origin));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("shop.example.test")]
    [InlineData("javascript:alert(1)")]
    public void No_origin_means_no_notify_address(string? origin)
    {
        Assert.Null(PayHereRoutes.NotifyUrl(origin));
        Assert.Null(PayHereRoutes.OrderPageUrl(origin, PayHereSample.OrderPublicId, PayHereRoutes.ReturnMarker));
    }

    [Fact]
    public void The_return_and_cancel_addresses_carry_a_marker_and_nothing_else()
    {
        Assert.Equal(
            "https://shop.example.test/order/0f8fad5b-d9cb-469f-a165-70867728950e?payment=return",
            PayHereRoutes.OrderPageUrl(Origin, PayHereSample.OrderPublicId, PayHereRoutes.ReturnMarker));
        Assert.Equal(
            "https://shop.example.test/order/0f8fad5b-d9cb-469f-a165-70867728950e?payment=cancelled",
            PayHereRoutes.OrderPageUrl(Origin, PayHereSample.OrderPublicId, PayHereRoutes.CancelMarker));
    }

    [Fact]
    public async Task The_status_query_reports_what_checkout_would_do_and_never_the_secret()
    {
        var handler = new GetPayHereStatusQueryHandler(
            new FakePayHereSettings(PayHereSample.Settings(secret: "do-not-leak-me")),
            new FixedSwitch(true),
            new EmailOptions { StorefrontBaseUrl = Origin });

        var status = await handler.HandleAsync(new GetPayHereStatusQuery(), TestContext.Current.CancellationToken);

        Assert.True(status.ProviderEnabled);
        Assert.True(status.OfferedAtCheckout);
        Assert.Empty(status.Problems);
        Assert.Equal("sandbox", status.Mode);
        Assert.Equal("https://shop.example.test/api/payments/payhere/notify", status.NotifyUrl);
        Assert.DoesNotContain("do-not-leak-me", System.Text.Json.JsonSerializer.Serialize(status), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public async Task PayHere_is_offered_only_when_switched_on_and_ready(bool switchedOn, bool ready, bool offered)
    {
        var handler = new GetPayHereStatusQueryHandler(
            new FakePayHereSettings(PayHereSample.Settings(merchantId: ready ? "1211149" : null)),
            new FixedSwitch(switchedOn),
            new EmailOptions { StorefrontBaseUrl = Origin });

        var status = await handler.HandleAsync(new GetPayHereStatusQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(offered, status.OfferedAtCheckout);
        Assert.Equal(switchedOn, status.ProviderEnabled);
        Assert.Equal(ready, status.Problems.Count == 0);
    }

    [Fact]
    public async Task Without_a_public_origin_the_status_has_no_notify_address_and_says_why()
    {
        var handler = new GetPayHereStatusQueryHandler(
            new FakePayHereSettings(PayHereSample.Settings()),
            new FixedSwitch(true),
            new EmailOptions());

        var status = await handler.HandleAsync(new GetPayHereStatusQuery(), TestContext.Current.CancellationToken);

        Assert.Null(status.NotifyUrl);
        Assert.False(status.OfferedAtCheckout);
        Assert.Single(status.Problems);
    }

    private sealed class FixedSwitch(bool enabled) : IPayHereProviderSwitch
    {
        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(enabled);
    }
}
