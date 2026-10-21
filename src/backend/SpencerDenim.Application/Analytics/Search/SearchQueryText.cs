using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SpencerDenim.Application.Catalog.Search;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Domain.Common;

namespace SpencerDenim.Application.Analytics.Search;

/// <summary>
/// What a submitted search term becomes before it may be counted in <c>analytics.SearchQueriesDaily</c>, or
/// <see langword="null"/> when it must not be counted at all (doc 08 DB25; issue #78). Pure: the whole privacy
/// rule for the table in one place a unit test can hold.
/// <para>
/// <b>Normalised exactly as the storefront normalises it</b> (<c>normaliseTerm</c> in
/// <c>core/search/search-term.ts</c>): unpaired surrogates dropped, C0/C1 controls turned into a space, runs of
/// white space collapsed to one, trimmed, cut to <see cref="SearchQueryDaily.QueryMaxLength"/> characters never
/// through a surrogate pair — and then lower-cased, which DB25 adds ("normalised (lower-case, trimmed, spaces
/// collapsed) and truncated to 100 chars"). A term the search endpoint would refuse (shorter than two characters,
/// or with no token of two) is not a search anyone ran, and is not counted.
/// </para>
/// <para>
/// <b>Dropped, never stored</b> — the storefront's corrected <c>isPrivate</c> rule, which #78 requires the server
/// to inherit rather than the anchored first version, plus one rule of its own:
/// </para>
/// <list type="bullet">
/// <item><description><b>Any at-sign</b>, in any of the three spellings a keyboard produces (<c>@</c>, fullwidth
/// <c>\uFF20</c>, small <c>\uFE6B</c>). Not an email pattern: a shopper types a sentence with an address in it, and
/// "john @ example.com" is an address to a person and not to a pattern. Nothing a clothing shop is searched for
/// has an at-sign in it.</description></item>
/// <item><description><b>Seven or more decimal digits</b>, in any script, counted by code point and wherever
/// they are in the term — a telephone number however it is punctuated. This is stricter than doc 08's "≥ 7 digits
/// once separators are removed" (a run) and deliberately the same as the storefront's rule, so a term the browser
/// refuses to remember is never one the server keeps for 13 months. It costs "levis 501 32x34"; that is the
/// storefront's own trade, made once for both sides.</description></item>
/// <item><description><b>An order or invoice number</b> (<c>SD-2026-…</c>, <c>INV-2026-…</c>, with or without the
/// dashes), even a partial one with too few digits for the rule above: a shopper looking for their order types
/// its number into the first box they see.</description></item>
/// </list>
/// </summary>
public static partial class SearchQueryText
{
    /// <summary>The storefront's <c>MAX_DIGITS</c> is 6: seven or more is a telephone number.</summary>
    public const int MaxDigits = 6;

    /// <summary>
    /// The term to count, or <see langword="null"/> when there is nothing countable — empty, too short, or
    /// personal (see the type remarks).
    /// </summary>
    public static string? Normalise(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var kept = new StringBuilder(Math.Min(raw.Length, SearchQueryDaily.QueryMaxLength * 2));

        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < raw.Length && char.IsLowSurrogate(raw[index + 1]))
                {
                    kept.Append(character).Append(raw[index + 1]);
                    index++;
                }

                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                continue;
            }

            // Bidi controls ("Trojan Source") and invisible formatting characters carry no meaning in a search
            // term and are dropped outright, never turned into a space — they are zero-width (#78 tester pass).
            // ZWJ and ZWNJ (U+200C/U+200D), which Sinhala and Tamil conjuncts need (#189 D1), are deliberately not
            // in either set PlainText checks here.
            if (PlainText.IsBidiControl(character) || PlainText.IsInvisibleFormatting(character))
            {
                continue;
            }

            // C0 controls (NUL included), DEL and the C1 range: a space, so the words either side stay apart.
            kept.Append(character <= '\u001F' || character is >= '\u007F' and <= '\u009F' ? ' ' : character);
        }

        var collapsed = WhiteSpaceRun().Replace(kept.ToString(), " ").Trim().ToLowerInvariant();

        // The privacy rule runs on the WHOLE normalised term, before it is cut to the stored width (#78 tester
        // pass): an address or a phone number past the 100th character is still one, and checking only the
        // truncated string let "<88 spaces>john.perera@example.com" through, stored as "…aaaa john.perera".
        if (IsPersonal(collapsed))
        {
            return null;
        }

        var term = Truncate(collapsed);

        if (term.Length < SearchTerms.MinTermLength || SearchTerms.Tokenise(term).Count == 0)
        {
            return null;
        }

        return term;
    }

    /// <summary>
    /// True when the term carries an at-sign, seven or more decimal digits, or an order or invoice number. Public so
    /// the admin hide list can refuse to be handed one.
    /// </summary>
    public static bool IsPersonal(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        if (term.AsSpan().IndexOfAny('@', '\uFF20', '\uFE6B') >= 0)
        {
            return true;
        }

        // By code point, not UTF-16 unit, so a digit outside the basic plane counts once (the storefront's own
        // Osmanya lesson).
        var digits = 0;
        foreach (var rune in term.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber && ++digits > MaxDigits)
            {
                return true;
            }
        }

        return DocumentNumberShape().IsMatch(term);
    }

    /// <summary>Cut to the column's width, never through a surrogate pair, and with no trailing space.</summary>
    private static string Truncate(string value)
    {
        if (value.Length <= SearchQueryDaily.QueryMaxLength)
        {
            return value;
        }

        var cut = value[..SearchQueryDaily.QueryMaxLength];
        if (char.IsHighSurrogate(cut[^1]))
        {
            cut = cut[..^1];
        }

        return cut.TrimEnd();
    }

    /// <summary>The storefront's <c>/\s+/gu</c>: any Unicode white space.</summary>
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpaceRun();

    /// <summary>
    /// <c>SD-2026-000123</c> and <c>INV-2026-000045</c> (<c>DocumentNumber</c>), with or without the separators and
    /// with any number of sequence digits, anywhere in the term.
    /// </summary>
    [GeneratedRegex(@"\b(?:sd|inv)[\s\-_/]*\d{4}[\s\-_/]*\d+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DocumentNumberShape();
}
