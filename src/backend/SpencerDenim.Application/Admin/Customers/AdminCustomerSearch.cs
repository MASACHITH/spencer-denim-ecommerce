namespace SpencerDenim.Application.Admin.Customers;

/// <summary>
/// Classifies what a staff member typed into the customer search box (ADM-15).
/// </summary>
/// <remarks>
/// One box, three meanings, decided the way <c>AdminOrderSearch</c> decides them for the order list, so a
/// staff member who has learned one search box has learned both:
/// <list type="bullet">
/// <item>anything with an <c>@</c> is an email address, matched as a <b>prefix</b> of
/// <c>NormalizedEmail</c>: an address read aloud gets cut off at the end, not the start;</item>
/// <item>seven or more digits and nothing else but separators is a telephone number, matched on its last
/// nine digits so <c>0771234567</c> finds <c>+94771234567</c>;</item>
/// <item>everything else is part of a name.</item>
/// </list>
/// </remarks>
public static class AdminCustomerSearch
{
    /// <summary>The longest term honoured; longer input is cut, never refused (a search box is not a form).</summary>
    public const int MaxLength = 100;

    /// <summary>
    /// How many trailing digits of a telephone number are compared: a Sri Lankan number without its
    /// leading 0 or +94.
    /// </summary>
    public const int PhoneSuffixDigits = 9;

    /// <summary>Fewer digits than this is not a telephone number (an order number's sequence part is six).</summary>
    public const int MinimumPhoneDigits = 7;

    public static AdminCustomerSearchTerm? Classify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var term = raw.Trim();

        if (term.Length > MaxLength)
        {
            term = term[..MaxLength];
        }

        if (term.Contains('@', StringComparison.Ordinal))
        {
            // Upper-cased to compare with NormalizedEmail, which Identity stores upper-cased, so the
            // collation is not what decides whether two addresses are the same.
            return new AdminCustomerSearchTerm(AdminCustomerSearchKind.Email, term.ToUpperInvariant());
        }

        var digits = new string(term.Where(char.IsAsciiDigit).ToArray());

        if (digits.Length >= MinimumPhoneDigits && digits.Length == term.Count(char.IsAsciiLetterOrDigit))
        {
            return new AdminCustomerSearchTerm(
                AdminCustomerSearchKind.Phone,
                digits.Length > PhoneSuffixDigits ? digits[^PhoneSuffixDigits..] : digits);
        }

        return new AdminCustomerSearchTerm(AdminCustomerSearchKind.Name, term);
    }
}

/// <summary>What a search term was read as.</summary>
public enum AdminCustomerSearchKind
{
    Name = 0,
    Email = 1,
    Phone = 2,
}

/// <param name="Kind">How to match.</param>
/// <param name="Value">
/// For <see cref="AdminCustomerSearchKind.Email"/>, the upper-cased prefix; for
/// <see cref="AdminCustomerSearchKind.Phone"/>, the trailing digits; for
/// <see cref="AdminCustomerSearchKind.Name"/>, the trimmed text.
/// </param>
public sealed record AdminCustomerSearchTerm(AdminCustomerSearchKind Kind, string Value);
