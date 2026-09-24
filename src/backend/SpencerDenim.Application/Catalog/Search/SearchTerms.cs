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
