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
/// The overseas delivery fee in LKR, or null for Sri Lanka (priced by district at checkout) and for a country
/// that is not available.
/// </param>
/// <param name="EstimatedDaysMin">The overseas estimate, or null like <paramref name="Fee"/>.</param>
/// <param name="EstimatedDaysMax">The overseas estimate, or null like <paramref name="Fee"/>.</param>
public sealed record DeliveryCountryDto(
    string Code,
    string Name,
    bool IsDomestic,
    bool IsAvailable,
    bool PostalCodeRequired,
    bool RegionRequired,
    decimal? Fee,
    byte? EstimatedDaysMin,
    byte? EstimatedDaysMax);

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

    /// <summary>The DTO for one country: an overseas fee only where one can actually be charged.</summary>
    public static DeliveryCountryDto ToDto(CheckoutCountry country)
    {
        ArgumentNullException.ThrowIfNull(country);

        var priced = !country.IsDomestic && country.Quote.IsAvailable;

        return new DeliveryCountryDto(
            country.Code,
            country.Name,
            country.IsDomestic,
            country.IsServed,
            country.PostalCodeRequired,
            country.RegionRequired,
            priced ? country.Quote.Fee : null,
            priced ? country.Quote.EstimatedDaysMin : null,
            priced ? country.Quote.EstimatedDaysMax : null);
    }
}
