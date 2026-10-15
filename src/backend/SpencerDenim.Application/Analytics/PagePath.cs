using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.Application.Analytics;

/// <summary>A storefront address reduced to what traffic recording may keep.</summary>
/// <param name="Path">What is stored in <c>PageViews.Path</c>: the safe path plus the whitelisted filter keys.</param>
/// <param name="PageType">
/// The page family from the route shape. <see cref="PageType.Department"/> on a single segment and
/// <see cref="PageType.Category"/> on two or three are <i>provisional</i>: only the catalogue knows whether
/// <c>/women</c> is a department or <c>/about</c> a content page, so the writer resolves them.
/// </param>
/// <param name="Segments">The safe segments, in order; an unsafe one is <c>*</c>.</param>
/// <param name="Campaign">The UTM tags and click-id flags, read before the query string was dropped.</param>
public sealed record NormalisedPage(
    string Path,
    PageType PageType,
    IReadOnlyList<string> Segments,
    CampaignTags Campaign);

/// <summary>
/// Turns the address a shopper's browser was on into the <c>Path</c> doc 08 §3.8 allows to be stored:
/// "query string dropped except whitelisted filter keys".
/// <para>
/// <b>Why the path itself is filtered too, and not only the query string.</b> The order success page and a
/// guest's order link, <c>/order/{token}</c>, carry a secret in the <i>path</i>: the token is the credential
/// (doc 08 §3.5 <c>Orders.PublicId</c>), and whoever holds it can read the order, the name, the address and
/// the phone number. So:
/// </para>
/// <list type="bullet">
/// <item><description><c>/order/…</c> is always stored as <c>/order/*</c>, whatever follows.</description></item>
/// <item><description>
/// Every other segment is kept only if it is a catalogue slug (lower-case letters and digits separated by
/// single hyphens, at most 100 characters) that is not a GUID and has no run of seven digits. Anything else
/// becomes <c>*</c>. That is a rule about what is <i>allowed</i>, not a list of what is forbidden, so an
/// address shape added later cannot leak by default: a token, an id, an email (an <c>@</c> is not a slug
/// character) or a phone number in a future route is starred before anybody has thought about it.
/// </description></item>
/// <item><description>
/// The query string keeps only the listing's filter and sort keys (<see cref="FilterKeys"/>), on listing and
/// search pages, and only values of the shape each key takes. <c>?q=</c> (search), <c>?token=</c> (password
/// reset and email confirmation), <c>?email=</c>, <c>?returnTo=</c>, <c>?paypal=</c>, <c>?pages=</c> and
/// anything an advertiser appends are all dropped. The fragment is dropped.
/// </description></item>
/// </list>
/// <para>
/// Returns <see langword="null"/> for something that is not a storefront path at all (empty, not rooted,
/// protocol-relative, a backslash, more than five segments, too long): such a view is not recorded.
/// </para>
/// </summary>
public static partial class PagePath
{
    /// <summary>The longest raw address accepted, before any reduction.</summary>
    public const int MaxRawLength = 2048;

    /// <summary>The storefront's deepest route is three segments (<c>/kids/girls/girls-dresses</c>).</summary>
    public const int MaxSegments = 5;

    /// <summary><c>catalog.*.Slug</c> is <c>varchar(100)</c>.</summary>
    public const int MaxSegmentLength = 100;

    /// <summary><c>ProductListQuery.MaxFilterValues</c>.</summary>
    public const int MaxValuesPerKey = 25;

    /// <summary>What an unsafe path segment is stored as.</summary>
    public const string Redacted = "*";

    /// <summary>
    /// The query keys that survive, from <c>listing-state.ts</c>: the six repeatable slug filters, the price,
    /// rating and discount bounds, the three switches and the sort. <c>pages</c> (how far Load More went) is
    /// not a filter and is dropped. Kept in this order, so one set of filters always gives one path.
    /// </summary>
    public static IReadOnlyList<string> FilterKeys { get; } =
    [
        "ageGroups", "colours", "fits", "inStock", "materials", "maxPrice", "minDiscountPercent", "minPrice",
        "minRating", "newArrivals", "occasions", "onSale", "sizes", "sort",
    ];

    private static readonly HashSet<string> SlugListKeys = new(StringComparer.Ordinal)
    {
        "ageGroups", "colours", "materials", "fits", "occasions",
    };

    private static readonly HashSet<string> SwitchKeys = new(StringComparer.Ordinal)
    {
        "inStock", "onSale", "newArrivals",
    };

    private static readonly HashSet<string> PaidClickIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "gclid", "gbraid", "wbraid", "dclid", "msclkid",
    };

    private static readonly HashSet<string> SocialClickIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "ttclid",
    };

    /// <summary>Reduces <paramref name="raw"/> (path, query and fragment as the browser had them) or answers null.</summary>
    public static NormalisedPage? Normalise(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxRawLength || raw[0] != '/'
            || raw.StartsWith("//", StringComparison.Ordinal) || raw.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        var fragmentAt = raw.IndexOf('#', StringComparison.Ordinal);
        var withoutFragment = fragmentAt >= 0 ? raw[..fragmentAt] : raw;

        var queryAt = withoutFragment.IndexOf('?', StringComparison.Ordinal);
        var pathPart = queryAt >= 0 ? withoutFragment[..queryAt] : withoutFragment;
        var queryPart = queryAt >= 0 ? withoutFragment[(queryAt + 1)..] : string.Empty;

        var rawSegments = pathPart.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (rawSegments.Length > MaxSegments)
        {
            return null;
        }

        var segments = rawSegments.Select(SafeSegment).ToArray();
        var pageType = Classify(segments);

        // The order page's token is starred by shape already (a GUID is never a safe segment); this makes it
        // unconditional, so whatever an order address carries after `/order/` is never stored.
        if (pageType == PageType.Order)
        {
            segments = ["order", Redacted];
        }

        var query = ParseQuery(queryPart);
        var campaign = ReadCampaign(query);

        var path = "/" + string.Join('/', segments);

        if (KeepsFilters(pageType))
        {
            var filters = FilterQuery(query);

            // Filters that would push the path past the column are dropped whole rather than cut part way,
            // which could store a value that was never sent.
            if (filters.Length > 0 && path.Length + 1 + filters.Length <= PageView.PathMaxLength)
            {
                path = path + "?" + filters;
            }
        }

        return path.Length > PageView.PathMaxLength ? null : new NormalisedPage(path, pageType, segments, campaign);
    }

    /// <summary>
    /// A path segment as stored: itself when it is a catalogue slug that cannot be a secret, otherwise
    /// <see cref="Redacted"/>. Percent-decoded first, so <c>%40</c> is judged as the <c>@</c> it is.
    /// </summary>
    public static string SafeSegment(string rawSegment)
    {
        ArgumentNullException.ThrowIfNull(rawSegment);

        var decoded = Decode(rawSegment);

        return decoded is not null && IsSafeSlug(decoded) ? decoded : Redacted;
    }

    /// <summary>A catalogue slug that is not a GUID and carries no phone-length run of digits.</summary>
    public static bool IsSafeSlug(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Length is > 0 and <= MaxSegmentLength
            && SlugPattern().IsMatch(value)
            && !Guid.TryParse(value, out _)
            && !PersonalDataPatterns.HasPhoneDigitRun(value);
    }

    /// <summary>
    /// A UTM value as stored: trimmed, whitespace collapsed, at most 100 characters — or nothing, when it is
    /// empty, holds a control character, or looks like an email address or a phone number (a personalised
    /// mail link is the usual cause).
    /// </summary>
    public static string? CleanTag(string? value, bool lowerCase)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var collapsed = WhitespaceRun().Replace(value.Trim(), " ");

        if (PersonalDataPatterns.LooksPersonal(collapsed) || collapsed.Any(char.IsControl))
        {
            return null;
        }

        if (collapsed.Length > PageView.UtmMaxLength)
        {
            collapsed = collapsed[..PageView.UtmMaxLength].TrimEnd();
        }

        return lowerCase ? collapsed.ToLowerInvariant() : collapsed;
    }

    private static PageType Classify(string[] segments)
    {
        if (segments.Length == 0)
        {
            return PageType.Home;
        }

        return (segments[0], segments.Length) switch
        {
            ("order", 2) => PageType.Order,
            ("order", _) => PageType.Other,
            ("product", 2) => PageType.Product,
            ("search", 1) => PageType.Search,
            ("bag", 1) => PageType.Bag,
            ("wishlist", 1) => PageType.Wishlist,
            ("checkout", 1) => PageType.Checkout,
            ("account", <= 2) => PageType.Account,
            ("new-arrivals", 1) => PageType.NewArrivals,
            ("sale", 1) => PageType.Sale,
            (Redacted, _) => PageType.Other,
            (_, 1) => PageType.Department,
            (_, 2 or 3) => PageType.Category,
            _ => PageType.Other,
        };
    }

    private static bool KeepsFilters(PageType pageType) =>
        pageType is PageType.Department or PageType.Category or PageType.NewArrivals or PageType.Sale
            or PageType.Search;

    private static List<KeyValuePair<string, string>> ParseQuery(string query)
    {
        var pairs = new List<KeyValuePair<string, string>>();

        if (query.Length == 0)
        {
            return pairs;
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsAt = part.IndexOf('=', StringComparison.Ordinal);
            var key = Decode((equalsAt >= 0 ? part[..equalsAt] : part).Replace('+', ' '));
            var value = Decode((equalsAt >= 0 ? part[(equalsAt + 1)..] : string.Empty).Replace('+', ' '));

            if (key is not null && value is not null)
            {
                pairs.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        return pairs;
    }

    private static string? Decode(string component)
    {
        try
        {
            return Uri.UnescapeDataString(component);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static CampaignTags ReadCampaign(List<KeyValuePair<string, string>> query)
    {
        if (query.Count == 0)
        {
            return CampaignTags.None;
        }

        string? First(string key) =>
            query.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

        return new CampaignTags(
            CleanTag(First("utm_source"), lowerCase: true),
            CleanTag(First("utm_medium"), lowerCase: true),
            CleanTag(First("utm_campaign"), lowerCase: false),
            query.Any(pair => PaidClickIds.Contains(pair.Key) && pair.Value.Length > 0),
            query.Any(pair => SocialClickIds.Contains(pair.Key) && pair.Value.Length > 0));
    }

    private static string FilterQuery(List<KeyValuePair<string, string>> query)
    {
        var builder = new StringBuilder();

        foreach (var key in FilterKeys)
        {
            var values = query
                .Where(pair => string.Equals(pair.Key, key, StringComparison.Ordinal))
                .Select(pair => pair.Value.Trim())
                .Where(value => IsAllowedValue(key, value))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(MaxValuesPerKey);

            foreach (var value in values)
            {
                if (builder.Length > 0)
                {
                    builder.Append('&');
                }

                builder.Append(key).Append('=').Append(Uri.EscapeDataString(value));
            }
        }

        return builder.ToString();
    }

    private static bool IsAllowedValue(string key, string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        if (SlugListKeys.Contains(key))
        {
            return IsSafeSlug(value);
        }

        if (SwitchKeys.Contains(key))
        {
            return value == "true";
        }

        return key switch
        {
            // A size is a label ("M", "32", "UK 10"): `catalog.Sizes.Label` is nvarchar(20).
            "sizes" => SizeLabelPattern().IsMatch(value) && !PersonalDataPatterns.HasPhoneDigitRun(value),

            // Bounded numbers. The bound is the API's own, and it is also what keeps a phone number out: Sri
            // Lankan numbers have nine or ten digits, and the largest price has eight.
            "minPrice" or "maxPrice" => IsWholeNumberInRange(value, 0, 10_000_000),
            "minDiscountPercent" => IsWholeNumberInRange(value, 0, 100),
            "minRating" => value.Length <= 4
                && decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rating)
                && rating is >= 0 and <= 5,
            "sort" => SortPattern().IsMatch(value),
            _ => false,
        };
    }

    private static bool IsWholeNumberInRange(string value, int min, int max) =>
        value.Length <= 8
        && value.All(char.IsAsciiDigit)
        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number >= min && number <= max;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ./-]{0,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex SizeLabelPattern();

    [GeneratedRegex("^[a-z]{1,20}(?:-[a-z]{1,20}){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex SortPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();
}
