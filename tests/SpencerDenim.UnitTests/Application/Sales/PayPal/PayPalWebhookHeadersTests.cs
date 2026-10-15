using SpencerDenim.Application.Sales.Payments.PayPal;

namespace SpencerDenim.UnitTests.Application.Sales.PayPal;

/// <summary>
/// <see cref="PayPalWebhookHeaders.LooksLikePayPal"/>: the cheap question the webhook endpoint asks before it
/// spends a throttle permit, and the gateway asks again before it makes a call to PayPal (#166).
/// <para>
/// It proves nothing about authenticity and these tests do not pretend it does. What they pin is the
/// refusal side: every request that cannot be a PayPal notification must be turned away here, because a
/// request that gets past costs a permit and, when one is left, a call to PayPal.
/// </para>
/// </summary>
public sealed class PayPalWebhookHeadersTests
{
    private const string Cert = "https://api.sandbox.paypal.com/v1/notifications/certs/CERT-TEST";

    private static PayPalWebhookHeaders Headers(
        string authAlgo = "SHA256withRSA",
        string certUrl = Cert,
        string transmissionId = "5b0e0e50-0000-0000-0000-000000000001",
        string transmissionSig = "c2ln",
        string transmissionTime = "2026-10-10T09:08:45Z") =>
        new(authAlgo, certUrl, transmissionId, transmissionSig, transmissionTime);

    [Fact]
    public void All_five_headers_with_a_certificate_on_a_PayPal_host_look_like_PayPal()
    {
        Assert.True(Headers().LooksLikePayPal());
    }

    [Theory]
    [InlineData("https://api.paypal.com/v1/notifications/certs/CERT-LIVE")]
    [InlineData("https://API.SANDBOX.PAYPAL.COM/v1/notifications/certs/CERT")]
    public void A_certificate_on_either_PayPal_environment_is_accepted(string certUrl)
    {
        Assert.True(Headers(certUrl: certUrl).LooksLikePayPal());
    }

    [Fact]
    public void A_request_with_no_headers_at_all_does_not()
    {
        Assert.False(new PayPalWebhookHeaders(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty)
            .LooksLikePayPal());
    }

    [Theory]
    [InlineData("authAlgo")]
    [InlineData("certUrl")]
    [InlineData("transmissionId")]
    [InlineData("transmissionSig")]
    [InlineData("transmissionTime")]
    public void Any_one_missing_header_is_enough_to_refuse(string missing)
    {
        var headers = missing switch
        {
            "authAlgo" => Headers(authAlgo: string.Empty),
            "certUrl" => Headers(certUrl: string.Empty),
            "transmissionId" => Headers(transmissionId: "  "),
            "transmissionSig" => Headers(transmissionSig: string.Empty),
            _ => Headers(transmissionTime: string.Empty),
        };

        Assert.False(headers.LooksLikePayPal());
    }

    /// <summary>
    /// PayPal's verification fetches the certificate from this address, so it has to be PayPal's own: an
    /// attacker-controlled host here would be a way to make verification talk to them.
    /// </summary>
    [Theory]
    [InlineData("http://api.sandbox.paypal.com/v1/notifications/certs/CERT")]
    [InlineData("https://evil.example.com/v1/notifications/certs/CERT")]
    [InlineData("https://paypal.com.evil.example/certs/CERT")]
    [InlineData("https://evilpaypal.com/certs/CERT")]
    [InlineData("https://api.sandbox.paypal.com.evil.example/certs/CERT")]
    [InlineData("https://evil.example/.paypal.com")]
    [InlineData("https://paypal.com/certs/CERT")]
    [InlineData("/v1/notifications/certs/CERT")]
    [InlineData("ftp://api.sandbox.paypal.com/certs/CERT")]
    [InlineData("not a url")]
    public void A_certificate_address_that_is_not_PayPals_own_is_refused(string certUrl)
    {
        Assert.False(Headers(certUrl: certUrl).LooksLikePayPal());
    }
}
