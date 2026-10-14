using SpencerDenim.Application.Accounts;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Application.Accounts;

/// <summary>
/// The field rules of doc 11 P11's forms (<see cref="CustomerAccountValidator"/>).
/// </summary>
/// <remarks>
/// <b>The widths are asserted against the constants that own the columns</b>, never against a number
/// written out here. #126's defect was a <c>maxlength</c> of 120 against a 100-character column passing
/// the form, the stub and sixteen browser tests; the only reliable cure is one number with one name, and
/// a test that restated the number would be a third copy of it.
/// </remarks>
public sealed class CustomerAccountValidatorTests
{
    private static CustomerRegistration Registration(
        string firstName = "Nimal",
        string? lastName = "Perera",
        string email = "nimal@example.com",
        string password = "Correct-Horse-Battery-1!",
        string? phone = null) =>
        new(firstName, lastName, email, password, phone);

    /// <summary>A good registration has nothing wrong with it.</summary>
    /// <remarks>
    /// <b>Without this the whole file could pass against a validator that refused everything.</b> That is
    /// the vacuous-test shape this project has shipped, so the positive case comes first.
    /// </remarks>
    [Fact]
    public void A_good_registration_passes()
    {
        Assert.Empty(CustomerAccountValidator.ValidateRegistration(Registration()));
    }

    /// <summary>
    /// Every missing field comes back at once, not the first one.
    /// </summary>
    /// <remarks>
    /// <c>PlaceOrderValidator</c>'s discipline: "a request missing six fields must come back with six
    /// messages rather than the first one". A shopper who fixes one field and is then told about the next
    /// fills the form in four round trips.
    /// </remarks>
    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var errors = CustomerAccountValidator.ValidateRegistration(
            Registration(firstName: " ", email: "not-an-address", password: "short"));

        Assert.Contains(errors, error => error.Field == "firstName");
        Assert.Contains(errors, error => error.Field == "email");
        Assert.Contains(errors, error => error.Field == "password");
    }

    /// <summary>The length rule is the policy's, and so is the sentence a shopper reads.</summary>
    [Fact]
    public void A_short_password_is_refused_with_the_policys_own_sentence()
    {
        var errors = CustomerAccountValidator.ValidateRegistration(Registration(password: "Ab1!cdef"));

        var message = Assert.Single(errors, error => error.Field == "password").Message;
        Assert.Equal(CustomerPasswordPolicy.Requirement, message);
    }

    /// <summary>
    /// A password exactly at the minimum passes, and one character under it does not.
    /// </summary>
    /// <remarks>
    /// <b>The boundary, read from the constant.</b> An off-by-one here is the difference between a rule
    /// the form's hint states and a rule the server enforces — and the hint says "at least 10", so 10
    /// must pass.
    /// </remarks>
    [Fact]
    public void The_minimum_length_is_inclusive()
    {
        var atTheLimit = "Aa1!" + new string('x', CustomerPasswordPolicy.MinimumLength - 4);
        var oneShort = atTheLimit[..^1];

        Assert.Equal(CustomerPasswordPolicy.MinimumLength, atTheLimit.Length);
        Assert.Empty(CustomerAccountValidator.ValidateRegistration(Registration(password: atTheLimit)));
        Assert.NotEmpty(CustomerAccountValidator.ValidateRegistration(Registration(password: oneShort)));
    }

    /// <summary>
    /// A password longer than the hashing ceiling is refused rather than hashed.
    /// </summary>
    /// <remarks>
    /// Identity's hasher is PBKDF2 and its cost grows with the input, so an unbounded password field is a
    /// cheap way to make the server do expensive work on an endpoint that needs no authentication.
    /// </remarks>
    [Fact]
    public void A_password_longer_than_the_ceiling_is_refused()
    {
        var huge = "Aa1!" + new string('x', CustomerAccountValidator.PasswordMaxLength);

        var errors = CustomerAccountValidator.ValidateRegistration(Registration(password: huge));

        Assert.Contains(errors, error => error.Field == "password");
    }

    /// <summary>A name longer than its column is refused, with the column's own number.</summary>
    [Fact]
    public void A_name_longer_than_its_column_is_refused_with_that_number()
    {
        var tooLong = new string('x', CustomerAccountValidator.NameMaxLength + 1);

        var message = Assert.Single(
            CustomerAccountValidator.ValidateRegistration(Registration(firstName: tooLong)),
            error => error.Field == "firstName").Message;

        Assert.Contains(
            CustomerAccountValidator.NameMaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            message,
            StringComparison.Ordinal);
    }

    /// <summary>A name exactly at the column's width passes.</summary>
    [Fact]
    public void A_name_exactly_at_the_column_width_passes()
    {
        var atTheLimit = new string('x', CustomerAccountValidator.NameMaxLength);

        Assert.Empty(CustomerAccountValidator.ValidateRegistration(Registration(firstName: atTheLimit)));
    }

    /// <summary>The last name and the phone are optional, which doc 11 P11 step A.1 says twice.</summary>
    [Fact]
    public void The_last_name_and_the_phone_are_optional()
    {
        Assert.Empty(CustomerAccountValidator.ValidateRegistration(
            Registration(lastName: null, phone: null)));

        Assert.Empty(CustomerAccountValidator.ValidateRegistration(
            Registration(lastName: "   ", phone: "   ")));
    }

    /// <summary>A phone that is given has to be dialable.</summary>
    /// <remarks>
    /// Decided by <c>PlaceOrderValidator.LooksLikePhone</c>, which is the one place in this codebase that
    /// answers that question — a second copy would be a second answer a shopper could be given.
    /// </remarks>
    [Theory]
    [InlineData("+94771234567", true)]
    [InlineData("077 123 4567", true)]
    [InlineData("0771234567", true)]
    [InlineData("12345", false)]
    [InlineData("not a number", false)]
    [InlineData("+94 77 +123", false)]
    public void A_phone_that_is_given_has_to_be_dialable(string phone, bool expected)
    {
        var errors = CustomerAccountValidator.ValidateRegistration(Registration(phone: phone));

        Assert.Equal(expected, errors.All(error => error.Field != "phone"));
    }

    /// <summary>
    /// The sign-in form checks only that the fields are there, and never the shape of the address.
    /// </summary>
    /// <remarks>
    /// <b>Refusing "that is not a valid email address" on a sign-in is an enumeration oracle of a subtler
    /// kind</b> — it tells an attacker which strings are worth trying — and it refuses a real customer
    /// whose address passed a looser check when they registered. Length is still checked, because a
    /// megabyte in a password field is a hash the server should not pay for.
    /// </remarks>
    [Fact]
    public void The_sign_in_form_does_not_judge_the_shape_of_the_address()
    {
        Assert.Empty(CustomerAccountValidator.ValidateSignIn("not-an-address", "whatever"));
        Assert.NotEmpty(CustomerAccountValidator.ValidateSignIn(" ", "whatever"));
        Assert.NotEmpty(CustomerAccountValidator.ValidateSignIn("a@b.cd", " "));
        Assert.NotEmpty(CustomerAccountValidator.ValidateSignIn(
            "a@b.cd",
            new string('x', CustomerAccountValidator.PasswordMaxLength + 1)));
    }

    /// <summary>
    /// The forgot-password form refuses a blank field and accepts a badly-shaped address.
    /// </summary>
    /// <remarks>
    /// A blank field says nothing about whether any address has an account, so refusing it is safe. A
    /// badly-shaped one is accepted and nothing is sent, for the sign-in form's reason.
    /// </remarks>
    [Fact]
    public void The_forgot_password_form_refuses_only_a_blank_field()
    {
        Assert.NotEmpty(CustomerAccountValidator.ValidateForgotPassword(null));
        Assert.NotEmpty(CustomerAccountValidator.ValidateForgotPassword("   "));
        Assert.Empty(CustomerAccountValidator.ValidateForgotPassword("not-an-address"));
    }

    /// <summary>Every width in the address form comes from the column that owns it.</summary>
    [Theory]
    [InlineData("recipientName")]
    [InlineData("line1")]
    [InlineData("city")]
    [InlineData("label")]
    [InlineData("postalCode")]
    [InlineData("landmark")]
    [InlineData("line2")]
    public void Every_address_field_is_refused_past_its_own_column_width(string field)
    {
        var width = field switch
        {
            "recipientName" => CustomerAddress.RecipientNameMaxLength,
            "line1" or "line2" or "landmark" => CustomerAddress.LineMaxLength,
            "city" => CustomerAddress.CityMaxLength,
            "label" => CustomerAddress.LabelMaxLength,
            "postalCode" => CustomerAddress.PostalCodeMaxLength,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field."),
        };

        var tooLong = new string('x', width + 1);

        var edit = new CustomerAddressEdit(
            field == "label" ? tooLong : "Home",
            field == "recipientName" ? tooLong : "Nimal Perera",
            "+94771234567",
            field == "line1" ? tooLong : "221 Galle Road",
            field == "line2" ? tooLong : null,
            field == "city" ? tooLong : "Colombo",
            1,
            field == "postalCode" ? tooLong : "00300",
            field == "landmark" ? tooLong : null,
            IsDefault: false);

        var message = Assert.Single(
            CustomerAccountValidator.ValidateAddress(edit),
            error => error.Field == field).Message;

        Assert.Contains(
            width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            message,
            StringComparison.Ordinal);
    }

    /// <summary>A good address has nothing wrong with it, and an empty one names every required field.</summary>
    [Fact]
    public void The_address_form_names_every_required_field()
    {
        var good = new CustomerAddressEdit(
            "Home",
            "Nimal Perera",
            "+94771234567",
            "221 Galle Road",
            null,
            "Colombo",
            1,
            "00300",
            null,
            IsDefault: true);

        Assert.Empty(CustomerAccountValidator.ValidateAddress(good));

        var empty = new CustomerAddressEdit(
            null,
            string.Empty,
            string.Empty,
            string.Empty,
            null,
            string.Empty,
            0,
            null,
            null,
            IsDefault: false);

        var fields = CustomerAccountValidator.ValidateAddress(empty)
            .Select(error => error.Field)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new HashSet<string>(["recipientName", "phone", "line1", "city", "districtId"], StringComparer.Ordinal),
            fields);
    }

    /// <summary>
    /// A district id of zero is refused as "choose one", not as "that is invalid".
    /// </summary>
    /// <remarks>
    /// <b>Zero is what an omitted number binds to</b>, which is why the request type makes it nullable and
    /// the endpoint passes zero: the validator then gives the message a shopper who chose nothing needs,
    /// rather than one about a value they never typed.
    /// </remarks>
    [Fact]
    public void A_district_of_zero_reads_as_choose_one()
    {
        var edit = new CustomerAddressEdit(
            null,
            "Nimal Perera",
            "+94771234567",
            "221 Galle Road",
            null,
            "Colombo",
            0,
            null,
            null,
            IsDefault: false);

        var message = Assert.Single(
            CustomerAccountValidator.ValidateAddress(edit),
            error => error.Field == "districtId").Message;

        Assert.Contains("Choose a district", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The profile form leaves the district unset rather than refusing it, and refuses a negative one.
    /// </summary>
    [Fact]
    public void The_profile_form_allows_no_district_and_refuses_a_nonsense_one()
    {
        Assert.Empty(CustomerAccountValidator.ValidateProfile(
            new CustomerProfileEdit("Nimal", null, null, null)));

        Assert.NotEmpty(CustomerAccountValidator.ValidateProfile(
            new CustomerProfileEdit("Nimal", null, null, -1)));
    }

    /// <summary>
    /// The policy's sentence names every rule that is actually enforced.
    /// </summary>
    /// <remarks>
    /// <b>A hint that stated only the length would leave a shopper meeting the stated rule and being
    /// refused.</b> The four character classes are Identity's and are enforced whether or not they are
    /// mentioned, so the sentence has to mention them.
    /// </remarks>
    [Fact]
    public void The_password_hint_names_every_rule_that_is_enforced()
    {
        var hint = CustomerPasswordPolicy.Requirement;

        Assert.Contains(
            CustomerPasswordPolicy.MinimumLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            hint,
            StringComparison.Ordinal);

        Assert.Contains("upper-case", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lower-case", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("number", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("symbol", hint, StringComparison.OrdinalIgnoreCase);
    }
}
