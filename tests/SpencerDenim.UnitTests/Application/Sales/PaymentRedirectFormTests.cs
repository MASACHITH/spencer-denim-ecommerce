using SpencerDenim.Application.Sales.Payments.Contracts;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// The redirect part of <see cref="PaymentInstructions"/> (PAY-03): a form the browser posts to a
/// provider's hosted page, or a link to it. The storefront applies the same address rule in its own
/// language, so a provider that breaks it finds out here and not on a customer's screen.
/// </summary>
public sealed class PaymentRedirectFormTests
{
    [Fact]
    public void A_post_form_keeps_its_fields_in_the_order_given()
    {
        var form = PaymentRedirectForm.Post(
            "https://pay.example.test/checkout",
            [
                new PaymentRedirectFormField("merchant_id", "121XXXX"),
                new PaymentRedirectFormField("order_id", "CW-2026-000123-4567"),
                new PaymentRedirectFormField("hash", "ABCDEF0123"),
            ]);

        Assert.Equal("POST", form.Method);
        Assert.Equal(PaymentRedirectForm.PostMethod, form.Method);
        Assert.Equal("https://pay.example.test/checkout", form.Action);
        Assert.Equal(["merchant_id", "order_id", "hash"], form.Fields.Select(field => field.Name).ToArray());
        Assert.Equal("ABCDEF0123", form.Fields[2].Value);
    }

    [Theory]
    [InlineData("http://pay.example.test/checkout")]
    [InlineData("/relative/path")]
    [InlineData("pay.example.test/checkout")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ftp://pay.example.test/")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_form_cannot_post_to_an_address_the_order_page_would_not_follow(string action)
    {
        Assert.Throws<ArgumentException>(
            () => PaymentRedirectForm.Post(action, [new PaymentRedirectFormField("a", "b")]));
    }

    [Theory]
    [InlineData("http://localhost:8080/checkout")]
    [InlineData("http://127.0.0.1/checkout")]
    [InlineData("https://sandbox.example.test/pay")]
    public void Plain_http_is_allowed_for_a_loopback_host_so_a_developer_machine_works(string action)
    {
        Assert.True(PaymentRedirectForm.IsSafeAbsoluteUrl(action));
        Assert.Equal(action, PaymentRedirectForm.Post(action, []).Action);
    }

    [Fact]
    public void A_field_without_a_name_is_refused()
    {
        Assert.Throws<ArgumentException>(
            () => PaymentRedirectForm.Post(
                "https://pay.example.test/checkout",
                [new PaymentRedirectFormField(" ", "value")]));
    }

    [Fact]
    public void A_request_for_instructions_defaults_to_no_attempt_so_existing_callers_are_unchanged()
    {
        var request = new PaymentInstructionsRequest(
            "CW-2026-000123",
            1500m,
            new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc),
            OrderUrl: null);

        Assert.Null(request.PaymentId);
        Assert.Null(request.ProviderReference);
    }

    [Fact]
    public void Instructions_default_to_no_redirect_form_so_the_bank_transfer_is_unchanged()
    {
        var instructions = new PaymentInstructions(
            "BankTransfer",
            "CW-2026-000123",
            1500m,
            new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc),
            [],
            null,
            RedirectUrl: null,
            AcceptsProof: true);

        Assert.Null(instructions.RedirectForm);
    }
}
