using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Checkout.Abstractions;

namespace SpencerDenim.Application.Sales.Delivery;

/// <summary>Every country a shopper can choose, and what delivery there costs (owner decision 2026-10-21).</summary>
public sealed record GetDeliveryCountriesQuery;

/// <summary>
/// One country for the storefront's country selector (header, bag, checkout, address book).
/// </summary>
/// <param name="Code">ISO 3166-1 alpha-2.</param>
/// <param name="Name">English short name.</param>
/// <param name="IsDomestic">True for Sri Lanka only, which keeps the district selector.</param>
/// <param name="IsAvailable">Whether an order can be sent here right now. Always true for Sri Lanka.</param>
/// <param name="PostalCodeRequired">Whether an address here must carry a postcode.</param>
/// <param name="RegionRequired">Whether an address here must name a state or region.</param>
/// <param name="Fee">
/// Standard delivery fee in LKR, or null for Sri Lanka and for a country where standard is not available.
/// Kept for backward-compat; identical to <paramref name="StandardFee"/>.
/// </param>
/// <param name="EstimatedDaysMin">Standard estimate, or null like <paramref name="Fee"/>.</param>
/// <param name="EstimatedDaysMax">Standard estimate, or null like <paramref name="Fee"/>.</param>
/// <param name="StandardFee">Standard delivery fee in LKR, or null when not available.</param>
/// <param name="StandardEstimatedDaysMin">Standard delivery estimate, or null when not available.</param>
/// <param name="StandardEstimatedDaysMax">Standard delivery estimate, or null when not available.</param>
/// <param name="ExpressFee">Express delivery fee in LKR, or null when express is not offered for this country.</param>
/// <param name="ExpressEstimatedDaysMin">Express estimate, or null when not available.</param>
/// <param name="ExpressEstimatedDaysMax">Express estimate, or null when not available.</param>
public sealed record DeliveryCountryDto(
    string Code,
    string Name,
    bool IsDomestic,
    bool IsAvailable,
    bool PostalCodeRequired,
    bool RegionRequired,
    decimal? Fee,
    byte? EstimatedDaysMin,
    byte? EstimatedDaysMax,
    decimal? StandardFee,
    byte? StandardEstimatedDaysMin,
    byte? StandardEstimatedDaysMax,
    decimal? ExpressFee,
    byte? ExpressEstimatedDaysMin,
    byte? ExpressEstimatedDaysMax);

/// <summary>Reads the country list through <see cref="IDeliveryCountryReader"/>, the one checkout uses.</summary>
public sealed class GetDeliveryCountriesQueryHandler(IDeliveryCountryReader reader)
    : IQueryHandler<GetDeliveryCountriesQuery, IReadOnlyList<DeliveryCountryDto>>
{
    public async Task<IReadOnlyList<DeliveryCountryDto>> HandleAsync(
        GetDeliveryCountriesQuery query,
        CancellationToken cancellationToken)
    {
        var countries = await reader.CountriesAsync(cancellationToken);

        return countries.Select(ToDto).ToList().AsReadOnly();
    }

    /// <summary>The DTO for one country: overseas fees only where they can actually be charged.</summary>
    public static DeliveryCountryDto ToDto(CheckoutCountry country)
    {
        ArgumentNullException.ThrowIfNull(country);

        var standardPriced = !country.IsDomestic && country.StandardQuote.IsAvailable;
        var expressPriced = !country.IsDomestic && country.ExpressQuote.IsAvailable;

        return new DeliveryCountryDto(
            country.Code,
            country.Name,
            country.IsDomestic,
            country.IsServed,
            country.PostalCodeRequired,
            country.RegionRequired,
            standardPriced ? country.StandardQuote.Fee : null,
            standardPriced ? country.StandardQuote.EstimatedDaysMin : null,
            standardPriced ? country.StandardQuote.EstimatedDaysMax : null,
            standardPriced ? country.StandardQuote.Fee : null,
            standardPriced ? country.StandardQuote.EstimatedDaysMin : null,
            standardPriced ? country.StandardQuote.EstimatedDaysMax : null,
            expressPriced ? country.ExpressQuote.Fee : null,
            expressPriced ? country.ExpressQuote.EstimatedDaysMin : null,
            expressPriced ? country.ExpressQuote.EstimatedDaysMax : null);
    }
}
