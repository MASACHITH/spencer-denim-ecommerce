using System.Text.RegularExpressions;
using SpencerDenim.Application.Analytics;

namespace SpencerDenim.Application.Privacy;

/// <summary>
/// The path a <b>log</b> may print for a request (issue #201, follow-up to #179): the address with every part that
/// could be a credential replaced by <c>*</c>, so a log line can say <i>which kind</i> of page was asked for and
/// never <i>which order</i>.
/// <para>
/// <b>One rule, three implementations that must agree.</b> Which path segments are secret is decided by
/// <see cref="PagePath"/> (W4-03: what traffic recording may store) and this class adds the two things only a log
/// needs. The gateway writes its access log through the same rule (the <c>$sd_log_path</c> map in
/// <c>deploy/gateway/nginx.conf</c>, #179) and the storefront's SSR server prints its failures through
/// <c>server/log-path.ts</c>. The routes in <see cref="CredentialRoutes"/> and the numbers below are the same in all
/// three; <c>RequestPathRedactionTests</c> and <c>log-path.spec.ts</c> run one shared table of spellings
/// (<c>tests/Shared/log-path-vectors.json</c>) through the two implementations that are code, and
/// <c>GatewayLogMapParityTests</c> compares this class with the gateway's map once that file exists.
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Routes that carry a credential in the path are starred whatever follows</b> (<see cref="CredentialRoutes"/>).
/// The generic rule below recognises a token only by its <i>shape</i>, so a token whose shape changed would pass it;
/// naming the routes means it cannot. Matching is case-insensitive (the API's routing is), and <c>;</c> ends a
/// segment as <c>/</c> does (the Angular router ignores matrix parameters, so <c>/order;x=1/{token}</c> opens the
/// order page).
/// </description></item>
/// <item><description>
/// <b>Everything else is an allow-list</b> (what <see cref="PagePath"/> stores): the leading segments that are
/// <see cref="IsSafeLogSegment">safe slugs</see> are kept, up to <see cref="MaxSegments"/> deep; the first that is not,
/// and everything after it, becomes <c>*</c>. A route added later cannot leak by default, because whatever the rule
/// does not recognise is starred before anybody has thought about it. It is a net for shapes, not a proof about
/// tokens: a secret of fifteen lower-case letters would pass it, which is why rule 1 names routes.
/// </description></item>
/// </list>
/// <para>
/// <b>What a segment must be to be kept</b> is <see cref="PagePath.IsSafeSlug"/> (lower-case letters and digits
/// separated by single hyphens, at most 100 characters, not a GUID, no run of seven digits) <b>plus</b>, because a
/// log sees addresses nobody routed: not a GUID with its hyphens in odd places, and no word of
/// <see cref="LongWordLength"/> or more letters and digits, which a catalogue slug does not have and a random token
/// does. Segments are never percent-decoded here: the server has already decoded the path it logs, so a <c>%</c>
/// that is still there is a separator or a second round of encoding and is not a slug character.
/// </para>
/// <para>
/// The query string and fragment are cut first: a log of a path never has one, and a credential in a query string
/// (<c>?token=</c> on a reset or confirmation link, the PayPal return's <c>PayerID</c>) must not survive because a
/// caller passed a whole request target.
/// </para>
/// </summary>
public static partial class RequestPathRedaction
{
    /// <summary>What an unsafe segment, and everything after it, is printed as.</summary>
    public const string Redacted = PagePath.Redacted;

    /// <summary>How many leading slug segments are kept: the deepest API route is six, <c>/api/admin/orders/bank-transfers/{id}/slip</c>.</summary>
    public const int MaxSegments = 7;

    /// <summary>A run of this many letters and digits is a token, not a word in a slug (no hyphen in it).</summary>
    public const int LongWordLength = 16;

    /// <summary>
    /// The routes that carry a credential in the path, in the order they are tried. <see cref="CredentialRoute.Pattern"/>
    /// is the gateway's own regular expression for the route, character for character, so the two can be compared.
    /// </summary>
    public static IReadOnlyList<CredentialRoute> CredentialRoutes { get; } =
    [
        // The storefront's guest order link. The address IS the credential (docs/08 3.5 Orders.PublicId).
        // PagePath stores the same thing, /order/*.
        new("^/order(?:[;/]|$)", "/order/*"),

        // The order page's own API calls: view, payment reference, cancel, bank-slip upload and download.
        new("^/api/orders(?:[;/]|$)", "/api/orders/*"),

        // The PayPal round trip. The browser navigates to these with the order token in the path. The action is
        // kept as the request spelled it, like the gateway's map does.
        new("^/api/payments/paypal/(?<sd_a>start|return|cancel)(?:[;/]|$)", "/api/payments/paypal/${sd_a}/*"),

        // Staff views of an order. The id opens the customer's /order/{id} page, so it is a credential too. The
        // queue itself (/api/admin/orders/bank-transfers, with or without a trailing slash) stays readable.
        new("^/api/admin/orders/bank-transfers/[^/]", "/api/admin/orders/bank-transfers/*"),
        new("^/api/admin/orders/(?!bank-transfers(?:[;/]|$))[^/]", "/api/admin/orders/*"),
    ];

    /// <summary>
    /// <paramref name="path"/> as a log may print it. Never throws and never returns the input unchanged unless every
    /// segment of it is a safe slug.
    /// </summary>
    public static string ForLog(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        var cut = path.AsSpan().IndexOfAny('?', '#');
        var withoutQuery = cut >= 0 ? path[..cut] : path;

        if (withoutQuery.Length == 0)
        {
            return "/";
        }

        foreach (var route in CredentialRoutes)
        {
            var match = route.Compiled.Match(withoutQuery);

            if (match.Success)
            {
                return match.Result(route.Redacted);
            }
        }

        if (withoutQuery[0] != '/')
        {
            return "/*";
        }

        // path starts with "/", so the first element is the empty string before it.
        var segments = withoutQuery.Split('/');
        var kept = new List<string>(MaxSegments);

        for (var index = 1; index < segments.Length; index++)
        {
            var segment = segments[index];

            if (segment.Length == 0 && index == segments.Length - 1)
            {
                break;
            }

            if (kept.Count == MaxSegments || !IsSafeLogSegment(segment))
            {
                return (kept.Count == 0 ? string.Empty : Join(kept)) + "/" + Redacted;
            }

            kept.Add(segment);
        }

        return kept.Count == 0 ? "/" : Join(kept);
    }

    /// <summary>
    /// True when <paramref name="segment"/> is a catalogue-slug-shaped word that cannot be a secret: the
    /// <see cref="PagePath.IsSafeSlug"/> rule, and neither a GUID spelled with its hyphens anywhere nor a word of
    /// <see cref="LongWordLength"/> or more letters and digits.
    /// </summary>
    public static bool IsSafeLogSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        return PagePath.IsSafeSlug(segment) && !LooseGuid().IsMatch(segment) && !LongWord().IsMatch(segment);
    }

    private static string Join(List<string> kept) => "/" + string.Join('/', kept);

    /// <summary>The gateway's GUID test: eight, four, four, four and twelve hex digits with each hyphen optional.</summary>
    [GeneratedRegex("^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex LooseGuid();

    [GeneratedRegex("[a-z0-9]{16}", RegexOptions.CultureInvariant)]
    private static partial Regex LongWord();

    /// <summary>A route whose path holds a credential, and what a log prints in place of everything after its prefix.</summary>
    public sealed class CredentialRoute
    {
        internal CredentialRoute(string pattern, string redacted)
        {
            Pattern = pattern;
            Redacted = redacted;
            Compiled = new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                TimeSpan.FromSeconds(1));
        }

        /// <summary>The gateway's pattern for the route, matched case-insensitively against the decoded path.</summary>
        public string Pattern { get; }

        /// <summary>What is printed when it matches; <c>${sd_a}</c> is the gateway's captured action.</summary>
        public string Redacted { get; }

        internal Regex Compiled { get; }
    }
}
