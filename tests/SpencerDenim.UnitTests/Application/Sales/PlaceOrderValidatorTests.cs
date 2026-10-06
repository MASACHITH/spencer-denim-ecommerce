using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Application.Sales.Checkout;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// Doc 11 P6 step 8's field validation, and issue #47's body shapes.
/// </summary>
/// <remarks>
/// <b>The shapes are the point of this file.</b> #47 is that a well-formed JSON object omitting a
/// required string binds <see langword="null"/> into a non-nullable reference property and the first
/// dereference returns 500. The two shapes it names — <c>{}</c> and <c>{"email":"a@b.c"}</c> — reach this
/// validator as a command whose strings are all null but one, and what has to happen is a list of field
/// errors rather than an exception. The endpoint's own test proves the HTTP status; this proves the use
/// case cannot throw on the way there.
/// </remarks>
public sealed class PlaceOrderValidatorTests
{
    private const int StandardDeliveryMethodId = 1;

    /// <summary>A command with nothing filled in: issue #47's <c>{}</c>.</summary>
    private static PlaceOrderCommand Empty => new(
        CartLookup.ForGuest(null),
        Guid.Empty,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);

    /// <summary>A complete, valid delivery checkout.</summary>
    private static PlaceOrderCommand Valid => Empty with
    {
        CheckoutKey = Guid.NewGuid(),
        FirstName = "Nimali",
        LastName = "Perera",
        Email = "nimali@example.com",
        Phone = "0771234567",
        DeliveryMethodId = StandardDeliveryMethodId,
        DistrictId = 1,
        PaymentMethodCode = "BankTransfer",
        ExpectedGrandTotal = 5_890m,
        Address = new CheckoutAddressInput(
            "Nimali Perera",
            "0771234567",
            "14 Galle Road",
            "Kollupitiya",
            "Colombo",
            "00300",
            "Opposite the temple"),
    };

    [Fact]
    public void An_empty_body_is_every_field_error_at_once_and_never_an_exception()
    {
        // Issue #47's `{}`. The assertion that matters is that this returns at all.
        var errors = PlaceOrderValidator.Validate(Empty, requiresAddress: true);

        var fields = errors.Select(error => error.Field).ToList();

        Assert.Contains("checkoutKey", fields);
        Assert.Contains("firstName", fields);
        Assert.Contains("email", fields);
        Assert.Contains("phone", fields);
        Assert.Contains("deliveryMethodId", fields);
        Assert.Contains("paymentMethodCode", fields);
        Assert.Contains("expectedGrandTotal", fields);
        Assert.Contains("districtId", fields);
        Assert.Contains("address", fields);
    }

    [Fact]
    public void A_body_with_only_an_email_is_every_other_field_error_and_never_an_exception()
    {
        // Issue #47's `{"email":"a@b.c"}`.
        var errors = PlaceOrderValidator.Validate(Empty with { Email = "a@b.c" }, requiresAddress: true);

        Assert.DoesNotContain("email", errors.Select(error => error.Field));
        Assert.Contains("firstName", errors.Select(error => error.Field));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void A_complete_delivery_checkout_has_no_errors()
    {
        Assert.Empty(PlaceOrderValidator.Validate(Valid, requiresAddress: true));
    }

    /// <summary>
    /// <b><c>phone: "not-a-phone"</c> placed an order (201).</b> The field was checked for presence and
    /// twenty characters and nothing else, which defeats the only reason it is required — a courier has to
    /// be able to call it.
    /// </summary>
    [Theory]
    [InlineData("not-a-phone")]
    [InlineData("call me")]
    [InlineData("077ABCDEFG")]
    [InlineData("12345")]
    [InlineData("0")]
    [InlineData("-")]
    [InlineData("0771234567890123456")] // Nineteen digits: fits the column, is not a telephone number.
    [InlineData("077123456+7")] // A `+` anywhere but the front is a typo or two numbers in one box.
    [InlineData("077<script>")]
    public void A_phone_number_the_courier_could_not_call_is_refused(string phone)
    {
        var onTheOrder = PlaceOrderValidator.Validate(Valid with { Phone = phone }, requiresAddress: true);
        Assert.Contains("phone", onTheOrder.Select(error => error.Field));

        // The address's own number is held to the same rule: it is the one the driver actually dials.
        var onTheAddress = PlaceOrderValidator.Validate(
            Valid with { Address = Valid.Address! with { Phone = phone } },
            requiresAddress: true);
        Assert.Contains("address.phone", onTheAddress.Select(error => error.Field));
    }

    /// <summary>
    /// The other half of the rule, and the more important one: a shopper who typed their real number in
    /// any of the ways people write one down must still be able to buy something.
    /// </summary>
    [Theory]
    [InlineData("0771234567")]
    [InlineData("077 123 4567")]
    [InlineData("077-123-4567")]
    [InlineData("(077) 1234567")]
    [InlineData("011 2345678")]
    [InlineData("+94771234567")]
    [InlineData("+94 77 123 4567")]
    [InlineData("0094771234567")]
    [InlineData("771234567")]
    [InlineData("+44 20 7123 4567")]
    public void A_phone_number_somebody_really_typed_is_accepted(string phone)
    {
        Assert.Empty(PlaceOrderValidator.Validate(Valid with { Phone = phone }, requiresAddress: true));
    }

    [Fact]
    public void A_missing_address_is_one_error_and_not_five()
    {
        // Five near-identical messages against fields the caller never sent would be noise; the form was
        // not filled in at all.
        var errors = PlaceOrderValidator.Validate(Valid with { Address = null }, requiresAddress: true);

        var error = Assert.Single(errors);
        Assert.Equal("address", error.Field);
    }

    [Theory]
    [InlineData(null, "address.recipientName")]
    [InlineData("", "address.recipientName")]
    [InlineData("   ", "address.recipientName")]
    public void Whitespace_is_the_same_as_missing(string? recipientName, string expectedField)
    {
        // Three shapes that arrive as different values and are the same mistake: omitted, null, and
        // spaces. A field that passed on "   " would write a blank recipient name into a parcel label.
        var errors = PlaceOrderValidator.Validate(
            Valid with { Address = Valid.Address! with { RecipientName = recipientName } },
            requiresAddress: true);

        Assert.Contains(expectedField, errors.Select(error => error.Field));
    }

    [Fact]
    public void The_four_fields_CK_Orders_ShipAddress_requires_are_all_required()
    {
        var errors = PlaceOrderValidator.Validate(
            Valid with { Address = new CheckoutAddressInput(null, null, null, null, null, null, null) },
            requiresAddress: true);

        var fields = errors.Select(error => error.Field).ToList();

        Assert.Contains("address.recipientName", fields);
        Assert.Contains("address.phone", fields);
        Assert.Contains("address.line1", fields);
        Assert.Contains("address.city", fields);

        // The three CK_Orders_ShipAddress does not require stay optional.
        Assert.DoesNotContain("address.line2", fields);
        Assert.DoesNotContain("address.postalCode", fields);
        Assert.DoesNotContain("address.landmark", fields);
    }

    [Fact]
    public void Store_pickup_needs_a_location_and_no_address()
    {
        var errors = PlaceOrderValidator.Validate(
            Empty with
            {
                CheckoutKey = Guid.NewGuid(),
                FirstName = "Nimali",
                Email = "nimali@example.com",
                Phone = "0771234567",
                DeliveryMethodId = 3,
                PickupLocationId = 7,
                PaymentMethodCode = "BankTransfer",
                ExpectedGrandTotal = 5_490m,
            },
            requiresAddress: false);

        Assert.Empty(errors);
    }

    [Fact]
    public void Store_pickup_without_a_location_is_refused()
    {
        var errors = PlaceOrderValidator.Validate(
            Empty with
            {
                CheckoutKey = Guid.NewGuid(),
                FirstName = "Nimali",
                Email = "nimali@example.com",
                Phone = "0771234567",
                DeliveryMethodId = 3,
                PaymentMethodCode = "BankTransfer",
                ExpectedGrandTotal = 5_490m,
            },
            requiresAddress: false);

        var error = Assert.Single(errors);
        Assert.Equal("pickupLocationId", error.Field);
    }

    [Fact]
    public void An_unresolved_delivery_method_skips_the_delivery_branch_entirely()
    {
        // requiresAddress: null means the method could not be resolved. Demanding address fields of a
        // shopper whose store-pickup option vanished under them would be four wrong messages on top of
        // the one true one.
        var errors = PlaceOrderValidator.Validate(
            Valid with { Address = null, DistrictId = null, DeliveryMethodId = 99 },
            requiresAddress: null);

        Assert.Empty(errors);
    }

    [Fact]
    public void The_expected_total_cannot_be_omitted()
    {
        // G-10's guard must not be skippable by leaving the field out: without it the server would place
        // an order at a total nobody had agreed to.
        var errors = PlaceOrderValidator.Validate(
            Valid with { ExpectedGrandTotal = null },
            requiresAddress: true);

        var error = Assert.Single(errors);
        Assert.Equal("expectedGrandTotal", error.Field);
    }

    [Fact]
    public void A_zero_expected_total_is_allowed_through_to_the_comparison()
    {
        // Zero is a figure, not a missing field. It will not match any priced total — doc 08 §6 clamps the
        // grand total at Rs. 1.00 — so it is refused by G-10's comparison with a message about prices
        // rather than here with a message about a form.
        Assert.Empty(PlaceOrderValidator.Validate(Valid with { ExpectedGrandTotal = 0m }, requiresAddress: true));
    }

    [Fact]
    public void An_empty_checkout_key_is_refused()
    {
        var errors = PlaceOrderValidator.Validate(Valid with { CheckoutKey = Guid.Empty }, requiresAddress: true);

        var error = Assert.Single(errors);
        Assert.Equal("checkoutKey", error.Field);
    }

    [Theory]
    [InlineData("nimali@example.com")]
    [InlineData("nimali.perera+orders@example.co.uk")]
    [InlineData("o'brien@example.lk")]
    [InlineData("a@b.c")]
    public void A_real_address_is_accepted(string email)
    {
        // Deliberately permissive. A pattern that rejects apostrophes, plus-addressing or a new top-level
        // domain stops a shopper who typed their own address correctly from buying anything.
        Assert.True(PlaceOrderValidator.LooksLikeEmail(email));
    }

    [Theory]
    [InlineData("nimali")]
    [InlineData("0771234567")]
    [InlineData("@example.com")]
    [InlineData("nimali@")]
    [InlineData("nimali@example")]
    [InlineData("nimali@example.")]
    [InlineData("nimali@@example.com")]
    [InlineData("nimali perera@example.com")]
    public void Something_that_is_not_an_address_is_refused(string candidate)
    {
        Assert.False(PlaceOrderValidator.LooksLikeEmail(candidate));
    }

    [Fact]
    public void An_email_that_is_not_an_address_is_reported_against_the_email_field()
    {
        var errors = PlaceOrderValidator.Validate(Valid with { Email = "0771234567" }, requiresAddress: true);

        var error = Assert.Single(errors);
        Assert.Equal("email", error.Field);
    }

    [Theory]
    [InlineData(nameof(PlaceOrderCommand.FirstName), PlaceOrderValidator.MaxNameLength, "firstName")]
    [InlineData(nameof(PlaceOrderCommand.LastName), PlaceOrderValidator.MaxNameLength, "lastName")]
    [InlineData(nameof(PlaceOrderCommand.Phone), PlaceOrderValidator.MaxPhoneLength, "phone")]
    [InlineData(nameof(PlaceOrderCommand.CustomerNote), PlaceOrderValidator.MaxCustomerNoteLength, "customerNote")]
    public void A_value_one_character_over_the_column_is_refused(string property, int maxLength, string field)
    {
        // One over, not wildly over: the boundary is where an off-by-one lives, and a value that slipped
        // through here would arrive as a SQL truncation error inside the placement transaction.
        var tooLong = new string('x', maxLength + 1);

        var command = property switch
        {
            nameof(PlaceOrderCommand.FirstName) => Valid with { FirstName = tooLong },
            nameof(PlaceOrderCommand.LastName) => Valid with { LastName = tooLong },
            nameof(PlaceOrderCommand.Phone) => Valid with { Phone = tooLong },
            _ => Valid with { CustomerNote = tooLong },
        };

        var errors = PlaceOrderValidator.Validate(command, requiresAddress: true);

        var error = Assert.Single(errors);
        Assert.Equal(field, error.Field);
    }

    [Fact]
    public void A_value_exactly_at_the_column_length_is_accepted()
    {
        var atLimit = new string('x', PlaceOrderValidator.MaxNameLength);

        Assert.Empty(PlaceOrderValidator.Validate(Valid with { FirstName = atLimit }, requiresAddress: true));
    }

    [Fact]
    public void An_address_field_one_character_over_its_column_is_refused()
    {
        var errors = PlaceOrderValidator.Validate(
            Valid with
            {
                Address = Valid.Address! with
                {
                    PostalCode = new string('9', PlaceOrderValidator.MaxPostalCodeLength + 1),
                },
            },
            requiresAddress: true);

        var error = Assert.Single(errors);
        Assert.Equal("address.postalCode", error.Field);
    }

    [Fact]
    public void Surrounding_whitespace_does_not_count_towards_the_length()
    {
        // The value stored is the trimmed one, so the length check has to measure the trimmed one too.
        var padded = "  " + new string('x', PlaceOrderValidator.MaxNameLength) + "  ";

        Assert.Empty(PlaceOrderValidator.Validate(Valid with { FirstName = padded }, requiresAddress: true));
    }

    [Fact]
    public void An_emoji_surrogate_pair_is_measured_the_way_the_column_measures_it()
    {
        // nvarchar(80) counts UTF-16 code units, and so does String.Length, so a name of 40 emoji is 80
        // characters and fits exactly. W1-12's constraint tests found this class of thing worth checking.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F60A", PlaceOrderValidator.MaxNameLength / 2));

        Assert.Equal(PlaceOrderValidator.MaxNameLength, emoji.Length);
        Assert.Empty(PlaceOrderValidator.Validate(Valid with { FirstName = emoji }, requiresAddress: true));

        var errors = PlaceOrderValidator.Validate(
            Valid with { FirstName = emoji + "\U0001F60A" },
            requiresAddress: true);

        Assert.Equal("firstName", Assert.Single(errors).Field);
    }
}
