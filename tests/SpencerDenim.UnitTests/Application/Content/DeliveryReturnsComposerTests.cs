using SpencerDenim.Application.Content.DeliveryReturns;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Application.Content;

/// <summary>
/// The product page's delivery panel (issue #33): every figure from the zone rates, a method only where checkout
/// would offer it, pickup only when an enabled pickup method has an active location with an available rate, the
/// zone labels drawn from the districts, and the summary range.
/// </summary>
public sealed class DeliveryReturnsComposerTests
{
    private static readonly CheckoutDistrict[] SeededDistricts =
    [
        new(1, "Colombo", DeliveryZone.Colombo),
        new(2, "Gampaha", DeliveryZone.Western),
        new(3, "Kalutara", DeliveryZone.Western),
        new(4, "Kandy", DeliveryZone.IslandWide),
        new(5, "Matale", DeliveryZone.IslandWide),
        new(6, "Galle", DeliveryZone.IslandWide),
        new(7, "Jaffna", DeliveryZone.IslandWide),
    ];

    [Fact]
    public void The_methods_and_fees_are_the_zone_rates_checkout_reads()
    {
        var panel = Compose(SeededRates());

        Assert.Equal(["STANDARD", "EXPRESS"], panel.Methods.Select(method => method.Code));

        var standard = panel.Methods[0];
        Assert.Equal("Standard delivery", standard.Name);
        Assert.Equal([DeliveryZone.Colombo, DeliveryZone.Western, DeliveryZone.IslandWide], standard.Rates.Select(rate => rate.Zone));
        Assert.Equal([400m, 450m, 600m], standard.Rates.Select(rate => rate.Fee));
        Assert.Equal([10_000m, 10_000m, 15_000m], standard.Rates.Select(rate => rate.FreeAboveAmount));
        Assert.Equal([(1, 2), (2, 3), (3, 5)], standard.Rates.Select(rate => (rate.DaysMin, rate.DaysMax)));

        // Express is unavailable island-wide: not listed there, and no free threshold where it has none.
        var express = panel.Methods[1];
        Assert.Equal([DeliveryZone.Colombo, DeliveryZone.Western], express.Rates.Select(rate => rate.Zone));
        Assert.All(express.Rates, rate => Assert.Null(rate.FreeAboveAmount));
    }

    [Fact]
    public void A_fee_changed_in_the_rate_table_is_the_fee_the_panel_shows()
    {
        var rates = SeededRates();
        rates[DeliveryZone.Colombo] = [Standard(new CheckoutZoneRate(true, 375m, 8_000m, 1, 1)), Express(null), Pickup(null)];

        var standard = Compose(rates).Methods[0];

        Assert.Equal(375m, standard.Rates[0].Fee);
        Assert.Equal(8_000m, standard.Rates[0].FreeAboveAmount);
        Assert.Equal((1, 1), (standard.Rates[0].DaysMin, standard.Rates[0].DaysMax));
    }

    [Fact]
    public void A_method_with_no_available_rate_anywhere_is_not_listed()
    {
        var rates = new Dictionary<DeliveryZone, IReadOnlyList<CheckoutDeliveryMethod>>
        {
            [DeliveryZone.Colombo] = [Standard(new CheckoutZoneRate(true, 400m, null, 1, 2)), Express(new CheckoutZoneRate(false, 950m, null, 1, 1))],
            [DeliveryZone.Western] = [Standard(new CheckoutZoneRate(true, 450m, null, 2, 3)), Express(null)],
            [DeliveryZone.IslandWide] = [Standard(new CheckoutZoneRate(true, 600m, null, 3, 5)), Express(null)],
        };

        var panel = Compose(rates);

        Assert.Equal(["STANDARD"], panel.Methods.Select(method => method.Code));
    }

    [Fact]
    public void A_zone_with_no_active_district_is_neither_labelled_nor_priced()
    {
        var districts = SeededDistricts.Where(district => district.DeliveryZone != DeliveryZone.Western).ToList();
        var rates = SeededRates();

        var panel = DeliveryReturnsComposer.Compose(districts, rates, [], "", "");

        Assert.Equal([DeliveryZone.Colombo, DeliveryZone.IslandWide], panel.Zones.Select(zone => zone.Zone));
        Assert.DoesNotContain(panel.Methods.SelectMany(method => method.Rates), rate => rate.Zone == DeliveryZone.Western);
    }

    [Fact]
    public void Zones_are_labelled_by_their_districts_and_the_large_one_as_the_rest_of_the_island()
    {
        var panel = Compose(SeededRates());

        Assert.Equal(
            ["Colombo", "Gampaha and Kalutara", DeliveryReturnsComposer.RestOfIslandLabel],
            panel.Zones.Select(zone => zone.Label));
    }

    [Theory]
    [InlineData(new[] { "Colombo" }, true, "Colombo")]
    [InlineData(new[] { "Gampaha", "Kalutara" }, true, "Gampaha and Kalutara")]
    [InlineData(new[] { "Galle", "Matara", "Hambantota" }, true, "Galle, Matara and Hambantota")]
    [InlineData(new[] { "A", "B", "C", "D" }, true, "Rest of Sri Lanka")]
    [InlineData(new[] { "A", "B", "C", "D" }, false, "All of Sri Lanka")]
    [InlineData(new string[0], true, "Rest of Sri Lanka")]
    [InlineData(new string[0], false, "All of Sri Lanka")]
    public void Label(string[] names, bool otherZones, string expected)
    {
        Assert.Equal(expected, DeliveryReturnsComposer.Label(names, otherZones));
    }

    /// <summary>
    /// Tester (#33): a shop whose only zone holds every district is "All of Sri Lanka", not "the rest" of nothing.
    /// Kills the mutant that reads <c>otherZones</c> as true when the zone is alone.
    /// </summary>
    [Fact]
    public void The_only_zone_with_districts_is_labelled_as_the_whole_island()
    {
        var zones = DeliveryReturnsComposer.Zones(
        [
            new(4, "Kandy", DeliveryZone.IslandWide),
            new(5, "Matale", DeliveryZone.IslandWide),
            new(6, "Galle", DeliveryZone.IslandWide),
            new(7, "Jaffna", DeliveryZone.IslandWide),
        ]);

        Assert.Equal(DeliveryReturnsComposer.WholeIslandLabel, Assert.Single(zones).Label);
    }

    private static readonly string[] WesternNames = ["Gampaha", "Kalutara", "Kegalle", "Kurunegala"];

    private static readonly string[] OuterNames = ["Ampara", "Badulla", "Galle", "Jaffna", "Kandy"];

    [Fact]
    public void Two_zones_too_large_to_name_get_two_different_labels_and_carry_their_districts()
    {
        var districts = new List<CheckoutDistrict>
        {
            new(1, "Colombo", DeliveryZone.Colombo),
        };
        districts.AddRange(WesternNames.Select((name, i) => new CheckoutDistrict(10 + i, name, DeliveryZone.Western)));
        districts.AddRange(OuterNames.Select((name, i) => new CheckoutDistrict(20 + i, name, DeliveryZone.IslandWide)));

        var zones = DeliveryReturnsComposer.Zones(districts);

        Assert.Equal(
            ["Colombo", "Gampaha, Kalutara and 2 other districts", "Ampara, Badulla and 3 other districts"],
            zones.Select(zone => zone.Label));
        Assert.Equal(zones.Count, zones.Select(zone => zone.Label).Distinct().Count());
        Assert.Equal(["Gampaha", "Kalutara", "Kegalle", "Kurunegala"], zones[1].Districts);
    }

    [Fact]
    public void Districts_are_named_in_alphabetical_order_whatever_order_they_arrive_in()
    {
        var zones = DeliveryReturnsComposer.Zones(
        [
            new(3, "Kalutara", DeliveryZone.Western),
            new(2, "Gampaha", DeliveryZone.Western),
        ]);

        Assert.Equal("Gampaha and Kalutara", Assert.Single(zones).Label);
    }

    [Fact]
    public void The_estimate_is_the_fewest_and_most_days_of_the_delivery_methods_and_ignores_pickup()
    {
        var rates = SeededRates();
        rates[DeliveryZone.Colombo] =
        [
            Standard(new CheckoutZoneRate(true, 400m, null, 1, 2)),
            Express(new CheckoutZoneRate(true, 950m, null, 1, 1)),
            Pickup(new CheckoutZoneRate(true, 0m, null, 0, 9)),
        ];

        var panel = DeliveryReturnsComposer.Compose(SeededDistricts, rates, [Location(DeliveryZone.Colombo)], "", "");

        Assert.Equal((1, 5), (panel.Estimate!.DaysMin, panel.Estimate.DaysMax));
    }

    /// <summary>
    /// Tester (#33): the lower bound is the smallest <i>minimum</i>, not the smallest maximum. Every seeded rate
    /// has a range here, so the two differ. Kills the mutant <c>Min(rate => rate.DaysMax)</c>.
    /// </summary>
    [Fact]
    public void The_estimate_lower_bound_is_the_smallest_minimum_when_no_rate_is_a_single_day()
    {
        var rates = new Dictionary<DeliveryZone, IReadOnlyList<CheckoutDeliveryMethod>>
        {
            [DeliveryZone.Colombo] = [Standard(new CheckoutZoneRate(true, 400m, null, 2, 4))],
            [DeliveryZone.Western] = [Standard(new CheckoutZoneRate(true, 450m, null, 3, 6))],
        };

        var panel = DeliveryReturnsComposer.Compose(
            [new(1, "Colombo", DeliveryZone.Colombo), new(2, "Gampaha", DeliveryZone.Western)],
            rates,
            [],
            "",
            "");

        Assert.Equal((2, 6), (panel.Estimate!.DaysMin, panel.Estimate.DaysMax));
    }

    [Fact]
    public void Nothing_on_offer_has_no_estimate_and_no_methods()
    {
        var rates = new Dictionary<DeliveryZone, IReadOnlyList<CheckoutDeliveryMethod>>
        {
            [DeliveryZone.Colombo] = [Standard(null)],
        };

        var panel = DeliveryReturnsComposer.Compose([new(1, "Colombo", DeliveryZone.Colombo)], rates, [], "", "");

        Assert.Null(panel.Estimate);
        Assert.Empty(panel.Methods);
    }

    // ---- pickup ---------------------------------------------------------------------------------------

    [Fact]
    public void With_no_pickup_location_there_is_no_pickup_even_when_the_method_is_enabled()
    {
        var rates = SeededRates(pickupEnabled: true);

        Assert.Empty(DeliveryReturnsComposer.Compose(SeededDistricts, rates, [], "", "").PickupPoints);
    }

    [Fact]
    public void With_a_location_but_the_pickup_method_disabled_there_is_no_pickup()
    {
        // Disabled means checkout's reader does not return the method at all.
        var rates = SeededRates(pickupEnabled: false);

        Assert.Empty(DeliveryReturnsComposer.Compose(SeededDistricts, rates, [Location(DeliveryZone.Colombo)], "", "").PickupPoints);
    }

    [Fact]
    public void A_location_whose_zone_has_no_available_pickup_rate_is_not_offered()
    {
        var rates = SeededRates(pickupEnabled: true);
        rates[DeliveryZone.Western] = [Standard(new CheckoutZoneRate(true, 450m, null, 2, 3)), Pickup(new CheckoutZoneRate(false, 0m, null, 1, 2))];

        var panel = DeliveryReturnsComposer.Compose(
            SeededDistricts,
            rates,
            [Location(DeliveryZone.Western, "Negombo store"), Location(DeliveryZone.Colombo, "Colombo 3 store")],
            "",
            "");

        var point = Assert.Single(panel.PickupPoints);
        Assert.Equal("Colombo 3 store", point.Name);
    }

    [Fact]
    public void An_enabled_pickup_with_a_location_is_offered_at_the_rate_for_the_locations_zone()
    {
        var rates = SeededRates(pickupEnabled: true);
        rates[DeliveryZone.IslandWide] = [Standard(new CheckoutZoneRate(true, 600m, null, 3, 5)), Pickup(new CheckoutZoneRate(true, 150m, null, 2, 4))];

        var panel = DeliveryReturnsComposer.Compose(
            SeededDistricts,
            rates,
            [Location(DeliveryZone.IslandWide, "Kandy store", "  Daily 10–6  ")],
            "",
            "");

        var point = Assert.Single(panel.PickupPoints);
        Assert.Equal(("Kandy store", "Kandy", "Daily 10–6", 150m, 2, 4), (point.Name, point.City, point.OpeningHours, point.Fee, point.DaysMin, point.DaysMax));
        Assert.DoesNotContain(panel.Methods, method => method.Code == DeliveryMethod.PickupCode);
    }

    [Fact]
    public void Blank_opening_hours_are_null_rather_than_an_empty_line()
    {
        var panel = DeliveryReturnsComposer.Compose(
            SeededDistricts,
            SeededRates(pickupEnabled: true),
            [Location(DeliveryZone.Colombo, hours: "   ")],
            "",
            "");

        Assert.Null(Assert.Single(panel.PickupPoints).OpeningHours);
    }

    [Fact]
    public void A_location_in_a_zone_checkout_did_not_price_is_skipped()
    {
        var rates = new Dictionary<DeliveryZone, IReadOnlyList<CheckoutDeliveryMethod>>
        {
            [DeliveryZone.Colombo] = [Pickup(new CheckoutZoneRate(true, 0m, null, 1, 2))],
        };

        var panel = DeliveryReturnsComposer.Compose(SeededDistricts, rates, [Location(DeliveryZone.Western)], "", "");

        Assert.Empty(panel.PickupPoints);
    }

    [Fact]
    public void The_texts_are_passed_through_and_null_becomes_empty()
    {
        var panel = DeliveryReturnsComposer.Compose(SeededDistricts, SeededRates(), [], "<p>D</p>", null!);

        Assert.Equal("<p>D</p>", panel.DeliveryInfoHtml);
        Assert.Equal(string.Empty, panel.ReturnsInfoHtml);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static SpencerDenim.Application.Content.Contracts.DeliveryReturnsDto Compose(
        Dictionary<DeliveryZone, IReadOnlyList<CheckoutDeliveryMethod>> rates) =>
        DeliveryReturnsComposer.Compose(SeededDistricts, rates, [], "", "");

    /// <summary>Doc 08 §9's seeded placeholders, as checkout's reader returns them per zone.</summary>
    private static Dictionary<DeliveryZone, IReadOnlyList<CheckoutDeliveryMethod>> SeededRates(bool pickupEnabled = false)
    {
        CheckoutDeliveryMethod[] Zone(CheckoutZoneRate standard, CheckoutZoneRate? express) =>
            pickupEnabled
                ? [Standard(standard), Express(express), Pickup(new CheckoutZoneRate(true, 0m, null, 1, 2))]
                : [Standard(standard), Express(express)];

        return new Dictionary<DeliveryZone, IReadOnlyList<CheckoutDeliveryMethod>>
        {
            [DeliveryZone.Colombo] = Zone(new(true, 400m, 10_000m, 1, 2), new(true, 950m, null, 1, 1)),
            [DeliveryZone.Western] = Zone(new(true, 450m, 10_000m, 2, 3), new(true, 1_200m, null, 1, 2)),
            [DeliveryZone.IslandWide] = Zone(new(true, 600m, 15_000m, 3, 5), new(false, 0m, null, 0, 0)),
        };
    }

    private static CheckoutDeliveryMethod Standard(CheckoutZoneRate? rate) =>
        new(1, DeliveryMethod.StandardCode, "Standard delivery", "To your door.", true, rate);

    private static CheckoutDeliveryMethod Express(CheckoutZoneRate? rate) =>
        new(2, DeliveryMethod.ExpressCode, "Express delivery", null, true, rate);

    private static CheckoutDeliveryMethod Pickup(CheckoutZoneRate? rate) =>
        new(3, DeliveryMethod.PickupCode, "Store pickup", null, false, rate);

    private static CheckoutPickupLocation Location(DeliveryZone zone, string name = "Store", string? hours = null) =>
        new(1, name, "1 Main Street", zone == DeliveryZone.IslandWide ? "Kandy" : "Colombo", 1, "District", zone, null, hours);
}
