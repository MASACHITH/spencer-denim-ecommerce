using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Content.Contracts;

/// <summary>
/// The product page's delivery and returns panel (PDP-05, issue #33), as <c>GET /api/content/delivery-returns</c>
/// sends it.
/// <para>
/// <b>The same answer for every visitor, and that is a requirement rather than a convenience.</b> The panel is
/// server-rendered into every product page, so it carries no district, no cart and no account: every zone's
/// fee and estimate is listed, and the shopper's own figure is the one checkout shows once they choose a
/// district. An answer that varied by visitor could not be cached in front of the server and would put one
/// shopper's guess into another's HTML.
/// </para>
/// <para>
/// <b>Every number comes from <c>sales.DeliveryMethodZoneRates</c></b>, through the same reader checkout prices
/// from, so the panel cannot promise a fee or a day that checkout then contradicts. A method is listed only
/// where checkout would offer it: enabled, and with an available rate for the zone.
/// </para>
/// </summary>
/// <param name="Estimate">
/// The fewest and most working days any listed delivery method quotes for any zone, for the panel's one-line
/// summary; <see langword="null"/> when nothing is offered.
/// </param>
/// <param name="Zones">The delivery zones that hold at least one active district, each with a shopper's label.</param>
/// <param name="Methods">The address delivery methods on offer, in the shop's order, each with its rate per zone.</param>
/// <param name="PickupPoints">
/// Where an order can be collected — <b>empty unless the pickup method is enabled and an active pickup location
/// exists</b> with an available rate for its district's zone (doc 08 §3.5 R7).
/// </param>
/// <param name="DeliveryInfoHtml">
/// <c>Content.PdpDeliveryInfoHtml</c>, sanitised on the way out as well as on save; empty when there is none.
/// </param>
/// <param name="ReturnsInfoHtml"><c>Content.PdpReturnsInfoHtml</c>, likewise.</param>
public sealed record DeliveryReturnsDto(
    DeliveryEstimateDto? Estimate,
    IReadOnlyList<DeliveryZoneDto> Zones,
    IReadOnlyList<DeliveryMethodInfoDto> Methods,
    IReadOnlyList<PickupPointDto> PickupPoints,
    string DeliveryInfoHtml,
    string ReturnsInfoHtml);

/// <summary>A range of working days.</summary>
public sealed record DeliveryEstimateDto(int DaysMin, int DaysMax);

/// <summary>One delivery zone, labelled by the districts in it.</summary>
/// <param name="Zone">The zone.</param>
/// <param name="Label">
/// What a shopper reads: the districts' own names when there are three or fewer ("Gampaha and Kalutara"),
/// otherwise "Rest of Sri Lanka". Built from <c>ref.Districts</c>, so moving a district between zones relabels
/// the panel with no code change.
/// </param>
/// <param name="Districts">
/// The active districts in the zone, alphabetically. Not drawn; the storefront uses it after hydration to mark the
/// zone of the district a shopper picked in the header, so the server-rendered HTML stays the same for everyone.
/// </param>
public sealed record DeliveryZoneDto(DeliveryZone Zone, string Label, IReadOnlyList<string> Districts);

/// <summary>One address delivery method and what it costs in each zone that offers it.</summary>
public sealed record DeliveryMethodInfoDto(
    string Code,
    string Name,
    string? Description,
    IReadOnlyList<DeliveryRateDto> Rates);

/// <summary>A method's rate in one zone.</summary>
/// <param name="Zone">The zone.</param>
/// <param name="Fee">The delivery fee, in rupees.</param>
/// <param name="FreeAboveAmount">The order value from which delivery is free, or <see langword="null"/> if never.</param>
/// <param name="DaysMin">The fewest working days quoted.</param>
/// <param name="DaysMax">The most working days quoted.</param>
public sealed record DeliveryRateDto(
    DeliveryZone Zone,
    decimal Fee,
    decimal? FreeAboveAmount,
    int DaysMin,
    int DaysMax);

/// <summary>One place an order can be collected from.</summary>
public sealed record PickupPointDto(
    string Name,
    string City,
    string? OpeningHours,
    decimal Fee,
    int DaysMin,
    int DaysMax);
