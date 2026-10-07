namespace SpencerDenim.Domain.Content;

/// <summary>
/// The addresses a content page may <b>not</b> take, because something else on the storefront already
/// answers them.
/// <para>
/// <b>Why this is a rule and not a comment.</b> A content page is served from a single URL segment
/// (<c>/returns</c>), and so are the department listings, the search page, the bag, the wishlist, checkout
/// and the account area. The storefront's route table is ordered, and those routes are declared <i>above</i>
/// the content-page route — they have to be, or a department called "women" would be looked up as a page on
/// every navigation. So a page saved on one of these slugs is a page that exists in the database, appears in
/// the admin list, appears in the footer, appears in <c>sitemap.xml</c>, and <b>cannot be opened by
/// anybody</b>: the address resolves to the bag.
/// </para>
/// <para>
/// That is a defect with no symptom on the screen that caused it, which is the kind this project has
/// shipped before, so it is refused at the point of saving with a message naming the clash. The six seeded
/// slugs are all outside this list, and so is anything a client is likely to want
/// (<c>size-guide</c>, <c>faq</c>, <c>stores</c>, <c>careers</c>).
/// </para>
/// <para>
/// <b>Department slugs are not in here</b>, deliberately: they are rows, the three seeded ones could in
/// principle grow to a fourth, and a hard-coded copy of a database list is the thing that goes stale. The
/// write use case checks them against the table instead. What <i>is</i> here is only what the front end's
/// own route table and the server's own endpoints declare — a fixed list on both counts.
/// </para>
/// </summary>
public static class ContentPageSlugRules
{
    /// <summary>
    /// Single-segment addresses the storefront already answers, from
    /// <c>projects/storefront/src/app/app.routes.ts</c> and <c>src/server</c>.
    /// <para>
    /// <c>ContentPageSlugRulesTests</c> reads that route file and fails if it declares a single-segment
    /// route this list does not hold, so the two cannot drift apart silently — the same binding
    /// <c>AdminProductEnumContractTests</c> uses to keep a TypeScript union and a C# enum in step.
    /// </para>
    /// </summary>
    public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Pages with routes of their own, declared above the content-page route.
        "product",
        "search",
        "bag",
        "wishlist",
        "checkout",
        "order",
        "new-arrivals",
        "sale",

        // Declared in UNBUILT_PATHS and owed by W3-01. A page saved here would be shadowed the day that
        // task lands rather than today, which is worse: it would work, be published, and then stop.
        "account",

        // Served by the Node server or the reverse proxy, not by Angular's router at all.
        "api",
        "internal",
        "media",
        "assets",
        "healthz",
        "robots",
        "sitemap",
    };

    /// <summary>Whether <paramref name="slug"/> is one the storefront already answers.</summary>
    public static bool IsReserved(string? slug) => slug is not null && Reserved.Contains(slug);
}
