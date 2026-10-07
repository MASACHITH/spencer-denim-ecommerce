using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Domain.Content;

/// <summary>
/// What the Contact form will accept (GLB-10, doc 11 P15 step 5: "name, email, phone (optional), subject,
/// message, order number (optional)").
/// <para>
/// In the Domain because both ends need the same answer and because the column widths of doc 08 §3.7 are
/// the real constraint: a message of 4,001 characters is not a 500 from SQL Server, it is a named field
/// error. The Application handler turns each of these into one.
/// </para>
/// </summary>
public static class ContactEnquiryRules
{
    /// <summary>
    /// A name has to be something, and 120 characters is the column. No character class is imposed: Sri
    /// Lankan names are written in Sinhala, Tamil and Latin scripts, and a form that refuses a name is
    /// worse than one that stores an odd one.
    /// </summary>
    public const int NameMinLength = 1;

    /// <summary>
    /// The shortest message this form will take. Two characters, which rejects an empty submit and a
    /// stray keypress and nothing a person meant to send — a one-word enquiry ("sizing?") is real.
    /// </summary>
    public const int MessageMinLength = 2;

    /// <summary>
    /// Whether <paramref name="email"/> is an address this shop will store.
    /// <para>
    /// <b>Deliberately the newsletter's rule</b> (<see cref="NewsletterEmail"/>) rather than a second
    /// one. "Is this an email address" has one answer, and the first version of this file had its own
    /// copy, which is how two forms on the same site come to disagree about whether
    /// <c>name@localhost</c> is acceptable. The namespace is the only thing about
    /// <see cref="NewsletterEmail"/> that is marketing-specific; its rule is documented as structural
    /// rather than RFC-complete, which is the right strength for a form that cannot verify delivery —
    /// and here nothing is ever sent to the address at all (doc 11 P15: notifications are none in v1),
    /// so its only job is to be readable by the staff member who replies by hand.
    /// </para>
    /// </summary>
    public static bool IsPlausibleEmail(string? email) =>
        NewsletterEmail.TryNormalise(email, out _, out _);

    /// <summary>
    /// Whether <paramref name="phone"/> could be a telephone number: digits, with the punctuation people
    /// type them with, and at least seven of the digits.
    /// <para>
    /// Not a Sri Lankan-only pattern, because the shop ships island-wide and takes enquiries from abroad,
    /// and not a strict E.164, because a visitor typing "077 123 4567" has given a perfectly usable
    /// number. The column is <c>varchar(20)</c>, so the stored form is the typed one with whitespace
    /// collapsed.
    /// </para>
    /// </summary>
    public static bool IsPlausiblePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            // Optional (doc 11 P15 step 5), so blank is valid. The caller decides whether to store null.
            return true;
        }

        var digits = 0;

        foreach (var character in phone)
        {
            if (char.IsAsciiDigit(character))
            {
                digits++;
                continue;
            }

            if (character is not (' ' or '-' or '+' or '(' or ')' or '.' or '/'))
            {
                return false;
            }
        }

        return digits is >= 7 and <= 15;
    }

    /// <summary>
    /// Normalises a typed telephone number to what the <c>varchar(20)</c> column holds: runs of
    /// whitespace collapsed to one space, trimmed. <see langword="null"/> for a blank value, so an
    /// optional field left empty is a NULL rather than an empty string — two spellings of "not given" in
    /// one column is how a report ends up with a blank row that is not blank.
    /// </summary>
    public static string? NormalisePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var collapsed = string.Join(' ', phone.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length == 0
            ? null
            : Truncate(collapsed, ContactSubmission.PhoneMaxLength);
    }

    /// <summary>
    /// An optional free-text field as it is stored: trimmed, or <see langword="null"/> when blank.
    /// </summary>
    public static string? NormaliseOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Truncate(value.Trim(), maxLength);
    }

    /// <summary>
    /// An order number as the visitor typed it, upper-cased, because that is how
    /// <c>sales.Orders.OrderNumber</c> is written and a staff member searching for it should find it.
    /// Never resolved to an order: doc 11 P15 says the field exists so staff "find it quickly", and a
    /// mistyped number is still worth storing.
    /// </summary>
    public static string? NormaliseOrderNumber(string? orderNumber)
    {
        var trimmed = NormaliseOptional(orderNumber, ContactSubmission.OrderNumberMaxLength);

        return trimmed?.ToUpperInvariant();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
