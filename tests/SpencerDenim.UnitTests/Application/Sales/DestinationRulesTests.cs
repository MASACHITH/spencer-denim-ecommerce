using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Sales.Checkout;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// The per-country address rules checkout and the address book share (owner decision 2026-10-21). A Sri Lankan
/// address must be untouched by them; an overseas one adds a state where the country always has one, a postcode
/// where the country always has one, and a phone written with its country code.
/// </summary>
public sealed class DestinationRulesTests
{
    private static readonly OverseasQuote Served = new(OverseasRefusal.None, 9_500m, 7, 14);

    private static CheckoutCountry Gb => new("GB", "United Kingdom", false, PostalCodeRequired: true, RegionRequired: false, Served);

    private static CheckoutCountry Us => new("US", "United States", false, PostalCodeRequired: true, RegionRequired: true, Served);

    private static CheckoutCountry Ae => new("AE", "United Arab Emirates", false, PostalCodeRequired: false, RegionRequired: false, Served);

    private static CheckoutDeliveryMethod Method(string code, bool requiresAddress = true) =>
        new(1, code, code, null, requiresAddress, null);

    [Theory]
    [InlineData(null, "LK")]
    [InlineData("", "LK")]
    [InlineData("   ", "LK")]
    [InlineData("gb", "GB")]
    [InlineData(" Us ", "US")]
    [InlineData("LK", "LK")]
    public void A_country_code_is_trimmed_and_upper_cased_and_none_means_Sri_Lanka(string? raw, string expected) =>
        Assert.Equal(expected, DestinationRules.NormaliseCountryCode(raw));

    [Theory]
    [InlineData("GBR")]
    [InlineData("G")]
    [InlineData("1A")]
    [InlineData("G-")]
    [InlineData("ÅL")]
    public void Anything_but_two_ASCII_letters_is_not_a_country_code(string raw) =>
        Assert.Null(DestinationRules.NormaliseCountryCode(raw));

    [Fact]
    public void Sri_Lanka_never_travels_by_International_delivery()
    {
        var error = DestinationRules.MethodMismatch(DestinationRules.SriLanka, Method(DeliveryMethod.InternationalCode));

        Assert.NotNull(error);
        Assert.Equal("deliveryMethodId", error.Field);
    }

    [Theory]
    [InlineData(DeliveryMethod.StandardCode)]
    [InlineData(DeliveryMethod.ExpressCode)]
    public void An_overseas_country_never_travels_by_a_Sri_Lankan_method(string code)
    {
        var error = DestinationRules.MethodMismatch(Gb, Method(code));

        Assert.NotNull(error);
        Assert.Contains("International delivery", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_pickup_is_Sri_Lanka_only()
    {
        var error = DestinationRules.MethodMismatch(Gb, Method(DeliveryMethod.PickupCode, requiresAddress: false));

        Assert.NotNull(error);
        Assert.Equal("Store pickup is only available in Sri Lanka.", error.Message);
    }

    [Fact]
    public void Matching_methods_pass()
    {
        Assert.Null(DestinationRules.MethodMismatch(Gb, Method(DeliveryMethod.InternationalCode)));
        Assert.Null(DestinationRules.MethodMismatch(DestinationRules.SriLanka, Method(DeliveryMethod.StandardCode)));
        Assert.Null(DestinationRules.MethodMismatch(
            DestinationRules.SriLanka,
            Method(DeliveryMethod.PickupCode, requiresAddress: false)));
    }

    [Fact]
    public void A_Sri_Lankan_address_gains_no_rule_at_all()
    {
        var errors = Overseas(DestinationRules.SriLanka, region: null, postalCode: "not a code!", phone: "0771234567");

        Assert.Empty(errors);
    }

    [Fact]
    public void The_United_States_needs_a_state_and_a_postcode()
    {
        var errors = Overseas(Us, region: null, postalCode: null, phone: "+1 212 555 0100");

        Assert.Equal(["address.region", "address.postalCode"], errors.Select(error => error.Field));
    }

    [Fact]
    public void The_United_Kingdom_needs_a_postcode_and_no_state()
    {
        Assert.Empty(Overseas(Gb, region: null, postalCode: "SW1A 1AA", phone: "+44 20 7946 0958"));

        var missing = Overseas(Gb, region: null, postalCode: "  ", phone: "+44 20 7946 0958");
        Assert.Equal("address.postalCode", Assert.Single(missing).Field);
        Assert.Contains("United Kingdom", missing[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Emirates_need_neither_a_postcode_nor_a_state()
    {
        Assert.Empty(Overseas(Ae, region: null, postalCode: null, phone: "+971 4 123 4567"));
    }

    [Theory]
    [InlineData("12345-6789")]
    [InlineData("SW1A 1AA")]
    [InlineData("K1A 0B1")]
    [InlineData("1")]
    public void A_postcode_of_letters_digits_spaces_and_hyphens_passes(string postalCode) =>
        Assert.Empty(Overseas(Ae, region: null, postalCode, phone: "+971 4 123 4567"));

    [Theory]
    [InlineData("-1234")]
    [InlineData("1234-")]
    [InlineData("12#45")]
    [InlineData("<b>1</b>")]
    public void A_postcode_with_anything_else_is_refused(string postalCode) =>
        Assert.Equal("address.postalCode", Assert.Single(Overseas(Ae, region: null, postalCode, phone: "+9714123456")).Field);

    [Fact]
    public void A_postcode_longer_than_its_column_is_refused()
    {
        var error = Assert.Single(Overseas(Ae, region: null, postalCode: "12345678901", phone: "+9714123456"));

        Assert.Equal("address.postalCode", error.Field);
        Assert.Contains("10", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_state_longer_than_its_column_is_refused()
    {
        var error = Assert.Single(Overseas(Ae, region: new string('x', 81), postalCode: null, phone: "+9714123456"));

        Assert.Equal("address.region", error.Field);
    }

    [Theory]
    [InlineData("+44 20 7946 0958")]
    [InlineData("0044 20 7946 0958")]
    [InlineData("  +971 4 123 4567")]
    public void An_overseas_phone_with_its_country_code_passes(string phone) =>
        Assert.Empty(Overseas(Ae, region: null, postalCode: null, phone));

    [Theory]
    [InlineData("020 7946 0958")]
    [InlineData("0771234567")]
    public void An_overseas_phone_without_its_country_code_is_refused(string phone)
    {
        var error = Assert.Single(Overseas(Ae, region: null, postalCode: null, phone));

        Assert.Equal("address.phone", error.Field);
    }

    [Fact]
    public void The_address_book_uses_the_same_rules_with_no_prefix()
    {
        var errors = new List<ValidationError>();
        DestinationRules.ValidateOverseasAddress(errors, string.Empty, Us, null, null, "212 555 0100");

        Assert.Equal(["region", "postalCode", "phone"], errors.Select(error => error.Field));
    }

    private static List<ValidationError> Overseas(CheckoutCountry country, string? region, string? postalCode, string? phone)
    {
        var errors = new List<ValidationError>();
        DestinationRules.ValidateOverseasAddress(errors, "address.", country, region, postalCode, phone);
        return errors;
    }
}
