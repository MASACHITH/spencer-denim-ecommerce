using System.Globalization;
using SpencerDenim.Application.Sales.Payments.Contracts;

namespace SpencerDenim.Application.Sales.Payments.PayHere;

/// <summary>The addresses PayHere is told, built from the storefront's public origin.</summary>
public static class PayHereRoutes
{
    /// <summary>
    /// The server-to-server notification endpoint, relative to the public origin. The reverse proxy sends
    /// <c>/api</c> to the API on the same origin as the storefront (doc 03 §4), so the address PayHere is
    /// given is the shop's own domain plus this path.
    /// </summary>
    public const string NotifyPath = "/api/payments/payhere/notify";

    /// <summary>The value of the <c>payment</c> query parameter PayHere's return address carries.</summary>
    public const string ReturnMarker = "return";

    /// <summary>The value of the <c>payment</c> query parameter PayHere's cancel address carries.</summary>
    public const string CancelMarker = "cancelled";

    /// <summary>
    /// <c>https://shop.example/api/payments/payhere/notify</c>, or <see langword="null"/> when no public
    /// origin is configured — in which case PayHere cannot be given an address it could call.
    /// </summary>
    public static string? NotifyUrl(string? publicOrigin) =>
        Origin(publicOrigin) is { } origin ? origin + NotifyPath : null;

    /// <summary>
    /// The storefront's order page for an order, with a marker that says why the customer is there. Display
    /// only: <b>the marker is never proof of anything</b>, and the page changes nothing because of it except
    /// the words it shows (doc 11 P8b step 3).
    /// </summary>
    public static string? OrderPageUrl(string? publicOrigin, Guid orderPublicId, string marker) =>
        Origin(publicOrigin) is { } origin
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{origin}/order/{orderPublicId:D}?payment={marker}")
            : null;

    /// <summary>The origin without a trailing slash, or null when it is not an absolute http(s) address.</summary>
    public static string? Origin(string? publicOrigin)
    {
        if (string.IsNullOrWhiteSpace(publicOrigin))
        {
            return null;
        }

        var trimmed = publicOrigin.Trim().TrimEnd('/');

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && string.IsNullOrEmpty(uri.UserInfo)
            ? trimmed
            : null;
    }
}

/// <summary>
/// Builds the form the customer's browser posts to PayHere (doc 11 P8b step 1): every value comes from the
/// server's own order, nothing from the client, and the <c>hash</c> field is what lets PayHere refuse a form
/// somebody has edited.
/// <para>
/// <b>The field order is the contract with the order page, which renders the fields in the order given</b>,
/// and is the order PayHere's own documentation lists them in: the merchant, the three addresses, the order
/// and what it is for, the money, the customer and the signature last.
/// </para>
/// <para>
/// <b>Nothing secret is in it.</b> The merchant secret is an input to <c>hash</c> and is never a field; the
/// hash is a digest of the secret and the order, which cannot be turned back into either.
/// </para>
/// <para>
/// <b>PayHere requires the customer fields to be non-empty, and an order does not always have them.</b> A
/// pickup order has no delivery address and a customer may have no last name or telephone number, so each has
/// a fixed, plainly-placeholder fallback rather than an empty value PayHere would reject. Each is also
/// clipped, so an unusually long address cannot make the gateway refuse an order that is otherwise fine.
/// </para>
/// </summary>
public static class PayHereCheckoutForm
{
    /// <summary>What PayHere is told the payment is for. The shop's name is PayHere's to show: it has the merchant's.</summary>
    private const string ItemsPrefix = "Order ";

    private const string Country = "Sri Lanka";

    private const int NameMaxLength = 50;
    private const int EmailMaxLength = 100;
    private const int PhoneMaxLength = 20;
    private const int AddressMaxLength = 100;
    private const int CityMaxLength = 50;

    /// <summary>First name when the order has none worth sending.</summary>
    public const string FallbackFirstName = "Customer";

    /// <summary>Last name when the order has none.</summary>
    public const string FallbackLastName = "-";

    /// <summary>Telephone number when the order has none.</summary>
    public const string FallbackPhone = "0000000000";

    /// <summary>Address when the order has none (a pickup order, say, with no store name).</summary>
    public const string FallbackAddress = "Not provided";

    /// <summary>City when the order has none.</summary>
    public const string FallbackCity = "N/A";

    /// <summary>
    /// The form for one payment attempt.
    /// </summary>
    /// <param name="settings">Must be <see cref="PayHereSettings.IsConfigured"/>.</param>
    /// <param name="context">The order the attempt belongs to.</param>
    /// <param name="publicOrigin">The storefront's public origin, which the three addresses are built from.</param>
    /// <exception cref="InvalidOperationException">Not configured, or no usable public origin.</exception>
    public static PaymentRedirectForm Build(
        PayHereSettings settings,
        PayHereCheckoutContext context,
        string? publicOrigin)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);

        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("PayHere is not configured: it needs a mode, a merchant ID and a secret.");
        }

        var returnUrl = PayHereRoutes.OrderPageUrl(publicOrigin, context.OrderPublicId, PayHereRoutes.ReturnMarker);
        var cancelUrl = PayHereRoutes.OrderPageUrl(publicOrigin, context.OrderPublicId, PayHereRoutes.CancelMarker);
        var notifyUrl = PayHereRoutes.NotifyUrl(publicOrigin);

        if (returnUrl is null || cancelUrl is null || notifyUrl is null)
        {
            throw new InvalidOperationException(
                "PayHere needs the storefront's public address (Email:StorefrontBaseUrl) to give it a "
                + "return, cancel and notify address.");
        }

        var merchantId = settings.MerchantId!.Trim();
        var orderId = PayHereOrderReference.Format(context.OrderNumber, context.PaymentId);
        var amount = PayHereHash.FormatAmount(context.AmountLkr);

        var fields = new List<PaymentRedirectFormField>(16)
        {
            new("merchant_id", merchantId),
            new("return_url", returnUrl),
            new("cancel_url", cancelUrl),
            new("notify_url", notifyUrl),
            new("order_id", orderId),
            new("items", ItemsPrefix + context.OrderNumber),
            new("currency", PayHereHash.Currency),
            new("amount", amount),
            new("first_name", PayHereText.Clip(context.FirstName, NameMaxLength) ?? FallbackFirstName),
            new("last_name", PayHereText.Clip(context.LastName, NameMaxLength) ?? FallbackLastName),
            new("email", PayHereText.Clip(context.Email, EmailMaxLength) ?? string.Empty),
            new("phone", PayHereText.Clip(context.Phone, PhoneMaxLength) ?? FallbackPhone),
            new("address", PayHereText.Clip(context.AddressLine, AddressMaxLength) ?? FallbackAddress),
            new("city", PayHereText.Clip(context.City, CityMaxLength) ?? FallbackCity),
            new("country", Country),
            new(
                "hash",
                PayHereHash.CheckoutHash(merchantId, orderId, amount, PayHereHash.Currency, settings.MerchantSecret!)),
        };

        return PaymentRedirectForm.Post(settings.CheckoutUrl!, fields);
    }
}
