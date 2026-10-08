using System.Globalization;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Orders;

/// <summary>
/// What a staff member typed into the order list's one search box, and which column it should be matched
/// against (ADM-09).
/// </summary>
/// <remarks>
/// <b>One box and not four, because the staff member is on the telephone.</b> Doc 11 §6.6 is a whole
/// section about a customer ringing up about an order: what they have is the order number, or their email
/// address, or their telephone number, and asking which of those they are about to say is a question
/// nobody can answer. So the term is classified here and the reader turns the classification into exactly
/// one predicate.
/// <para>
/// <b>The classification is in the Application layer and has no EF in it</b>, so it is unit-testable
/// without a database and the reader cannot quietly disagree with it. Which index each kind can use is
/// recorded on the member.
/// </para>
/// </remarks>
public static class AdminOrderSearch
{
    /// <summary>
    /// The longest term accepted. Anything longer is truncated rather than refused — the box is a search,
    /// not a form field, and a paste of half a bank statement should find nothing rather than 400.
    /// </summary>
    public const int MaxLength = 100;

    /// <summary>
    /// How many digits an order number's sequence part carries, so a staff member who types only the
    /// digits can still be matched. <c>SD-2026-000123</c> → six.
    /// <para>
    /// <b>The Domain's own constant, not a copy of it.</b> It is the minimum width
    /// <see cref="DocumentNumber.Format"/> pads to, so a second 6 written here would be a search that
    /// stopped finding orders the day the series widened.
    /// </para>
    /// </summary>
    private const int SequenceDigits = DocumentNumber.SequenceDigits;

    /// <summary>Classifies <paramref name="raw"/>, or answers <see langword="null"/> for nothing to search.</summary>
    public static AdminOrderSearchTerm? Classify(string? raw)
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

        // An email address, which is the second thing a caller offers. The order stores an upper-cased
        // EmailNormalized under IX_Orders_EmailNormalized precisely so the database collation is not what
        // decides whether two addresses are the same (doc 08 §3.5), so the term is upper-cased here and
        // compared to that column. A prefix match, because somebody reading an address aloud gets cut off,
        // and a prefix is seekable where a contains is not.
        if (term.Contains('@', StringComparison.Ordinal))
        {
            return new AdminOrderSearchTerm(
                AdminOrderSearchKind.Email,
                term.ToUpperInvariant(),
                null);
        }

        var digits = new string(term.Where(char.IsAsciiDigit).ToArray());

        // Only digits, and enough of them to be a telephone number rather than part of an order number.
        // Sri Lankan numbers are nine or ten digits; seven is the shortest thing worth treating as one,
        // and an order number's sequence part is six — so the boundary is not arbitrary, it is the point
        // where the two cannot be confused. Matched as a *suffix* because a caller may or may not say the
        // leading zero or the +94 (IX_Orders_Phone cannot seek a suffix; see the reader's remarks).
        if (digits.Length >= 7 && digits.Length == term.Count(char.IsAsciiLetterOrDigit))
        {
            return new AdminOrderSearchTerm(AdminOrderSearchKind.Phone, digits, null);
        }

        // Everything else is treated as a document number first, because that is what ADM-09 asks for and
        // what a customer quotes. Two forms are accepted:
        //
        //   * the whole thing, or its start — "SD-2026-0001", "INV-2026-000045". A prefix, so
        //     UX_Orders_OrderNumber and UX_Orders_InvoiceNumber can both seek it.
        //   * the digits alone — "123", because somebody reading a number off a screen says the part that
        //     changes. Those are padded to the series' six digits and matched as a suffix, which no index
        //     can seek; IX_Orders_Placed includes OrderNumber, so it is an index scan rather than a table
        //     one. A deliberate trade: the alternative is failing to find the order the customer is
        //     holding, which is the whole reason this screen exists.
        //
        // A term with letters in it that matches no document number still falls through to the name match
        // below, because "Perera" is a legitimate thing to type.
        var suffix = digits.Length is > 0 and <= SequenceDigits && digits.Length == term.Length
            ? digits.PadLeft(SequenceDigits, '0')
            : null;

        return new AdminOrderSearchTerm(AdminOrderSearchKind.NumberOrName, term, suffix);
    }

    /// <summary>
    /// The prefix an order number carries, for a screen that wants to show the staff member what shape to
    /// type. Exposed from the Domain's own constant so the two cannot drift.
    /// </summary>
    public static string OrderNumberPrefix => DocumentNumber.OrderNumberPrefix;
}

/// <summary>Which column a term should be matched against.</summary>
public enum AdminOrderSearchKind
{
    /// <summary>
    /// An order or invoice number, or a customer's name. One kind rather than two because a term with
    /// letters in it can be either and the reader matches both in one predicate — the alternative is
    /// asking the staff member to tell us which they typed.
    /// </summary>
    NumberOrName = 0,

    /// <summary>An email address: matched against <c>EmailNormalized</c> as a prefix.</summary>
    Email = 1,

    /// <summary>A telephone number: matched against <c>Phone</c> as a suffix, digits only.</summary>
    Phone = 2,
}

/// <summary>
/// A classified search term.
/// </summary>
/// <param name="Kind">Which column to match.</param>
/// <param name="Value">
/// The term as the reader should use it: trimmed, truncated, upper-cased for an email, digits only for a
/// telephone number.
/// </param>
/// <param name="SequenceSuffix">
/// For a digits-only term short enough to be a sequence number, the zero-padded six digits an order or
/// invoice number ends with — so "123" finds <c>SD-2026-000123</c>. <see langword="null"/> otherwise, and
/// a reader must not invent one: padding an arbitrary string would match orders at random.
/// </param>
public sealed record AdminOrderSearchTerm(
    AdminOrderSearchKind Kind,
    string Value,
    string? SequenceSuffix)
{
    /// <summary>
    /// The term with SQL <c>LIKE</c>'s own wildcards escaped, for the one predicate that cannot be
    /// expressed without <c>LIKE</c>.
    /// </summary>
    /// <remarks>
    /// <b>It exists because <c>%</c> in a search box is otherwise a free table scan</b> — and, with
    /// <c>[</c> and <c>_</c>, a way to match rows the typist did not mean. EF translates
    /// <c>EndsWith</c> to <c>LIKE '%' + @p</c> and does <i>not</i> escape the parameter, so a term is
    /// escaped here and the <c>LIKE</c> is written out with an explicit <c>ESCAPE</c> clause. Backslash is
    /// the escape character and is therefore escaped first.
    /// </remarks>
    public string LikeEscaped() => Value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal);

    /// <summary>
    /// The sequence suffix, escaped the same way.
    /// </summary>
    /// <remarks>
    /// It is all ASCII digits by construction, so there is nothing for the escaper to find — and it is
    /// escaped anyway, because "this value can never contain a wildcard" is exactly the kind of comment
    /// that stops being true without anybody noticing. A wrong comment is worse than none.
    /// </remarks>
    public string? SequenceSuffixEscaped() => SequenceSuffix is null
        ? null
        : SequenceSuffix.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);

    /// <summary>The term upper-cased with the invariant culture, for a case-insensitive name comparison.</summary>
    public string Upper() => Value.ToUpperInvariant();

    /// <summary>Formatted for an audit or log line, never for a query.</summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Kind}:{Value}");
}
