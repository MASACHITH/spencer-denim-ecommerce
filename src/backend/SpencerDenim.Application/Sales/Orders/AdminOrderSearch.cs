using System.Globalization;
using System.Text.RegularExpressions;
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
public static partial class AdminOrderSearch
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

        // **The canonical document-number form, computed before anything else is decided.** The series
        // writes `SD-2026-000123`; a staff member reads it off a screen and types `SD 2026 000123`, or
        // quotes the part after the prefix as `2026-000123`. Both used to miss — the spaced one matched no
        // stored value because the stored one has dashes, and the prefix-less one is ten digits and so was
        // read as a telephone number. Both were found in review, and both are exactly what the one search
        // box exists to catch.
        var canonical = Separators().Replace(term.ToUpperInvariant(), "-").Trim('-');

        if (DocumentNumberShape().IsMatch(canonical))
        {
            // With a prefix, the stored value starts with what was typed, so a seekable prefix match does
            // it. Without one, the stored value *ends* with it — see SequenceSuffix for what that costs.
            var hasPrefix = char.IsAsciiLetter(canonical[0]);

            return new AdminOrderSearchTerm(
                AdminOrderSearchKind.NumberOrName,
                canonical,
                hasPrefix ? null : canonical);
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

    /// <summary>
    /// Runs of the things people type between a document number's parts, collapsed to the one the series
    /// actually writes.
    /// </summary>
    /// <remarks>
    /// Spaces, underscores, en dashes and hyphens. <b>Not a full stop</b>, which would fold an email
    /// address's domain, and <b>not a plus</b>, which is how a telephone number starts.
    /// </remarks>
    [GeneratedRegex(@"[\s_\-\u2013\u2014]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    /// <summary>
    /// A document number, or the start of one: an optional letter prefix, a four-digit year, and the
    /// sequence.
    /// </summary>
    /// <remarks>
    /// <b>The year group is what tells a document number from a telephone number, and the ambiguity it
    /// leaves is worth naming.</b> `2026-000123` is ten digits; so is a mobile number. What separates them
    /// here is the <i>separator</i> plus a leading `19`/`20`, so `2026-000123` is a document number and
    /// `0771234567` is a telephone number. A bare `2026000123` with no separator at all is still read as a
    /// telephone number, and that is a deliberate loss: nothing in the term distinguishes it, and guessing
    /// wrong in the other direction would make every ten-digit telephone search scan the order-number
    /// index for nothing.
    /// <para>
    /// The sequence is <c>1,8</c> rather than exactly six, because a prefix of a number is a legitimate
    /// thing to type and <c>DocumentNumber.SequenceDigits</c> is a minimum width rather than a cap.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(?:[A-Z]{2,4}-)?(?:19|20)[0-9]{2}-[0-9]{1,8}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DocumentNumberShape();
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
/// The tail of the document number this term identifies, when the term is not a prefix of one.
/// <para>
/// Two shapes reach it. A digits-only term short enough to be a sequence number is zero-padded to the
/// series' width, so "123" finds <c>SD-2026-000123</c>. A term that carries the year but not the letter
/// prefix is itself the tail, so "2026-000123" finds the same order — which it did not before, because ten
/// digits read as a telephone number.
/// </para>
/// <para>
/// <see langword="null"/> otherwise, and a reader must not invent one: padding an arbitrary string would
/// match orders at random. A suffix cannot be seeked — see the reader's remarks for what that costs and why
/// it is still the right trade.
/// </para>
/// </param>
public sealed record AdminOrderSearchTerm(
    AdminOrderSearchKind Kind,
    string Value,
    string? SequenceSuffix)
{
    // **There was a `LikeEscaped()` and a `SequenceSuffixEscaped()` here, and both have gone.**
    //
    // They were dead — `AdminOrderReader.Filter` passes the raw value to `StartsWith` / `EndsWith` /
    // `Contains` and never called either — and the comments justifying them stated a falsehood about EF:
    // that it translates `EndsWith` to `LIKE '%' + @p` *without* escaping the parameter. The tester
    // measured EF 10.0.12's actual output with `ToQueryString()`:
    //
    //     DECLARE @text_startswith varchar(20) = 'SD-\%%';
    //     WHERE [o].[OrderNumber] LIKE @text_startswith ESCAPE '\'
    //
    // EF escapes the value itself, at run time, and emits an explicit `ESCAPE`. So the behaviour was
    // always right, the reasoning was wrong, and *wiring the helpers up as those comments implied* would
    // have double-escaped every term — a search for `100%` would then have matched nothing.
    //
    // The guarantee still holds and is still pinned, by the one test that can see it:
    // `AdminOrderEndpointTests.A_wildcard_in_the_search_box_is_not_a_wildcard` asserts on the wire, against
    // real SQL Server, that `%` and `_` find nothing. Two unit tests over seven cases asserted a method
    // production never called, which is the "test that cannot fail" shape wearing a useful-looking hat.
    //
    // The lesson worth keeping: my own grep sweep at the end of this task checked every *schema* claim I
    // had made against the configuration, and caught five. It did not check *framework* claims, and this
    // is the one it missed.

    /// <summary>The term upper-cased with the invariant culture, for a case-insensitive name comparison.</summary>
    public string Upper() => Value.ToUpperInvariant();

    /// <summary>Formatted for an audit or log line, never for a query.</summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Kind}:{Value}");
}
