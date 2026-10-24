using System.Text.RegularExpressions;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.UnitTests.Infrastructure.Seeding;

/// <summary>One thing the copy lint found: the rule that found it, and what it found.</summary>
internal sealed record LintViolation(string Rule, string Detail);

/// <summary>
/// The mechanical half of reviewing the About, Privacy and Terms drafts (issue #252): the failures a drafted page can
/// have that no semantic test notices because nothing crashes.
/// <para>
/// <b>Why this is a function of a string, not a set of assertions on the seeded constants.</b> The tester for #252
/// ran six mutations against the first version of these tests and all six survived: a sentence saying the shop may
/// sell personal data to partners, a courier given the customer's email and purchase history, "trading since 1998",
/// "more than 10,000 happy customers", a fourth cookie, and an American spelling. A guard is only worth having if
/// it can be shown to go red, so the rules live here, take any body, and
/// <see cref="StorePagesCopyTests.Each_mutation_the_tester_ran_is_caught_by_the_lint_rule_meant_to_catch_it"/> feeds
/// them each of those six edits in memory and asserts the right rule fires. The real copy must produce none.
/// </para>
/// <para>
/// The rules are block-lists and allow-lists, and are honest about being that: they catch the careless edit, not a
/// determined one. They exist because the careless edit (a number, a year, a "best", a US spelling, a cookie nobody
/// set) is the realistic one in a page the owner edits in a text box.
/// </para>
/// </summary>
internal static partial class StorePagesLint
{
    public const string ForbiddenPhrase = "forbidden-phrase";
    public const string UnlistedNumber = "unlisted-number";
    public const string CookieName = "cookie-name";
    public const string UsSpelling = "us-spelling";
    public const string PinnedSentence = "pinned-sentence";

    /// <summary>
    /// Words and phrases that make a promise, a boast or a trade the shop has not agreed to. A match is an error
    /// unless it sits inside one of <see cref="PermittedFragments"/>, which are removed before the search. So
    /// "We do not sell your personal data" passes, and "We may sell your personal data to partners" does not.
    /// </summary>
    private static readonly string[] ForbiddenPatterns =
    [
        @"\bsell(s|ing)?\b", @"\bsold\b", @"\brent(s|ed|ing)?\b", @"\btrad(e|es|ed|ing)\b", @"\bpartners?\b",
        @"\bthird[- ]part(y|ies)\b", @"\baffiliates?\b", @"\badvertis\w*", @"\bmarketing\b", @"\bprofil(e|es|ing)\b",
        @"\bguarantee\w*", @"\balways\b", @"\bnever\b", @"\bbest\b", @"\bleading\b", @"\bhappy\b", @"\bpremium\b",
        @"\bsince\b", @"\bestablished\b", @"\bfounded\b", @"\baward\w*", @"\bfamily[- ](owned|run|business)\b",
        @"\bhandcrafted\b", @"\bnumber one\b", @"\bworld[- ]class\b", @"\bfree (delivery|shipping|returns)\b",
        @"\bunsubscribe\w*", @"\bwithin \d+ (hours?|days?)\b",
    ];

    /// <summary>
    /// The exact wording in which a forbidden word is allowed, each with the reason. "Sold out" is the colour and size
    /// marker; "We sell clothing, footwear and accessories" is what a shop does; "never" appears only where the code backs it (a review never shows an email address, the card is never
    /// seen, a password is never readable); "our best estimate" is how delivery times are described; "none for
    /// advertising" and "no third-party analytics" are the cookie statements the privacy page makes; "We do not sell" is the promise on the owner's list. Anything else that
    /// uses these words is a change somebody has to look at.
    /// </summary>
    private static readonly string[] PermittedFragments =
    [
        "sold out",
        "We sell clothing, footwear and accessories",
        "We do not sell your personal data",
        "none for advertising",
        "no third-party analytics",
        "never as readable text",
        "We never show your email address",
        "We never see or store your card details",
        "our best estimate",
    ];

    /// <summary>
    /// The integers each page may contain, as the text prints them. Anything else is a figure nobody confirmed:
    /// a year, a count of customers, a retention period. The Privacy policy's are the Act's number and year, the cookie
    /// lifetimes (30 days, 30 minutes) and the standard retention for visit records (90 days), each pinned elsewhere
    /// to the constant in code or setting that decides it.
    /// </summary>
    private static readonly Dictionary<string, string[]> PermittedNumbers = new(StringComparer.OrdinalIgnoreCase)
    {
        [ContentPageKeys.About] = [],
        [ContentPageKeys.Privacy] = ["9", "2022", "30", "90"],
        [ContentPageKeys.Terms] = [],
    };

    /// <summary>
    /// Sentences the Privacy policy makes about who gets what, which are promises about how the shop behaves. Each must
    /// appear exactly, so widening one (a courier given an email address and a purchase history) fails here and has to
    /// be argued for, and the owner's list of promises is updated with it.
    /// </summary>
    private static readonly Dictionary<string, string[]> PinnedSentences = new(StringComparer.OrdinalIgnoreCase)
    {
        [ContentPageKeys.Privacy] =
        [
            "We give the courier that delivers your parcel your name, delivery address and telephone number.",
            "It sends the order and account emails we address to you.",
            "They use separate sign-ins and see your data only to do their work.",
            "We do not sell your personal data. We may disclose it where the law requires us to.",
        ],
    };

    /// <summary>
    /// American spellings of words the shop writes the British way (the shop is Sri Lankan, and every other page of it
    /// says colour, organised, jewellery, cancelled). The <c>-ize</c> pattern catches the family; the explicit list is
    /// the words that are not.
    /// </summary>
    private static readonly string[] UsSpellingPatterns =
    [
        @"\b[a-z]{3,}iz(e|es|ed|ing|ation|ations)\b", @"\bcolors?\b", @"\bcenters?\b", @"\bfavorites?\b",
        @"\bprograms?\b", @"\bcatalogs?\b", @"\bbehaviors?\b", @"\bcanceled\b", @"\bcanceling\b", @"\btraveling\b",
        @"\blabeled\b", @"\bjewelry\b", @"\bgray\b", @"\bdefense\b", @"\boffense\b", @"\bhonors?\b", @"\bneighbors?\b",
        @"\bflavors?\b", @"\bfulfillment\b", @"\benrollment\b", @"\binstallments?\b", @"\banalyz\w*", @"\blicense[sd]?\b",
        @"\bmom\b", @"\bpajamas\b", @"\bsweater\b",
    ];

    /// <summary>Words that end in <c>-ize</c> and are spelled so in British English too.</summary>
    private static readonly string[] IzeWordsThatAreFine = ["prize", "prizes", "seize", "capsize", "baptize"];

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"\d[\d,.]*\d|\d")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\bsd_[a-z0-9_]+")]
    private static partial Regex CookiePattern();

    /// <summary>What a reader reads: tags removed, whitespace collapsed.</summary>
    public static string TextOf(string html) =>
        WhitespacePattern().Replace(TagPattern().Replace(html, " "), " ").Trim();

    /// <summary>
    /// Everything this lint has to say about one page's body. <paramref name="realCookieNames"/> are the cookie names
    /// the code sets, read from the constants in code by the caller.
    /// </summary>
    public static IReadOnlyList<LintViolation> Check(string key, string html, IReadOnlySet<string> realCookieNames)
    {
        var violations = new List<LintViolation>();
        var text = TextOf(html);

        // 1. Forbidden words, after the permitted wordings have been taken out.
        var remainder = text;
        foreach (var fragment in PermittedFragments)
        {
            remainder = remainder.Replace(fragment, " ", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var pattern in ForbiddenPatterns)
        {
            foreach (Match match in Regex.Matches(remainder, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                violations.Add(new LintViolation(ForbiddenPhrase, $"\"{match.Value}\" is not permitted outside its approved wording"));
            }
        }

        // 2. Numbers: every figure must be one the page is allowed to print; no percentage anywhere.
        var permitted = PermittedNumbers.TryGetValue(key, out var numbers) ? numbers : [];
        foreach (Match match in NumberPattern().Matches(text))
        {
            if (!permitted.Contains(match.Value, StringComparer.Ordinal))
            {
                violations.Add(new LintViolation(UnlistedNumber, $"\"{match.Value}\" is not a number this page is allowed to print"));
            }
        }

        if (text.Contains('%', StringComparison.Ordinal) || Regex.IsMatch(text, @"per\s*cent", RegexOptions.IgnoreCase))
        {
            violations.Add(new LintViolation(UnlistedNumber, "a percentage is not permitted"));
        }

        // 3. Cookies: every sd_ name must be a real one, and the Privacy policy must name all of them.
        var named = CookiePattern().Matches(text).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var name in named.Where(name => !realCookieNames.Contains(name)))
        {
            violations.Add(new LintViolation(CookieName, $"\"{name}\" is not a cookie the code sets"));
        }

        if (string.Equals(key, ContentPageKeys.Privacy, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var name in realCookieNames.Where(name => !named.Contains(name)))
            {
                violations.Add(new LintViolation(CookieName, $"the code sets \"{name}\" and the Privacy policy does not name it"));
            }
        }
        else if (named.Count > 0)
        {
            violations.Add(new LintViolation(CookieName, "only the Privacy policy may name a cookie"));
        }

        // 4. American spelling.
        foreach (var pattern in UsSpellingPatterns)
        {
            foreach (Match match in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (IzeWordsThatAreFine.Contains(match.Value, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                violations.Add(new LintViolation(UsSpelling, $"\"{match.Value}\" is an American spelling; the shop writes British English"));
            }
        }

        // 5. Pinned promise sentences.
        if (PinnedSentences.TryGetValue(key, out var pinned))
        {
            foreach (var sentence in pinned.Where(sentence => !text.Contains(sentence, StringComparison.Ordinal)))
            {
                violations.Add(new LintViolation(PinnedSentence, $"the promise \"{sentence}\" is missing or has been reworded"));
            }
        }

        return violations;
    }
}
