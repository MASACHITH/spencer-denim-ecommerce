using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Content.DeliveryReturns;

/// <summary>
/// Turns what checkout's reader returns into the product page's panel (issue #33). Pure, so every rule below is
/// a unit test rather than a database fixture.
/// </summary>
public static class DeliveryReturnsComposer
{
    /// <summary>A zone with more districts than this is labelled as the rest of the island, not by name.</summary>
    public const int MaxNamedDistricts = 3;

    /// <summary>The label of a zone too large to name district by district, when another zone exists.</summary>
    public const string RestOfIslandLabel = "Rest of Sri Lanka";

    /// <summary>The label of a zone too large to name, when it is the only zone with districts in it.</summary>
    public const string WholeIslandLabel = "All of Sri Lanka";

    /// <summary>Builds the panel.</summary>
    /// <param name="districts">Every active district, with its zone.</param>
    /// <param name="methodsByZone">
    /// What checkout's reader returns for each zone that holds an active district: the enabled methods, each with
    /// its rate for that zone or none.
    /// </param>
    /// <param name="pickupLocations">The active pickup locations in active districts.</param>
    /// <param name="deliveryInfoHtml">The sanitised delivery text.</param>
    /// <param name="returnsInfoHtml">The sanitised returns text.</param>
    public static DeliveryReturnsDto Compose(
        IReadOnlyList<CheckoutDistrict> districts,
        IReadOnlyDictionary<DeliveryZone, IReadOnlyList<CheckoutDeliveryMethod>> methodsByZone,
        IReadOnlyList<CheckoutPickupLocation> pickupLocations,
        string deliveryInfoHtml,
        string returnsInfoHtml)
    {
        ArgumentNullException.ThrowIfNull(districts);
        ArgumentNullException.ThrowIfNull(methodsByZone);
        ArgumentNullException.ThrowIfNull(pickupLocations);

        var zones = Zones(districts);
        var zoneSet = zones.Select(zone => zone.Zone).ToHashSet();

        var methods = AddressMethods(methodsByZone, zoneSet);
        var pickup = PickupPoints(methodsByZone, pickupLocations);

        return new DeliveryReturnsDto(
            Estimate(methods),
            zones,
            methods,
            pickup,
            deliveryInfoHtml ?? string.Empty,
            returnsInfoHtml ?? string.Empty);
    }

    /// <summary>
    /// The zones that hold an active district, in zone order, each labelled by its districts.
    /// </summary>
    public static IReadOnlyList<DeliveryZoneDto> Zones(IReadOnlyList<CheckoutDistrict> districts)
    {
        ArgumentNullException.ThrowIfNull(districts);

        var groups = districts
            .GroupBy(district => district.DeliveryZone)
            .OrderBy(group => group.Key)
            .ToList();

        return groups
            .Select(group => new DeliveryZoneDto(
                group.Key,
                Label(
                    group.Select(district => district.Name).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                    otherZones: groups.Count > 1)))
            .ToList();
    }

    /// <summary>
    /// A zone's label: its districts by name when there are <see cref="MaxNamedDistricts"/> or fewer, otherwise
    /// the rest of the island (or the whole island, when no other zone has a district).
    /// </summary>
    public static string Label(IReadOnlyList<string> districtNames, bool otherZones)
    {
        ArgumentNullException.ThrowIfNull(districtNames);

        if (districtNames.Count > MaxNamedDistricts || districtNames.Count == 0)
        {
            return otherZones ? RestOfIslandLabel : WholeIslandLabel;
        }

        return districtNames.Count == 1
            ? districtNames[0]
            : string.Join(", ", districtNames.Take(districtNames.Count - 1)) + " and " + districtNames[^1];
    }

    /// <summary>
    /// The methods that deliver to an address, each with its available rate per zone, in checkout's order. A
    /// method with no available rate anywhere is left out — checkout would never offer it.
    /// </summary>
    private static List<DeliveryMethodInfoDto> AddressMethods(
        IReadOnlyDictionary<DeliveryZone, IReadOnlyList<CheckoutDeliveryMethod>> methodsByZone,
        HashSet<DeliveryZone> zones)
    {
        var order = new List<int>();
        var byId = new Dictionary<int, (CheckoutDeliveryMethod Method, List<DeliveryRateDto> Rates)>();

        foreach (var (zone, methods) in methodsByZone.OrderBy(pair => pair.Key))
        {
            if (!zones.Contains(zone))
            {
                continue;
            }

            foreach (var method in methods.Where(candidate => candidate.RequiresAddress))
            {
                if (!byId.TryGetValue(method.Id, out var entry))
                {
                    entry = (method, []);
                    byId[method.Id] = entry;
                    order.Add(method.Id);
                }

                if (method.Rate is { IsAvailable: true } rate)
                {
                    entry.Rates.Add(new DeliveryRateDto(
                        zone,
                        rate.Fee,
                        rate.FreeAboveAmount,
                        rate.EstimatedDaysMin,
                        rate.EstimatedDaysMax));
                }
            }
        }

        // Checkout's reader returns each zone's methods in SortOrder; a method first seen in a later zone still
        // sorts by where checkout puts it, which is the order of first appearance across zones in zone order.
        return order
            .Select(id => byId[id])
            .Where(entry => entry.Rates.Count > 0)
            .Select(entry => new DeliveryMethodInfoDto(
                entry.Method.Code,
                entry.Method.Name,
                entry.Method.Description,
                entry.Rates))
            .ToList();
    }

    /// <summary>
    /// Where an order can be collected: each active location, priced by the pickup method's rate for <b>the
    /// location's</b> zone (doc 08 §3.5 R7). Empty when no pickup method is enabled, when no location exists, or
    /// when no location's zone has an available pickup rate — so the panel never offers a collection checkout
    /// would refuse.
    /// </summary>
    private static List<PickupPointDto> PickupPoints(
        IReadOnlyDictionary<DeliveryZone, IReadOnlyList<CheckoutDeliveryMethod>> methodsByZone,
        IReadOnlyList<CheckoutPickupLocation> locations)
    {
        var points = new List<PickupPointDto>();

        foreach (var location in locations)
        {
            if (!methodsByZone.TryGetValue(location.DeliveryZone, out var methods))
            {
                continue;
            }

            var rate = methods
                .Where(method => !method.RequiresAddress)
                .Select(method => method.Rate)
                .FirstOrDefault(candidate => candidate is { IsAvailable: true });

            if (rate is not { } available)
            {
                continue;
            }

            points.Add(new PickupPointDto(
                location.Name,
                location.City,
                string.IsNullOrWhiteSpace(location.OpeningHours) ? null : location.OpeningHours.Trim(),
                available.Fee,
                available.EstimatedDaysMin,
                available.EstimatedDaysMax));
        }

        return points;
    }

    /// <summary>
    /// The fewest and the most working days any listed delivery method quotes, for the panel's summary line.
    /// Pickup is not delivery, so it is not in the range.
    /// </summary>
    private static DeliveryEstimateDto? Estimate(List<DeliveryMethodInfoDto> methods)
    {
        var rates = methods.SelectMany(method => method.Rates).ToList();

        return rates.Count == 0
            ? null
            : new DeliveryEstimateDto(rates.Min(rate => rate.DaysMin), rates.Max(rate => rate.DaysMax));
    }
}
