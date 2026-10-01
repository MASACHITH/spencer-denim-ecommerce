namespace SpencerDenim.Application.Catalog.Contracts;

/// <summary>
/// Everything a crawler may be told exists, as catalogue facts rather than as addresses (GLB-11).
/// <para>
/// **Slugs, not URLs, and deliberately so.** The storefront owns its route table — a product lives at
/// <c>/product/{slug}</c> and a category at <c>/{department}/{trail}</c>, and both shapes are decided
/// in <c>app.routes.ts</c>. If this contract carried finished paths, the API would hold a second copy
/// of that table and a route change would be a silent sitemap bug. So the API answers which
/// departments, categories and products a visitor may see; <c>sitemap.xml</c> in the storefront's
/// Express layer turns them into addresses.
/// </para>
/// <para>
/// Visibility is <b>not</b> re-decided here either: the reader behind this composes the very same
/// predicates the listing and the mega menu use (<c>CatalogVisibility</c>), so a department an admin
/// switches off, a soft-deleted category and an unpublished product leave the sitemap at the moment
/// they leave the shop.
/// </para>
/// </summary>
/// <param name="GeneratedAtUtc">When the snapshot was read, so a cache downstream can age it.</param>
/// <param name="Departments">Active departments, each with its active category tree.</param>
/// <param name="Products">Published products, newest change first, capped (see <paramref name="ProductsTruncated"/>).</param>
/// <param name="ProductsTruncated">
/// True when the catalogue holds more published products than <c>Catalog:SitemapMaxProducts</c>
/// allows one answer to carry. It is reported rather than hidden: a silently short sitemap is the
/// kind of defect nobody notices for a year.
/// </param>
public sealed record CatalogSitemapDto(
    DateTime GeneratedAtUtc,
    IReadOnlyList<SitemapDepartmentDto> Departments,
    IReadOnlyList<SitemapProductDto> Products,
    bool ProductsTruncated);

/// <summary>One active department and the active categories under it.</summary>
/// <param name="Slug">The department's own slug, the first segment of a listing address.</param>
/// <param name="LastModifiedUtc">Last change to the row, or when it was created. Null when neither is known.</param>
/// <param name="Categories">Every active category in the department, at any depth.</param>
public sealed record SitemapDepartmentDto(
    string Slug,
    DateTime? LastModifiedUtc,
    IReadOnlyList<SitemapCategoryDto> Categories);

/// <summary>
/// One active category, as the trail of slugs from the department down to it — <c>["girls",
/// "girls-dresses"]</c> — because that is the address the storefront routes and the breadcrumb draws.
/// A category whose ancestors are not all active is absent: it has no reachable address.
/// </summary>
public sealed record SitemapCategoryDto(IReadOnlyList<string> SlugTrail, DateTime? LastModifiedUtc);

/// <summary>One published product.</summary>
public sealed record SitemapProductDto(string Slug, DateTime? LastModifiedUtc);
