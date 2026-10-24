namespace SpencerDenim.Application.Settings.Contracts;

/// <summary>
/// One currency the storefront may show prices in, and what it costs in rupees (issue #248). Hand-written, with a
/// field for each of the four things the storefront needs and no place for anything else, so the question "could a
/// secret or PayPal's own rate reach this endpoint" has a structural answer rather than a filtering one.
/// </summary>
/// <param name="Code">ISO 4217: <c>USD</c>, <c>GBP</c>, <c>EUR</c>, <c>AUD</c> or <c>AED</c>.</param>
/// <param name="Symbol">What goes before the figure: <c>US$</c>, <c>£</c>, <c>€</c>, <c>A$</c> or <c>AED</c>.</param>
/// <param name="LkrPerUnit">Rupees for one unit of this currency: a price is divided by it to be shown here.</param>
/// <param name="UpdatedAtUtc">
/// When staff last changed the rate, in UTC, or <see langword="null"/> for a row somebody set outside the admin.
/// For a note such as "rates as of 24 October"; nothing hides a currency because this is old.
/// </param>
public sealed record CurrencyRateDto(string Code, string Symbol, decimal LkrPerUnit, DateTime? UpdatedAtUtc);
