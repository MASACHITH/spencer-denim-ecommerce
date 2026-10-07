using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.Contracts;

/// <summary>
/// What checkout tells a provider about the order it is about to place (PAY-03).
/// </summary>
/// <param name="AmountLkr">
/// The order's <c>GrandTotal</c>, which is <c>sales.Payments.Amount</c>. Already priced and already
/// compared against the figure the shopper was shown (G-10); a provider must never reprice it.
/// </param>
/// <param name="PlacedAtUtc">
/// The placement instant. <see cref="PaymentInitiation.PaymentDueAtUtc"/> is measured from this and not
/// from the provider's own clock reading, so the order row and the payment row cannot disagree about when
/// the order was placed.
/// </param>
public sealed record PaymentInitiationRequest(decimal AmountLkr, DateTime PlacedAtUtc);

/// <summary>
/// The provider's answer: the shape of the one open payment row doc 08 §4.1 lists as a placement side
/// effect, plus the order's payment deadline.
/// </summary>
/// <param name="Status">
/// <see cref="PaymentStatus.Pending"/> for a bank transfer, <see cref="PaymentStatus.Initiated"/> for
/// PayPal. Both are inside <c>UX_Payments_OneOpen</c>'s filter, which is what makes "one open attempt per
/// order" the database's rule rather than the application's.
/// </param>
/// <param name="PaymentDueAtUtc">
/// <c>Orders.PaymentDueAtUtc</c>. The expiry job releases the stock after it, so a provider that returned
/// a time in the past would have its orders expired before the customer could pay — which is why
/// <c>PaymentProviderContractTests</c> asserts it is strictly after
/// <see cref="PaymentInitiationRequest.PlacedAtUtc"/>.
/// </param>
/// <param name="ProviderCurrency">
/// The currency the provider actually charges in — <c>USD</c> for PayPal (D1), <see langword="null"/> for
/// a bank transfer, which charges in LKR like the order.
/// </param>
/// <param name="ProviderAmount">The amount in <paramref name="ProviderCurrency"/>, null when there is none.</param>
/// <param name="ExchangeRate">LKR per 1 unit of <paramref name="ProviderCurrency"/>, frozen now.</param>
/// <param name="FxBufferPercent">The buffer applied to that rate.</param>
public sealed record PaymentInitiation(
    PaymentStatus Status,
    DateTime PaymentDueAtUtc,
    string? ProviderCurrency = null,
    decimal? ProviderAmount = null,
    decimal? ExchangeRate = null,
    decimal? FxBufferPercent = null);
