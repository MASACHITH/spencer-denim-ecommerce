using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Delivery;

namespace SpencerDenim.Api.Endpoints.Sales;

/// <summary>
/// <c>GET /api/delivery/countries</c> (owner decision 2026-10-21: Sri Lanka plus worldwide delivery). Public and
/// anonymous: the list holds nothing about any shopper, only where the shop sends parcels and for how much.
/// </summary>
internal static class DeliveryEndpoints
{
    public static IEndpointRouteBuilder MapDeliveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/delivery/countries", GetCountriesAsync)
            .WithTags("Delivery")
            .WithName("GetDeliveryCountries")
            .WithSummary("Every country, with whether the shop delivers there and the overseas fee.")
            .WithDescription(
                "Sri Lanka first (isDomestic: true, priced by district at checkout), then every other country by "
                + "name with its International delivery fee in LKR and estimate, or isAvailable: false when it is "
                + "not delivered to right now. Cached by browsers for one minute, so an admin change shows "
                + "within a minute.")
            .Produces<IReadOnlyList<DeliveryCountryDto>>();

        return endpoints;
    }

    private static async Task<Ok<IReadOnlyList<DeliveryCountryDto>>> GetCountriesAsync(
        IQueryHandler<GetDeliveryCountriesQuery, IReadOnlyList<DeliveryCountryDto>> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var countries = await handler.HandleAsync(new GetDeliveryCountriesQuery(), cancellationToken);

        // Short and public: the same for every visitor, and an admin who switches a country off or changes a
        // fee sees it within a minute. Checkout re-reads the country itself, so a stale list can never place
        // an order at an old fee (G-10 refuses a total that moved).
        httpContext.Response.Headers.CacheControl = "public, max-age=60";

        return TypedResults.Ok(countries);
    }
}
