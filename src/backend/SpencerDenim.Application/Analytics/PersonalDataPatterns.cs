using System.Text.RegularExpressions;

namespace SpencerDenim.Application.Analytics;

/// <summary>
/// The two shapes of personal data that turn up where a shop never asked for them: an email address and a
/// telephone number. Doc 08 DB25 drops a search query that "matches an email pattern or contains ≥ 7 digits
/// once separators are removed"; traffic recording applies the same test to every value it would otherwise
/// keep from a URL — a path segment, a filter value, a UTM tag — because a mail tool that personalises its
/// links (<c>utm_campaign=jane@example.com</c>) and a shopper who types a phone number into a search box
/// both put one there.
/// </summary>
public static partial class PersonalDataPatterns
{
    /// <summary>The run of digits, separators removed, that DB25 treats as a telephone number.</summary>
    public const int PhoneDigitRun = 7;

    /// <summary>
    /// True when <paramref name="value"/> contains something that looks like an email address or a
    /// telephone number.
    /// <para>
    /// <b>"≥ 7 digits once separators are removed" is read as a run</b>: separators (<see cref="IsSeparator"/>: any space or dash, dots, brackets,
    /// slashes, plus signs, underscores and commas) are taken out and the longest run of consecutive digits is measured. So
    /// <c>077 123 4567</c> and <c>+94-77-123-4567</c> are phone numbers, and <c>levis 501 32x34</c> (runs of
    /// 5 and 2 once the spaces go) is not — a reading that counted every digit in the string would drop
    /// ordinary searches for a jeans size.
    /// </para>
    /// </summary>
    public static bool LooksPersonal(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value.Contains('@', StringComparison.Ordinal) || HasPhoneDigitRun(value);
    }

    /// <summary>True when the longest run of digits, with separators removed, is at least <see cref="PhoneDigitRun"/>.</summary>
    public static bool HasPhoneDigitRun(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var run = 0;

        foreach (var character in value)
        {
            if (char.IsDigit(character))
            {
                run++;

                if (run >= PhoneDigitRun)
                {
                    return true;
                }
            }
            else if (!IsSeparator(character))
            {
                run = 0;
            }
        }

        return false;
    }

    /// <summary>
    /// An address-shaped string anywhere in the value. Wider than "contains @" only in name: an <c>@</c> on
    /// its own is already enough for <see cref="LooksPersonal"/>, and this exists for the search statistics,
    /// which DB25 specifies by pattern.
    /// </summary>
    public static bool ContainsEmail(string value) => EmailPattern().IsMatch(value);

    /// <summary>
    /// What a person or a tool puts between the digits of a telephone number. Not only the ASCII space and hyphen:
    /// an underscore or a comma (a mail tool's merge field, a campaign naming scheme), any Unicode space or dash (a
    /// non-breaking hyphen or an en dash pasted from a document), brackets and dots. W3-07 found numbers stored
    /// because a rule knew too few separators; and a digit is any decimal digit, full-width ones included, so
    /// <c>０７７１２３４５６７</c> is the number it looks like (W4-03 tester).
    /// </summary>
    private static bool IsSeparator(char character) =>
        character is '.' or '(' or ')' or '[' or ']' or '/' or '+' or '_' or ','
        || char.IsWhiteSpace(character)
        || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.DashPunctuation;

    [GeneratedRegex(@"[^\s@]+@[^\s@]+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();
}
