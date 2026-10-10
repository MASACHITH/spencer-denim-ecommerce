using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments;

/// <summary>
/// The order id a payment provider was given for one payment attempt, <b>when it is derived rather than
/// stored</b> - what a member of staff types into the provider's own portal to find the payment (a refund is
/// made there, by hand, in v1).
/// <para>
/// PayHere is told <c>{OrderNumber}-{PaymentId}</c> (<see cref="PayHereOrderReference"/>) and it is not kept in
/// a column of its own, because it is always recomputable from two values the payment row already holds.
/// PayPal's is a <c>ProviderReference</c> it returned, so it needs nothing here; a bank transfer has none. The
/// switch is the one place that knows which provider names an attempt how, so the staff screen and its reader
/// stay neutral and a new gateway adds one line.
/// </para>
/// </summary>
public static class ProviderOrderIds
{
    /// <summary>
    /// The id the provider was given for payment <paramref name="paymentId"/> of the order numbered
    /// <paramref name="orderNumber"/>, or <see langword="null"/> when the provider has no derived id.
    /// </summary>
    public static string? For(string providerCode, string orderNumber, int paymentId)
    {
        if (!string.Equals(providerCode, PaymentProviderCodes.PayHere, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // An order number that is not one this shop issues (a fixture, a hand-edited row) must not turn a
        // read of the order into an exception: the staff screen shows nothing rather than failing.
        return PayHereOrderReference.TryParse($"{orderNumber}-{paymentId}", out _, out _)
            ? PayHereOrderReference.Format(orderNumber, paymentId)
            : null;
    }
}
