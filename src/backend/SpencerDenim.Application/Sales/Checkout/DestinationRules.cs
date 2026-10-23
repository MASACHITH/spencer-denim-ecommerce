using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Domain.Reference;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Checkout;

/// <summary>
/// The country rules an address and an order share (owner decision 2026-10-21: Sri Lanka plus worldwide
/// delivery). Checkout and the account address book both use these, so a saved address that the book
/// accepted is one checkout accepts.
/// <para>
/// <b>Deliberately simple.</b> A Sri Lankan address is validated exactly as it was before overseas delivery
/// existed. An overseas address adds three things: a state or region where the country always uses one, a
/// postcode where the country always uses one (letters, digits, spaces and hyphens, up to ten), and a phone
/// number written with its country code, because the courier at the far end has to be able to dial it. No
/// country's postcode format is checked: a wrong pattern table refuses real addresses, and the parcel label
/// is read by a person.
/// </para>
/// </summary>
public static class DestinationRules
{
    /// <summary><c>Ship_Region</c> / <c>CustomerAddresses.Region</c>, <c>nvarchar(80)</c>.</summary>
    public const int MaxRegionLength = 80;

    /// <summary><c>Ship_PostalCode</c>, <c>varchar(10)</c>.</summary>
    public const int MaxPostalCodeLength = 10;

    /// <summary>
    /// Sri Lanka as a destination, for a caller that has no country to hand because it predates overseas
    /// delivery. Its overseas quote is meaningless: delivery there is priced by district.
    /// </summary>
    public static CheckoutCountry SriLanka { get; } =
        new(Country.SriLankaCode, "Sri Lanka", true, false, false, new OverseasQuote(OverseasRefusal.None, 0m, 0, 0));

    /// <summary>
    /// The country a request names, upper-cased: Sri Lanka when it names none — every client before overseas
    /// delivery sent none, and their orders are Sri Lankan — and <see langword="null"/> when what it sent is
    /// not two ASCII letters.
    /// </summary>
    public static string? NormaliseCountryCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Country.SriLankaCode;
        }

        var trimmed = raw.Trim();

        return trimmed.Length == Country.CodeLength && trimmed.All(char.IsAsciiLetter)
            ? trimmed.ToUpperInvariant()
            : null;
    }

    /// <summary>The message for a country that is unknown or malformed.</summary>
    public static ValidationError UnknownCountry(string field) =>
        new(field, "Choose the country your order is going to.");

    /// <summary>The message for a country the shop does not deliver to right now.</summary>
    public static ValidationError NotServed(CheckoutCountry country)
    {
        ArgumentNullException.ThrowIfNull(country);

        return new ValidationError(
            "countryCode",
            $"We cannot deliver to {country.Name} at the moment. Choose another country.");
    }

    /// <summary>
    /// Whether the delivery method fits the country: overseas only by International delivery, and Sri Lanka
    /// never by it. Store pickup is a Sri Lankan method, so it falls under the first rule.
    /// </summary>
    public static ValidationError? MethodMismatch(CheckoutCountry country, CheckoutDeliveryMethod method)
    {
        ArgumentNullException.ThrowIfNull(country);
        ArgumentNullException.ThrowIfNull(method);

        var international = string.Equals(method.Code, DeliveryMethod.InternationalCode, StringComparison.Ordinal);

        if (country.IsDomestic && international)
        {
            return new ValidationError(
                "deliveryMethodId",
                "International delivery is for addresses outside Sri Lanka. Choose another option.");
        }

        if (!country.IsDomestic && !international)
        {
            return new ValidationError(
                "deliveryMethodId",
                method.RequiresAddress
                    ? $"This delivery option is for Sri Lanka only. Choose International delivery for {country.Name}."
                    : "Store pickup is only available in Sri Lanka.");
        }

        return null;
    }

    /// <summary>
    /// The overseas-only address checks. Adds nothing for a Sri Lankan address: its rules are unchanged.
    /// </summary>
    /// <param name="errors">Where to add the problems.</param>
    /// <param name="prefix">The field prefix, e.g. <c>address.</c> at checkout and empty in the address book.</param>
    /// <param name="country">The destination.</param>
    /// <param name="region">The state or region the shopper typed.</param>
    /// <param name="postalCode">The postcode the shopper typed.</param>
    /// <param name="phone">The delivery phone number the shopper typed.</param>
    public static void ValidateOverseasAddress(
        List<ValidationError> errors,
        string prefix,
        CheckoutCountry country,
        string? region,
        string? postalCode,
        string? phone)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(country);

        if (country.IsDomestic)
        {
            return;
        }

        var trimmedRegion = Trimmed(region);
        if (trimmedRegion is null)
        {
            if (country.RegionRequired)
            {
                errors.Add(new ValidationError(prefix + "region", "Enter the state, province or region."));
            }
        }
        else if (trimmedRegion.Length > MaxRegionLength)
        {
            errors.Add(new ValidationError(prefix + "region", TooLong(MaxRegionLength)));
        }

        var trimmedPostal = Trimmed(postalCode);
        if (trimmedPostal is null)
        {
            if (country.PostalCodeRequired)
            {
                errors.Add(new ValidationError(prefix + "postalCode", $"Enter the postcode for {country.Name}."));
            }
        }
        else if (trimmedPostal.Length > MaxPostalCodeLength)
        {
            errors.Add(new ValidationError(prefix + "postalCode", TooLong(MaxPostalCodeLength)));
        }
        else if (!LooksLikePostalCode(trimmedPostal))
        {
            errors.Add(new ValidationError(
                prefix + "postalCode",
                "A postcode can use only letters, numbers, spaces and hyphens."));
        }

        if (Trimmed(phone) is { } trimmedPhone && !HasCountryCode(trimmedPhone))
        {
            errors.Add(new ValidationError(
                prefix + "phone",
                "Include the country code, for example +44 20 7946 0958."));
        }
    }

    /// <summary>Letters, digits, spaces and hyphens, starting and ending with a letter or digit.</summary>
    public static bool LooksLikePostalCode(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return candidate.Length > 0
            && char.IsAsciiLetterOrDigit(candidate[0])
            && char.IsAsciiLetterOrDigit(candidate[^1])
            && candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '-');
    }

    /// <summary>
    /// A number written in international form: a leading <c>+</c> or <c>00</c>. The digit count is
    /// <see cref="PlaceOrderValidator.LooksLikePhone"/>'s job.
    /// </summary>
    public static bool HasCountryCode(string phone)
    {
        ArgumentNullException.ThrowIfNull(phone);

        var trimmed = phone.TrimStart();

        return trimmed.StartsWith('+') || trimmed.StartsWith("00", StringComparison.Ordinal);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string TooLong(int maxLength) => $"This can be at most {maxLength} characters.";
}
