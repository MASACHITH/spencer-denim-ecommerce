namespace SpencerDenim.Application.Content.Contracts;

/// <summary>
/// A published content page as the storefront renders it (GLB-10).
/// <para>
/// <b>Only a published page is ever projected into this shape.</b> The reader's predicate is the control —
/// there is no <c>isPublished</c> field here for a client to check, because a payload that could carry a
/// draft is a payload that will one day leak one. A draft is a 404 to the public API, which is the same
/// answer a slug that never existed gets.
/// </para>
/// </summary>
/// <param name="Slug">The address it was found at, echoed back so the page can set its canonical URL.</param>
/// <param name="Title">The heading, and the basis of the document title when there is no SEO title.</param>
/// <param name="BodyHtml">
/// <b>Already-sanitised</b> HTML (doc 08 §3.7), cleaned by the allow-list sanitiser when it was saved.
/// Empty for a page the client has published with no copy in it, which the storefront renders as a page
/// with a heading and nothing under it rather than as an error.
/// </param>
/// <param name="SeoTitle">The <c>&lt;title&gt;</c>, or null to build one from <paramref name="Title"/>.</param>
/// <param name="SeoDescription">The meta description, or null for the storefront's own fallback.</param>
/// <param name="SystemKey">
/// <c>about</c>, <c>contact</c>, <c>returns</c>, <c>shipping</c>, <c>privacy</c>, <c>terms</c>, or null for
/// a custom page. The storefront needs it for one reason: the Contact page also carries a form, the opening
/// hours, the map and the social links, and it has to know which page that is without matching on a slug
/// the client may rename.
/// </param>
/// <param name="PublishedAtUtc">When it was first published. A policy page's reader wants the date.</param>
/// <param name="UpdatedAtUtc">When it last changed, which is what <c>lastmod</c> in the sitemap uses.</param>
public sealed record ContentPageDto(
    string Slug,
    string Title,
    string BodyHtml,
    string? SeoTitle,
    string? SeoDescription,
    string? SystemKey,
    DateTime? PublishedAtUtc,
    DateTime? UpdatedAtUtc);

/// <summary>
/// One published page as something that links to it needs it: the footer, the sitemap and the storefront's
/// route guard (GLB-10, GLB-11).
/// <para>
/// A separate, smaller shape from <see cref="ContentPageDto"/> on purpose. The footer is rendered on every
/// page of the site, so sending six bodies of HTML with it would put the whole of the shop's terms and
/// conditions into the markup of every product page.
/// </para>
/// </summary>
/// <param name="Slug">The address.</param>
/// <param name="Title">The link text.</param>
/// <param name="SystemKey">As <see cref="ContentPageDto.SystemKey"/>.</param>
/// <param name="UpdatedAtUtc">For <c>sitemap.xml</c>'s <c>lastmod</c>.</param>
public sealed record ContentPageLinkDto(
    string Slug,
    string Title,
    string? SystemKey,
    DateTime? UpdatedAtUtc);

/// <summary>
/// Every published page, in the order a footer lists them.
/// <para>
/// A wrapper rather than a bare list, because the storefront has to tell "the shop has no published pages"
/// from "the content feed could not be read" — #30's lesson, which this feature meets in its sharpest form:
/// a slug that is not in this list must answer 404 when the list is <i>live</i> and 503 when it is not, and
/// a bare array cannot say which it was.
/// </para>
/// </summary>
/// <param name="Pages">The published pages. Empty is a normal state, and the state a fresh shop is in.</param>
public sealed record ContentPageLinksDto(IReadOnlyList<ContentPageLinkDto> Pages)
{
    /// <summary>A shop whose client has published no pages yet, which is every shop until they do.</summary>
    public static ContentPageLinksDto Empty { get; } = new([]);
}
