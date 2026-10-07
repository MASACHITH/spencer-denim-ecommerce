using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Content;

/// <summary>
/// <c>content.ContentPages</c> (doc 08 §3.7, GLB-10) [A] [SD] [RV]. An editorial page the client writes
/// in the admin and a visitor reads on the storefront: About, Contact, Returns, Shipping, Privacy, Terms,
/// plus any extra page the client creates.
/// <para>
/// <b>Two kinds of row, and the difference is one nullable column.</b> A row with a
/// <see cref="SystemKey"/> is one of the six pages the shop is built around: the footer links to it, the
/// storefront has code that expects it, and doc 11 P13's business rules say it <i>cannot be deleted</i>.
/// A row without one is a custom page, which can. <see cref="CanBeDeleted"/> is that rule, in one place,
/// so the delete use case and the screen that hides the button cannot disagree about it.
/// </para>
/// <para>
/// <b>Every one of the six ships unpublished with an empty body, and that is the specification rather
/// than an unfinished job.</b> Doc 11 P13: system pages "stay <b>unpublished until the client supplies
/// the copy</b>; Privacy and Terms need a PDPA review". A returns policy, a privacy policy and terms of
/// business are the client's own words and carry legal weight, so nothing in this repository writes them.
/// </para>
/// </summary>
public sealed class ContentPage : IAuditable, ISoftDeletable
{
    /// <summary><c>content.ContentPages.Slug</c> is <c>varchar(100)</c>.</summary>
    public const int SlugMaxLength = 100;

    /// <summary><c>content.ContentPages.Title</c> is <c>nvarchar(150)</c>.</summary>
    public const int TitleMaxLength = 150;

    /// <summary><c>content.ContentPages.SystemKey</c> is <c>varchar(30)</c>.</summary>
    public const int SystemKeyMaxLength = 30;

    /// <summary><c>content.ContentPages.SeoTitle</c> is <c>nvarchar(160)</c>.</summary>
    public const int SeoTitleMaxLength = 160;

    /// <summary><c>content.ContentPages.SeoDescription</c> is <c>nvarchar(320)</c>.</summary>
    public const int SeoDescriptionMaxLength = 320;

    /// <summary>
    /// How much sanitised HTML one body may hold. The column is <c>nvarchar(max)</c>, so this is a
    /// product decision rather than a storage one: a shop's terms page is long, a megabyte of HTML in a
    /// server-rendered page is not, and an unbounded text field on a write path is a cheap way to fill a
    /// database. Measured against the longest policy page any of the reference shops publish, which is
    /// under 30 KB of markup.
    /// </summary>
    public const int BodyHtmlMaxLength = 200_000;

    public int Id { get; set; }

    /// <summary>
    /// <c>about</c>, <c>contact</c>, <c>returns</c>, <c>shipping</c>, <c>privacy</c> or <c>terms</c> for
    /// one of the six system pages (<see cref="ContentPageKeys"/>); <see langword="null"/> for a custom
    /// page. Unique among live rows, and never edited after the seeder writes it.
    /// </summary>
    public string? SystemKey { get; set; }

    /// <summary>
    /// The URL segment, unique among live rows. Editable even on a system page — doc 11 P13 step 12 lists
    /// "slug" among the fields staff may change — because the six slugs are English words a Sri Lankan
    /// shop may well want to spell differently, and nothing in the storefront addresses a page by
    /// anything but this column. The storefront's footer reads the live list rather than a constant, so a
    /// renamed slug does not leave a broken link behind it.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The body, as <b>already-sanitised</b> HTML (doc 08 §3.7 "Sanitised"). Empty for a page the client
    /// has not written yet, which is how all six are seeded.
    /// <para>
    /// <b>Nothing may write this property with HTML that has not been through the allow-list
    /// sanitiser.</b> It is rendered as markup on a public page, so it is the one stored-XSS surface this
    /// feature has. The sanitising happens in the write use case, on save, where doc 08 §3.7/§3.8 puts
    /// it; the storefront binds it with Angular's <c>[innerHTML]</c>, whose own sanitiser then runs a
    /// second time on render. Neither layer is a reason to skip the other.
    /// </para>
    /// </summary>
    public string BodyHtml { get; set; } = string.Empty;

    public string? SeoTitle { get; set; }

    public string? SeoDescription { get; set; }

    /// <summary>
    /// Whether a visitor may read it. An unpublished page is not served, is not in the footer and is not
    /// in <c>sitemap.xml</c>: a draft policy must not leak, and a draft that a crawler indexes is worse
    /// than one nobody can find.
    /// </summary>
    public bool IsPublished { get; set; }

    /// <summary>
    /// When it was <i>first</i> published, which is what a reader of a policy page wants to know. It is
    /// not cleared by unpublishing: the date the client first published their returns policy does not
    /// stop being true because the page is being rewritten.
    /// </summary>
    public DateTime? PublishedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    public byte[]? RowVersion { get; set; }

    /// <summary>Doc 08 §1.5 [SD]. Set only by <see cref="SoftDelete"/>.</summary>
    public bool IsDeleted { get; private set; }

    public DateTime? DeletedAtUtc { get; private set; }

    public int? DeletedByUserId { get; private set; }

    /// <summary>True for one of the six pages doc 11 P13 calls a system page.</summary>
    public bool IsSystemPage => !string.IsNullOrEmpty(SystemKey);

    /// <summary>
    /// Doc 11 P13: "System pages cannot be deleted." A custom page can, and that is a soft delete, so
    /// the slug it held is released for reuse (the unique index is filtered on <c>IsDeleted = 0</c>).
    /// </summary>
    public bool CanBeDeleted => !IsSystemPage;

    public void SoftDelete(DateTime utcNow, int? userId)
    {
        if (IsDeleted)
        {
            return;
        }

        IsDeleted = true;
        DeletedAtUtc = utcNow;
        DeletedByUserId = userId;
    }

    public void Restore()
    {
        IsDeleted = false;
        DeletedAtUtc = null;
        DeletedByUserId = null;
    }

    /// <summary>
    /// Publishes or unpublishes the page, stamping <see cref="PublishedAtUtc"/> the first time only.
    /// Returns true when the flag actually moved, so the caller knows whether to write a status-change
    /// audit row.
    /// </summary>
    public bool SetPublished(bool published, DateTime utcNow)
    {
        if (IsPublished == published)
        {
            return false;
        }

        IsPublished = published;

        if (published)
        {
            PublishedAtUtc ??= utcNow;
        }

        return true;
    }
}

/// <summary>
/// The six <see cref="ContentPage.SystemKey"/> values of doc 08 §3.7, and the slug and title each one is
/// seeded with.
/// <para>
/// <b>This list is the definition of "a page that cannot be deleted", and it is also the seeder's
/// input.</b> One list, so a seventh system page is one entry rather than four places to keep in step.
/// </para>
/// </summary>
public static class ContentPageKeys
{
    public const string About = "about";

    public const string Contact = "contact";

    public const string Returns = "returns";

    public const string Shipping = "shipping";

    public const string Privacy = "privacy";

    public const string Terms = "terms";

    /// <summary>
    /// The six, in the order the admin list shows them: the two the shop introduces itself with, the two
    /// about buying, then the two legal ones that need a PDPA review (doc 08 §9).
    /// </summary>
    public static IReadOnlyList<ContentPageSeed> Seeds { get; } =
    [
        new(About, About, "About us"),
        new(Contact, Contact, "Contact us"),
        new(Returns, Returns, "Returns and exchanges"),
        new(Shipping, Shipping, "Delivery and shipping"),
        new(Privacy, Privacy, "Privacy policy"),
        new(Terms, Terms, "Terms and conditions"),
    ];

    private static readonly HashSet<string> Known =
        Seeds.Select(seed => seed.SystemKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="systemKey"/> is one of the six. Case-insensitively, to match the database
    /// collation (<c>Latin1_General_100_CI_AS_SC</c>) — an ordinal comparison here would let
    /// <c>About</c> and <c>about</c> be two different answers on the two sides of the same query.
    /// </summary>
    public static bool IsSystemKey(string? systemKey) =>
        systemKey is not null && Known.Contains(systemKey);
}

/// <summary>One of the six system pages as the seeder creates it: a shell with no copy in it.</summary>
/// <param name="SystemKey">The immutable key (<see cref="ContentPageKeys"/>).</param>
/// <param name="Slug">The slug it starts with. Staff may change it afterwards.</param>
/// <param name="Title">
/// The heading it starts with, in the shop's own voice. A title is a label rather than copy, so seeding
/// one is not writing the client's words for them — the <i>body</i> is what stays empty.
/// </param>
public sealed record ContentPageSeed(string SystemKey, string Slug, string Title);
