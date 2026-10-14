using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.PayPal;

/// <summary>
/// The dollar amount PayPal is asked for (doc 08 §3.5 <c>Payments.ProviderAmount</c>, doc 11 P8 "Currency",
/// D1): <c>round(order total ÷ LKR per USD × (1 + buffer%), 2)</c>.
/// <para>
/// Doc 11's worked example is the test: Rs. 15,596.60 at Rs. 300.00 per USD with a 3% buffer is
/// <b>USD 53.55</b> (15,596.60 ÷ 300 = 51.988667; × 1.03 = 53.548627; rounded to the cent).
/// </para>
/// </summary>
public static class PayPalUsdAmount
{
    /// <summary>The currency PayPal is charged in.</summary>
    public const string Currency = "USD";

    /// <summary>
    /// The smallest amount PayPal will take. A very small order converted at a high rate rounds to zero
    /// dollars, which PayPal refuses; one cent is the nearest chargeable amount, and the order's own minimum
    /// (doc 08 §6: the grand total never falls below Rs. 1.00) keeps this a corner case.
    /// </summary>
    public const decimal Minimum = 0.01m;

    /// <summary>
    /// Computes the dollar amount. <b>Rounded half away from zero</b>, the commercial convention: half a
    /// cent goes up, which is also what the customer is shown. <c>decimal</c> throughout — a <c>double</c>
    /// here would charge a cent more or less on some orders and the captured amount would then fail the
    /// exact comparison doc 08 §4.2 step 3.1 makes.
    /// </summary>
    /// <param name="amountLkr">The order's grand total.</param>
    /// <param name="lkrPerUsd">LKR per 1 USD. Must be positive.</param>
    /// <param name="fxBufferPercent">The buffer, in percent. Must not be negative.</param>
    public static decimal Compute(decimal amountLkr, decimal lkrPerUsd, decimal fxBufferPercent)
    {
        if (amountLkr <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amountLkr), "The amount to convert must be positive.");
        }

        if (lkrPerUsd <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(lkrPerUsd), "The exchange rate must be positive.");
        }

        if (fxBufferPercent < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(fxBufferPercent), "The buffer must not be negative.");
        }

        var usd = Math.Round(
            amountLkr / lkrPerUsd * (1m + (fxBufferPercent / 100m)),
            2,
            MidpointRounding.AwayFromZero);

        return Math.Max(usd, Minimum);
    }

    /// <summary>
    /// Formats a dollar amount the way PayPal's API and our own copy want it: invariant culture, exactly
    /// two decimal places, no grouping (<c>53.55</c>).
    /// </summary>
    public static string Format(decimal usd) =>
        usd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The idempotency keys sent to PayPal (<c>PayPal-Request-Id</c>). <b>Each is a pure function of the payment
/// id, so every attempt on one payment sends the same value</b> and a repeated call — a retried request, a
/// recovery run, a second tab — cannot create or capture twice (doc 08 §4.2 step 2).
/// </summary>
public static class PayPalRequestIds
{
    /// <summary>Doc 08 §4.2 step 2: <c>capture-{PaymentId}</c>.</summary>
    public static string Capture(int paymentId) => FormattableString.Invariant($"capture-{paymentId}");

    /// <summary>
    /// The create-order call's key. PayPal keeps a request id for hours, so if our process dies after PayPal
    /// created the order and before we stored its id, the next attempt gets the same order back instead of a
    /// second one.
    /// </summary>
    public static string Create(int paymentId) => FormattableString.Invariant($"create-{paymentId}");

    /// <summary>
    /// What is written in the purchase unit's <c>custom_id</c>, and so comes back in webhooks: the way an
    /// event for a capture we have not recorded yet is tied to its payment.
    /// </summary>
    public static string CustomId(int paymentId) => FormattableString.Invariant($"pay-{paymentId}");

    /// <summary>The payment id inside a <see cref="CustomId"/>, or null when the text is not one of ours.</summary>
    public static int? PaymentIdFromCustomId(string? customId)
    {
        const string prefix = "pay-";

        return customId is { } text
            && text.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(
                text.AsSpan(prefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var id)
            && id > 0
            ? id
            : null;
    }
}

/// <summary>
/// What a new PayPal payment looks like (doc 08 §4.1 "— → PendingPayment": PayPal opens
/// <see cref="PaymentStatus.Initiated"/> with a USD <c>ProviderAmount</c> at a frozen rate), shared by the
/// provider at checkout and by a retry from the order page so the two cannot compute it differently.
/// </summary>
public static class PayPalInitiationPolicy
{
    /// <summary>
    /// The payment row a new attempt starts with. <paramref name="settings"/> must be
    /// <see cref="PayPalAvailability.Available"/>; the caller has checked.
    /// </summary>
    public static PaymentInitiation Build(PayPalSettings settings, decimal amountLkr, DateTime placedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var rate = settings.LkrPerUsd
            ?? throw new InvalidOperationException("PayPal has no exchange rate, so no dollar amount can be worked out.");

        return new PaymentInitiation(
            PaymentStatus.Initiated,
            placedAtUtc.AddMinutes(settings.ExpiryMinutes),
            PayPalUsdAmount.Currency,
            PayPalUsdAmount.Compute(amountLkr, rate, settings.FxBufferPercent),
            rate,
            settings.FxBufferPercent);
    }
}
