namespace SpencerDenim.Application.Catalog.Admin.Contracts;

/// <summary>
/// A department as the admin screens see it (ADM-11, W1-12).
/// <para>
/// <b>Why this is not <c>DepartmentDto</c>.</b> The storefront's reader drops anything an admin has
/// switched off — <c>IsActive</c> is the switch doc 08 §7.1 gives them, and the whole point of the
/// switch is that the shop stops showing the row. An admin screen built on that payload could never
/// show a deactivated department, so it could never switch one back on. This contract is deliberately
/// a separate read model, not a flag added to the storefront's.
/// </para>
/// </summary>
/// <param name="RowVersion">
/// Base64 of <c>catalog.Departments.RowVersion</c>. The screen sends it back with an edit and the
/// command refuses a stale one, so two admins editing the same department cannot silently overwrite
/// each other (doc 08 §1.5 [RV]).
/// </param>
/// <param name="CategoryCount">Live categories in this department, at every depth.</param>
/// <param name="StorefrontProductCount">
/// Published, non-deleted products currently reachable in this department — that is, with this
/// department active <i>and</i> their own primary category active, which is the predicate doc 08 §7.1
/// states and <c>ProductCatalogReader.BaseQuery</c> implements. It is the number that changes when the
/// department is switched off.
/// </param>
public sealed record AdminDepartmentDto(
    int Id,
    string Name,
    string Slug,
    string? Description,
    int? HeroImageAssetId,
    short SortOrder,
    bool IsActive,
    string? SeoTitle,
    string? SeoDescription,
    string RowVersion,
    int CategoryCount,
    int StorefrontProductCount);

/// <summary>
/// One node of the admin category tree (ADM-11): every live category, active or not, with the counts
/// the screens need to warn before an admin hides or removes something.
/// </summary>
/// <param name="Path">
/// The materialised path, e.g. <c>/3/17/</c>. Exposed because it is the one value an admin cannot
/// reconstruct from the screen and the one most likely to be wrong after a move, so a reviewer, a
/// support call or a test can read it straight off the payload.
/// </param>
/// <param name="RowVersion">Base64 of <c>catalog.Categories.RowVersion</c>; see <see cref="AdminDepartmentDto.RowVersion"/>.</param>
/// <param name="ProductCount">
/// Non-deleted products linked to this category through <c>catalog.ProductCategories</c>, published or
/// not. This is the number the remove guard uses, so the screen can disable the button for the same
/// reason the API would refuse it.
/// </param>
/// <param name="PrimaryProductCount">
/// Non-deleted products whose <i>primary</i> category this is. Deactivating the category takes exactly
/// these off the storefront (doc 08 §7.1); the products merely linked to it are unaffected.
/// </param>
public sealed record AdminCategoryNodeDto(
    int Id,
    int DepartmentId,
    int? ParentCategoryId,
    string Path,
    byte Depth,
    string Name,
    string Slug,
    string? Description,
    int? ImageAssetId,
    int? BannerAssetId,
    string? BannerLinkUrl,
    int? AgeGroupId,
    int? SizeGuideId,
    int SortOrder,
    bool ShowInMegaMenu,
    byte? MegaMenuColumn,
    bool IsActive,
    string? SeoTitle,
    string? SeoDescription,
    string RowVersion,
    int ProductCount,
    int PrimaryProductCount,
    IReadOnlyList<AdminCategoryNodeDto> Children);

/// <summary>One department's whole admin category tree (ADM-11).</summary>
public sealed record AdminCategoryTreeDto(
    int DepartmentId,
    string DepartmentName,
    string DepartmentSlug,
    bool DepartmentIsActive,
    IReadOnlyList<AdminCategoryNodeDto> Categories);

/// <summary>
/// What switching a category off will actually do to the shop, so an admin reads it <b>before</b>
/// pressing the button rather than discovering it afterwards (doc 08 §7.1).
/// <para>
/// <b>The three numbers are three different things, and conflating them is the trap.</b>
/// <c>IsActive</c> is per row and <c>ProductCatalogReader.BaseQuery</c> checks only a product's
/// <i>own</i> primary category, so deactivating a parent does <b>not</b> take its children's products
/// out of the listing — it only takes the parent and, with it, every route to that subtree out of the
/// navigation (<c>DepartmentCatalogReader</c> builds the tree from rows whose parent is itself in the
/// tree). The result is products that are still on sale with no menu path to them, which is the
/// consequence an admin is least likely to predict and the one this payload exists to state.
/// </para>
/// </summary>
/// <param name="ProductsLeavingTheStorefront">
/// Published products whose primary category is this one, and which are visible today. These disappear
/// from listings, from search and from their own product page.
/// </param>
/// <param name="DescendantCategories">Live categories underneath this one, at any depth.</param>
/// <param name="DescendantProductsLosingTheirMenuRoute">
/// Published products whose primary category is an active <i>descendant</i> of this one. They stay in
/// the listing and keep working URLs, but nothing in the mega menu leads to them any more.
/// </param>
/// <param name="Examples">
/// Up to five of the products in <paramref name="ProductsLeavingTheStorefront"/>, by name, so the
/// warning names something an admin recognises instead of only a number.
/// </param>
public sealed record CategoryDeactivationImpactDto(
    int CategoryId,
    string Name,
    bool IsActive,
    int ProductsLeavingTheStorefront,
    int DescendantCategories,
    int DescendantProductsLosingTheirMenuRoute,
    IReadOnlyList<string> Examples);
