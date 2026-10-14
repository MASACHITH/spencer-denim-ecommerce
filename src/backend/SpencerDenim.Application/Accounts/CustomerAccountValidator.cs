using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Application.Sales.Checkout;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Application.Accounts;

/// <summary>
/// The field-level validation of doc 11 P11's forms, done before anything touches the database.
/// </summary>
/// <remarks>
/// <b>Shape first, references second</b> — <c>PlaceOrderValidator</c>'s discipline, for its reasons: a
/// request naming nothing valid must not cost a query, and a request missing six fields must come back
/// with six messages rather than the first one.
/// <para>
/// <b>Every width here is read from the constant that owns the column</b>, never written out again.
/// #126's defect was a <c>maxlength</c> of 120 against a 100-character column passing the form, the stub
/// and sixteen browser tests, and the only reliable cure is one number with one name. The numbers come
/// from <see cref="CustomerAddress"/> for the address book, from <see cref="NameMaxLength"/> for the
/// name columns on <c>identity.Users</c>, and from <see cref="NewsletterEmail.MaxLength"/> for the
/// address.
/// </para>
/// <para>
/// <b>Two helpers are borrowed rather than copied, and both borrowings are deliberate.</b>
/// <see cref="NewsletterEmail.TryNormalise"/> is the one place in this codebase that decides whether a
/// string is an address the shop will store, and <c>PlaceOrderValidator.LooksLikePhone</c> is the one
/// place that decides whether a number is dialable. A second copy of either would be a second answer to
/// a question a shopper can only be told one answer to. Both class <i>names</i> are now narrower than
/// their contents — a newsletter type validating a sign-in address, a checkout type validating a profile
/// — and the task report says so; renaming a shared Domain type is not this task's change to make.
/// </para>
/// </remarks>
internal static class CustomerAccountValidator
{
    /// <summary><c>identity.Users.FirstName</c> / <c>LastName</c>, <c>nvarchar(80)</c> (doc 08 §3.1).</summary>
    public const int NameMaxLength = 80;

    /// <summary>Doc 11 P11 step A.1's form.</summary>
    public static IReadOnlyList<ValidationError> ValidateRegistration(CustomerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var errors = new List<ValidationError>();

        Required(errors, "firstName", registration.FirstName, NameMaxLength, "Enter your first name.");
        Optional(errors, "lastName", registration.LastName, NameMaxLength);
        Email(errors, "email", registration.Email);
        OptionalPhone(errors, "phone", registration.Phone);
        Password(errors, "password", registration.Password);

        return errors;
    }

    /// <summary>
    /// Doc 11 P11 step B.5's form: present and within the columns, and <b>nothing else</b>.
    /// </summary>
    /// <remarks>
    /// <b>A sign-in form must not validate the <i>shape</i> of what was typed.</b> Refusing "that is not
    /// a valid email address" on a sign-in is an enumeration oracle of a subtler kind — it tells an
    /// attacker which strings are worth trying — and it refuses a real customer whose address passed a
    /// looser check when they registered. Length is checked because a megabyte in a password field is a
    /// hash the server should not pay for; everything else is "invalid email or password".
    /// </remarks>
    public static IReadOnlyList<ValidationError> ValidateSignIn(string? email, string? password)
    {
        var errors = new List<ValidationError>();

        Required(errors, "email", email, NewsletterEmail.MaxLength, "Enter your email address.");
        Required(errors, "password", password, PasswordMaxLength, "Enter your password.");

        return errors;
    }

    /// <summary>Doc 11 P11 step C.8's form: one field.</summary>
    public static IReadOnlyList<ValidationError> ValidateForgotPassword(string? email)
    {
        var errors = new List<ValidationError>();

        Required(errors, "email", email, NewsletterEmail.MaxLength, "Enter your email address.");

        return errors;
    }

    /// <summary>Doc 11 P11 step 10's profile form (ACC-02).</summary>
    public static IReadOnlyList<ValidationError> ValidateProfile(CustomerProfileEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var errors = new List<ValidationError>();

        Required(errors, "firstName", edit.FirstName, NameMaxLength, "Enter your first name.");
        Optional(errors, "lastName", edit.LastName, NameMaxLength);
        OptionalPhone(errors, "phone", edit.Phone);

        if (edit.DefaultDistrictId is { } districtId && districtId <= 0)
        {
            errors.Add(new ValidationError("defaultDistrictId", "Choose a district, or leave it unset."));
        }

        return errors;
    }

    /// <summary>Doc 11 P11 step 11's address form (ACC-02, CHK-01).</summary>
    public static IReadOnlyList<ValidationError> ValidateAddress(CustomerAddressEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var errors = new List<ValidationError>();

        Optional(errors, "label", edit.Label, CustomerAddress.LabelMaxLength);

        Required(
            errors,
            "recipientName",
            edit.RecipientName,
            CustomerAddress.RecipientNameMaxLength,
            "Enter the name the courier should ask for.");

        RequiredPhone(errors, "phone", edit.Phone);

        Required(
            errors,
            "line1",
            edit.Line1,
            CustomerAddress.LineMaxLength,
            "Enter the first line of the address.");

        Optional(errors, "line2", edit.Line2, CustomerAddress.LineMaxLength);
        Required(errors, "city", edit.City, CustomerAddress.CityMaxLength, "Enter the town or city.");
        Optional(errors, "postalCode", edit.PostalCode, CustomerAddress.PostalCodeMaxLength);
        Optional(errors, "landmark", edit.Landmark, CustomerAddress.LandmarkMaxLength);

        if (edit.DistrictId <= 0)
        {
            errors.Add(new ValidationError("districtId", "Choose a district."));
        }

        return errors;
    }

    /// <summary>
    /// The longest password the server will hash.
    /// </summary>
    /// <remarks>
    /// <b>A ceiling and not a rule a shopper has to meet.</b> Identity's hasher is PBKDF2 and its cost
    /// grows with the input, so an unbounded password field is a cheap way to make the server do
    /// expensive work — and a form that accepted a megabyte would also break the 200-character column
    /// nothing stores it in. 256 is far beyond any password anybody types and far below anything
    /// expensive.
    /// </remarks>
    public const int PasswordMaxLength = 256;

    private static void Password(List<ValidationError> errors, string field, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            errors.Add(new ValidationError(field, "Choose a password."));
            return;
        }

        if (value.Length > PasswordMaxLength)
        {
            errors.Add(new ValidationError(
                field,
                $"A password can be at most {PasswordMaxLength} characters."));
            return;
        }

        // The length rule only. Identity's own validators — the four character classes — run inside the
        // store, against the user being created, and their messages come back from there: they are the
        // ones that match what is actually enforced, and a second wording here would be a second source
        // of truth about the rule.
        if (!CustomerPasswordPolicy.IsLongEnough(value))
        {
            errors.Add(new ValidationError(field, CustomerPasswordPolicy.Requirement));
        }
    }

    private static void Email(List<ValidationError> errors, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new ValidationError(field, "Enter your email address."));
        }
        else if (!NewsletterEmail.TryNormalise(value, out _, out _))
        {
            errors.Add(new ValidationError(field, "Enter an email address we can send to."));
        }
    }

    private static void RequiredPhone(List<ValidationError> errors, string field, string? value)
    {
        var trimmed = Trimmed(value);

        if (trimmed is null)
        {
            errors.Add(new ValidationError(field, "Enter a phone number for the delivery."));
        }
        else if (trimmed.Length > CustomerAddress.PhoneMaxLength)
        {
            errors.Add(new ValidationError(field, TooLong(CustomerAddress.PhoneMaxLength)));
        }
        else if (!PlaceOrderValidator.LooksLikePhone(trimmed))
        {
            errors.Add(new ValidationError(field, "Enter a phone number the courier can call."));
        }
    }

    private static void OptionalPhone(List<ValidationError> errors, string field, string? value)
    {
        if (Trimmed(value) is not { } trimmed)
        {
            return;
        }

        if (trimmed.Length > CustomerAddress.PhoneMaxLength)
        {
            errors.Add(new ValidationError(field, TooLong(CustomerAddress.PhoneMaxLength)));
        }
        else if (!PlaceOrderValidator.LooksLikePhone(trimmed))
        {
            errors.Add(new ValidationError(field, "Enter a phone number we can call."));
        }
    }

    private static void Required(
        List<ValidationError> errors,
        string field,
        string? value,
        int maxLength,
        string missingMessage)
    {
        var trimmed = Trimmed(value);

        if (trimmed is null)
        {
            errors.Add(new ValidationError(field, missingMessage));
        }
        else if (trimmed.Length > maxLength)
        {
            errors.Add(new ValidationError(field, TooLong(maxLength)));
        }
    }

    private static void Optional(List<ValidationError> errors, string field, string? value, int maxLength)
    {
        if (Trimmed(value) is { } trimmed && trimmed.Length > maxLength)
        {
            errors.Add(new ValidationError(field, TooLong(maxLength)));
        }
    }

    private static string TooLong(int maxLength) => $"This can be at most {maxLength} characters.";

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
