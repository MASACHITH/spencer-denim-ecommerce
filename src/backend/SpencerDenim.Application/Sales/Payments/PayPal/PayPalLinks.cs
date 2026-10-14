using System.Globalization;
using SpencerDenim.Application.Ops.Email;

namespace SpencerDenim.Application.Sales.Payments.PayPal;

/// <summary>
/// What the customer is told about a payment when the shop sends them back to their order page
/// (<c>/order/{token}?pay=…</c>). <b>A message, never proof</b>: the order page reads the order's real status
/// from the server and the query string only picks the sentence that explains what just happened (doc 11 P8
/// step 3: the return is informational).
/// </summary>
public static class PayPalPageHints
{
    /// <summary>The query-string parameter.</summary>
    public const string Parameter = "pay";

    /// <summary>The order is paid. The order's own status says so; this only confirms the visit came from PayPal.</summary>
    public const string Paid = "paid";

    /// <summary>
    /// PayPal took the money and the order could not take it (it expired, was cancelled, was already paid, or
    /// the amount differed): "We received your payment, our team will contact you" (doc 11 P8 step 6).
    /// </summary>
    public const string Received = "received";

    /// <summary>PayPal is holding the payment: "Your payment is pending with PayPal".</summary>
    public const string Pending = "pending";

    /// <summary>PayPal refused it: "Payment declined, try again".</summary>
    public const string Declined = "declined";

    /// <summary>Another attempt is in flight, or the result is not known yet: "Your payment is being processed".</summary>
    public const string Processing = "processing";

    /// <summary>The buyer came back without approving at PayPal.</summary>
    public const string NotApproved = "notapproved";

    /// <summary>The buyer backed out at PayPal.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>"This order can no longer be paid."</summary>
    public const string Closed = "closed";

    /// <summary>PayPal cannot be used right now (not configured, rate out of date, unreachable).</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// The addresses PayPal and the buyer's browser are sent to, all under the storefront's own origin.
/// <para>
/// <b>One origin, because that is how the shop is deployed</b> (doc 03 §4): Caddy serves the storefront and
/// routes <c>/api</c> to this API on the same host, so the storefront's origin is also the API's. The only
/// public origin this build is configured with is <c>Email:StorefrontBaseUrl</c> (W3-06), so it is reused
/// rather than inventing a second setting for the same fact. It is never taken from a request's
/// <c>Host</c> header, which a caller controls.
/// </para>
/// <para>
/// <b>Null when that origin is not set</b>, and then PayPal cannot be sent a return address and is not
/// offered: a payment page whose "Pay now" button goes nowhere is worse than none.
/// </para>
/// </summary>
public sealed class PayPalLinks(EmailOptions options)
{
    private string? Origin => options.NormalisedStorefrontBaseUrl;

    /// <summary>Whether links can be built at all.</summary>
    public bool IsAvailable => Origin is not null;

    /// <summary>The customer-facing start endpoint: creates the PayPal order and sends the buyer to approve it.</summary>
    public string? StartUrl(Guid orderPublicId) => Build("start", orderPublicId);

    /// <summary>Where PayPal sends the buyer after they approve.</summary>
    public string? ReturnUrl(Guid orderPublicId) => Build("return", orderPublicId);

    /// <summary>Where PayPal sends a buyer who backs out.</summary>
    public string? CancelUrl(Guid orderPublicId) => Build("cancel", orderPublicId);

    /// <summary>
    /// The path of the order page, relative to whatever origin the buyer is on, with the hint that picks the
    /// sentence. Relative on purpose: it is a redirect inside the same site.
    /// </summary>
    public static string OrderPagePath(Guid orderPublicId, string? hint)
    {
        var path = $"/order/{orderPublicId.ToString("D", CultureInfo.InvariantCulture)}";

        return string.IsNullOrEmpty(hint) ? path : $"{path}?{PayPalPageHints.Parameter}={Uri.EscapeDataString(hint)}";
    }

    private string? Build(string action, Guid orderPublicId) =>
        Origin is { } origin
            ? $"{origin}/api/payments/paypal/{action}/{orderPublicId.ToString("D", CultureInfo.InvariantCulture)}"
            : null;
}
