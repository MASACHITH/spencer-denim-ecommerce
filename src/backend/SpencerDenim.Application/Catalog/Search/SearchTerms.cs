using System.Text;

namespace SpencerDenim.Application.Catalog.Search;

/// <summary>
/// Turns a raw search box value into the tokens the <c>LIKE</c> search uses (doc 08 §7.6):
/// normalise, escape <c>%</c>, <c>_</c> and <c>[</c>, and require all of up to five tokens to match.
/// <para>
/// The escape character is also escaped, otherwise a query ending in a backslash would produce a
/// malformed pattern and SQL Server would reject the batch.
/// </para>
/// </summary>
public static class SearchTerms
{
    /// <summary>Doc 08 §7.6: "all of up to 5 tokens must match".</summary>
    public const int MaxTokens = 5;

    /// <summary>Shortest useful term. One character matches almost everything.</summary>
    public const int MinTermLength = 2;

    /// <summary>Longest term accepted, so the pattern stays inside the indexed column's width.</summary>
    public const int MaxTermLength = 100;

    /// <summary>The <c>ESCAPE</c> character used in every generated <c>LIKE</c> pattern.</summary>
    public const char EscapeCharacter = '\\';

    /// <summary>
    /// Splits the raw term on whitespace, lower-cases it (the column is stored lower-cased), drops
    /// tokens that are too short, removes duplicates and keeps at most <see cref="MaxTokens"/>.
    /// </summary>
    public static IReadOnlyList<string> Tokenise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxTermLength)
        {
            trimmed = trimmed[..MaxTermLength];
        }

        var tokens = new List<string>(MaxTokens);
        foreach (var part in trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = part.ToLowerInvariant();
            if (token.Length < MinTermLength || tokens.Contains(token, StringComparer.Ordinal))
            {
                continue;
            }

            tokens.Add(token);
            if (tokens.Count == MaxTokens)
            {
                break;
            }
        }

        return tokens.AsReadOnly();
    }

    /// <summary>
    /// True when <paramref name="raw"/> holds more distinct tokens of at least <see cref="MinTermLength"/>
    /// characters than <see cref="Tokenise"/> keeps.
    /// <para>
    /// <b>Why this exists (issue #78 tester pass).</b> The search itself is allowed to drop anything past
    /// <see cref="MaxTokens"/> — doc 08 §7.6 says so, and a shopper who pastes a six-word search still sees results
    /// for the first five. But a caller deciding whether the <i>whole</i> term may be credited with "having
    /// results", for the popular-searches list, cannot make the same allowance: five overlapping substrings of a
    /// real word ("li in ne en lin", all five inside "linen") plus one unrelated sixth word passed the search
    /// (which only ever looked at the first five) and let the sixth word ride onto the public list unverified.
    /// This answers "could the search have missed something in this term", not "what does the search check" — the
    /// two are the same question only while the term has five tokens or fewer.
    /// </para>
    /// </summary>
    public static bool HasUncheckedTokens(string? raw) => CountDistinctTokens(raw) > MaxTokens;

    /// <summary>
    /// <see cref="Tokenise"/>'s own split, filter and de-duplication, but counting every token rather than
    /// stopping at <see cref="MaxTokens"/>. A count, not the tokens themselves: nothing here needs to see them.
    /// </summary>
    private static int CountDistinctTokens(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxTermLength)
        {
            trimmed = trimmed[..MaxTermLength];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = part.ToLowerInvariant();
            if (token.Length >= MinTermLength)
            {
                seen.Add(token);
            }
        }

        return seen.Count;
    }

    /// <summary>
    /// Escapes the LIKE wildcards in a token so a customer typing <c>50%</c> searches for the text
    /// "50%" instead of matching everything.
    /// </summary>
    public static string EscapeLikeValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length + 4);
        foreach (var character in value)
        {
            if (character is '%' or '_' or '[' or EscapeCharacter)
            {
                builder.Append(EscapeCharacter);
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>A "contains" pattern: <c>%token%</c>, with the token's wildcards escaped.</summary>
    public static string ToContainsPattern(string token) => $"%{EscapeLikeValue(token)}%";

    /// <summary>A "starts with" pattern: <c>token%</c>. Used for the name-prefix relevance bonus.</summary>
    public static string ToPrefixPattern(string token) => $"{EscapeLikeValue(token)}%";

    /// <summary>A "contains a word starting with" pattern: <c>% token%</c>.</summary>
    public static string ToWordPrefixPattern(string token) => $"% {EscapeLikeValue(token)}%";
}
