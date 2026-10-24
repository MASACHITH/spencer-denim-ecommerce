using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Application.Sales.Checkout;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Delivery;
using SpencerDenim.Application.Sales.Delivery.Admin;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// Checkout's overseas branch (owner decision 2026-10-21) at the level of its pure parts: the validator's
/// country handling, the free-delivery promo rule, the CountryNotServed blocker, the country list DTO, the admin
/// fee bounds and the PayPal address-refusal classifier.
/// </summary>
public sealed class OverseasCheckoutRulesTests
{
    private static readonly OverseasQuote Served = new(OverseasRefusal.None, 9_500m, 7, 14);
    private static readonly OverseasQuote ExpressNotPriced = new(OverseasRefusal.ExpressNotPriced, 0m, 0, 0);

    private static CheckoutCountry Gb => new("GB", "United Kingdom", false, true, false, Served, ExpressNotPriced);

    private static PlaceOrderCommand SriLankan => new(
        CartLookup.ForGuest(null),
        Guid.NewGuid(),
        "Nimali",
        "Perera",
        "nimali@example.com",
        "0771234567",
        1,
        1,
        null,
        new CheckoutAddressInput("Nimali Perera", "0771234567", "14 Galle Road", null, "Colombo", "00300", null),
        "PayPal",
        5_890m,
        null,
        null);

    private static PlaceOrderCommand British => SriLankan with
    {
        DistrictId = null,
        CountryCode = "GB",
        Address = new CheckoutAddressInput(
            "Amelia Hart",
            "+44 20 7946 0958",
            "10 Downing Street",
            null,
            "London",
            "SW1A 2AA",
            null,
            Region: null),
    };

    [Fact]
    public void An_overseas_order_needs_no_district()
    {
        Assert.Empty(PlaceOrderValidator.Validate(British, requiresAddress: true, Gb));
    }

    [Fact]
    public void A_Sri_Lankan_order_still_needs_its_district()
    {
        var errors = PlaceOrderValidator.Validate(SriLankan with { DistrictId = null }, requiresAddress: true, DestinationRules.SriLanka);

        Assert.Equal("districtId", Assert.Single(errors).Field);
    }

    [Fact]
    public void The_two_argument_validator_is_the_Sri_Lankan_one()
    {
        Assert.Empty(PlaceOrderValidator.Validate(SriLankan, requiresAddress: true));
        Assert.Equal(
            "districtId",
            Assert.Single(PlaceOrderValidator.Validate(SriLankan with { DistrictId = null }, requiresAddress: true)).Field);
    }

    [Fact]
    public void An_unknown_country_is_one_error_of_its_own()
    {
        var errors = PlaceOrderValidator.Validate(British with { CountryCode = "ZZ" }, requiresAddress: true, country: null);

        Assert.Contains(errors, error => error.Field == "countryCode");
    }

    [Fact]
    public void An_overseas_address_is_held_to_its_countrys_rules()
    {
        var errors = PlaceOrderValidator.Validate(
            British with
            {
                Address = British.Address! with { PostalCode = null, Phone = "020 7946 0958" },
            },
            requiresAddress: true,
            Gb);

        Assert.Equal(["address.postalCode", "address.phone"], errors.Select(error => error.Field));
    }

    [Fact]
    public void A_free_delivery_code_does_not_waive_an_overseas_fee_but_keeps_its_money_off()
    {
        var promo = new PromoCharge(250m, FreeDelivery: true);

        var overseas = CheckoutQuote.ForDestination(promo, isDomestic: false);
        var domestic = CheckoutQuote.ForDestination(promo, isDomestic: true);

        Assert.NotNull(overseas);
        Assert.False(overseas.FreeDelivery);
        Assert.Equal(250m, overseas.Amount);
        Assert.Same(promo, domestic);
        Assert.Null(CheckoutQuote.ForDestination(null, isDomestic: false));
    }

    [Fact]
    public void A_money_off_code_is_the_same_object_overseas()
    {
        var promo = new PromoCharge(250m);

        Assert.Same(promo, CheckoutQuote.ForDestination(promo, isDomestic: false));
    }

    [Fact]
    public void An_unserved_overseas_country_is_named_as_such_not_as_no_delivery_method()
    {
        var refused = new CheckoutCountry("AQ", "Antarctica", false, false, false, new OverseasQuote(OverseasRefusal.CountryDisabled, 0m, 0, 0), ExpressNotPriced);

        var blockers = GetCheckoutQueryHandler.Blockers([], null, [], [], refused);

        Assert.Contains(CheckoutBlocker.CountryNotServed, blockers);
        Assert.DoesNotContain(CheckoutBlocker.NoDeliveryMethod, blockers);
    }

    [Fact]
    public void A_served_overseas_country_with_its_option_is_not_blocked_for_delivery()
    {
        var option = new CheckoutDeliveryMethod(9, DeliveryMethod.InternationalCode, "International delivery", null, true, Gb.OverseasRateFor(null));

        var blockers = GetCheckoutQueryHandler.Blockers([], null, [option], [], Gb);

        Assert.DoesNotContain(CheckoutBlocker.CountryNotServed, blockers);
    }

    [Fact]
    public void A_served_country_with_International_delivery_switched_off_is_still_not_served()
    {
        var blockers = GetCheckoutQueryHandler.Blockers([], null, [], [], Gb);

        Assert.Contains(CheckoutBlocker.CountryNotServed, blockers);
    }

    [Fact]
    public void No_payment_method_is_its_own_blocker()
    {
        Assert.Contains(CheckoutBlocker.NoPaymentMethod, GetCheckoutQueryHandler.Blockers([], null, [], [], Gb));
        Assert.DoesNotContain(
            CheckoutBlocker.NoPaymentMethod,
            GetCheckoutQueryHandler.Blockers([], null, [], [new CheckoutPaymentMethod("PayPal", "PayPal", null)], Gb));
    }

    [Fact]
    public void The_country_list_shows_a_fee_only_where_one_can_be_charged()
    {
        var gb = GetDeliveryCountriesQueryHandler.ToDto(Gb);
        var lk = GetDeliveryCountriesQueryHandler.ToDto(DestinationRules.SriLanka);
        var aq = GetDeliveryCountriesQueryHandler.ToDto(
            new CheckoutCountry("AQ", "Antarctica", false, false, false, new OverseasQuote(OverseasRefusal.CountryDisabled, 0m, 0, 0), ExpressNotPriced));

        Assert.Equal((true, 9_500m, (byte?)7, (byte?)14), (gb.IsAvailable, gb.Fee!.Value, gb.EstimatedDaysMin, gb.EstimatedDaysMax));
        Assert.True(lk.IsAvailable);
        Assert.True(lk.IsDomestic);
        Assert.Null(lk.Fee);
        Assert.False(aq.IsAvailable);
        Assert.Null(aq.Fee);
        Assert.Null(aq.EstimatedDaysMin);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(9500.5, true)]
    [InlineData(1000000, true)]
    [InlineData(1000000.01, false)]
    [InlineData(-0.01, false)]
    [InlineData(10.005, false)]
    public void An_admin_fee_is_between_zero_and_a_million_to_the_cent(double fee, bool valid)
    {
        var errors = new List<SpencerDenim.Application.Abstractions.Results.ValidationError>();
        OverseasDeliveryRules.CheckFee(errors, "fee", (decimal)fee);

        Assert.Equal(valid, errors.Count == 0);
    }

    [Fact]
    public void An_unpriced_region_is_allowed()
    {
        var errors = new List<SpencerDenim.Application.Abstractions.Results.ValidationError>();
        OverseasDeliveryRules.CheckFee(errors, "fee", null);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(1, 90, true, true)]
    [InlineData(5, 5, true, true)]
    [InlineData(0, 5, true, false)]
    [InlineData(5, 91, true, false)]
    [InlineData(6, 5, true, false)]
    [InlineData(5, null, false, false)]
    [InlineData(null, 5, false, false)]
    [InlineData(null, null, false, true)]
    [InlineData(null, null, true, false)]
    public void An_admin_estimate_is_both_days_or_neither_in_order_within_bounds(int? min, int? max, bool required, bool valid)
    {
        var errors = new List<SpencerDenim.Application.Abstractions.Results.ValidationError>();
        OverseasDeliveryRules.CheckDays(errors, "days", min, max, required);

        Assert.Equal(valid, errors.Count == 0);
    }

    [Theory]
    [InlineData("POSTAL_CODE_REQUIRED", true)]
    [InlineData("CITY_REQUIRED", true)]
    [InlineData("INVALID_COUNTRY_CODE", true)]
    [InlineData("SHIPPING_ADDRESS_INVALID", true)]
    [InlineData("STATE_REQUIRED", true)]
    [InlineData("INVALID_PARAMETER_SYNTAX", true)]
    [InlineData("INVALID_STRING_LENGTH", true)]
    [InlineData("INSTRUMENT_DECLINED", false)]
    [InlineData("PAYEE_ACCOUNT_RESTRICTED", false)]
    [InlineData("DUPLICATE_INVOICE_ID", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_an_address_refusal_earns_the_retry_without_the_address(string? issue, bool expected) =>
        Assert.Equal(expected, PayPalAddressRefusal.IsAddressIssue(issue));
}
