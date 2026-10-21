using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Content.DeliveryReturns;

/// <summary>The product page's delivery and returns panel (PDP-05, issue #33).</summary>
public sealed record GetDeliveryReturnsQuery;

/// <summary>
/// Reads the panel from the delivery tables and the two <c>Content.Pdp*</c> settings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through <see cref="ICheckoutReader"/>, the reader checkout prices from</b>, so "what is offered" has one
/// definition: an enabled method with an available rate for the zone (doc 08 §3.5), an active district, an
/// active pickup location in an active district. The panel cannot list an option checkout would refuse, or
/// quote a fee checkout would not charge.
/// </para>
/// <para>
/// <b>Not cached.</b> It is a handful of small reads, and the owner who has just corrected a fee or a sentence
/// should see it on the next product page, as with a content page's body. The answer is identical for every
/// visitor, so a cache in front of the server may hold it; nothing here would make that unsafe.
/// </para>
/// </remarks>
public sealed class GetDeliveryReturnsQueryHandler(
    ICheckoutReader checkout,
    IProductPageTextReader texts,
    IHtmlSanitiser sanitiser)
    : IQueryHandler<GetDeliveryReturnsQuery, DeliveryReturnsDto>
{
    public async Task<DeliveryReturnsDto> HandleAsync(
        GetDeliveryReturnsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var districts = await checkout.ActiveDistrictsAsync(cancellationToken);

        var methodsByZone = new Dictionary<DeliveryZone, IReadOnlyList<CheckoutDeliveryMethod>>();
        foreach (var zone in districts.Select(district => district.DeliveryZone).Distinct().Order())
        {
            methodsByZone[zone] = await checkout.DeliveryMethodsForZoneAsync(zone, cancellationToken);
        }

        // The pickup locations are read only when a pickup method is on offer at all, which is the ordinary
        // state of a shop that has not added a store yet (PICKUP is seeded disabled, doc 08 §9).
        var offersPickup = methodsByZone.Values.Any(methods => methods.Any(method => !method.RequiresAddress));
        var locations = offersPickup
            ? await checkout.ActivePickupLocationsAsync(cancellationToken)
            : [];

        var stored = await texts.ReadAsync(cancellationToken);

        return DeliveryReturnsComposer.Compose(
            districts,
            methodsByZone,
            locations,
            Clean(stored.DeliveryInfoHtml),
            Clean(stored.ReturnsInfoHtml));
    }

    /// <summary>
    /// Sanitised on the way out as well as on save: a row written by a script or a restore never came through the
    /// save handler, and this is a public page. A value the sanitiser refuses to parse is shown as nothing.
    /// </summary>
    private string Clean(string? html)
    {
        var cleaned = sanitiser.SanitiseSnippet(html);

        return cleaned.WasRejected ? string.Empty : cleaned.Html;
    }
}
