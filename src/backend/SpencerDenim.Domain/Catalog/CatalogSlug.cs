using System.Text.RegularExpressions;

namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// The one spelling rule for a catalogue slug: lower-case letters and digits, separated by single
/// hyphens. It lives in the domain because both sides need the same answer — the storefront refuses a
/// URL that does not match it, so a row that does not match it is a product no one can open.
/// <para>
/// The database enforces it too, on every slug column an admin can write: <c>CK_Departments_Slug</c> and
/// <c>CK_Categories_Slug</c> (W1-12) and <c>CK_Products_Slug</c> (W1-03). This guard catches a typo on the
/// way in with a named field error; the check constraint catches a support script, a data fix or a restore
/// from a spreadsheet, none of which comes through a property setter.
/// </para>
/// </summary>
public static partial class CatalogSlug
{
    /// <summary>True when the value is a slug this catalogue can put in a URL.</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && Pattern().IsMatch(value);

    /// <summary>
    /// Returns the value, or throws when it is not a slug. Used by the property setters that persist
    /// one, so the rule holds however the row is written.
    /// </summary>
    public static string Require(string? value, string parameterName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"'{parameterName}' must be a slug: lower-case letters, digits and single hyphens.",
                parameterName);
        }

        return value!;
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex Pattern();
}
