using SpencerDenim.Application.Abstractions.Auditing;

namespace SpencerDenim.Application.Catalog.Admin;

/// <summary>
/// Edit a department's description, hero image and SEO fields (ADM-11, doc 11 P1 step 1).
/// <para>
/// There is no create and no delete, and that is doc 08 §3.2's rule rather than an omission: "the nav is
/// fixed (Q5): there is no admin create or delete; admins edit only". <see cref="SetDepartmentActiveCommand"/>
/// is how a department leaves the shop.
/// </para>
/// <para>
/// <b>And no name and no slug</b> — the owner's decision of 2026-10-02, confirming doc 11 P1 step 1's list
/// of what staff may edit: "their description, hero image, sort order and SEO". A department slug is the
/// first segment of every listing URL in its department and redirects are deferred (doc 08 Appendix A), so
/// an editable one is a third of the shop's links waiting to break for a rename nobody can undo.
/// </para>
/// <para>
/// This does <b>not</b> leave W1-12's W1-02 follow-up unserved, and that was the error in the reading this
/// was first built from: <c>Department.Slug</c> has had a write path since W1-02 — the seeder — so
/// <see cref="SpencerDenim.Domain.Catalog.CatalogSlug"/> in the property setter and
/// <c>CK_Departments_Slug</c> behind it guard the path that exists, which is the argument
/// <c>CatalogSlugConstraint</c> itself already makes.
/// </para>
/// </summary>
/// <param name="RowVersion">Base64, from the admin read model (doc 08 §1.5 [RV]).</param>
public sealed record UpdateDepartmentCommand(
    int DepartmentId,
    string? Description,
    int? HeroImageAssetId,
    string? SeoTitle,
    string? SeoDescription,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Put the departments in a new order (ADM-11): the nav's left-to-right order, <c>SortOrder</c>.
/// <para>
/// The whole list, not one row's position — see <see cref="ReorderCategoriesCommand"/> for why.
/// </para>
/// </summary>
public sealed record ReorderDepartmentsCommand(
    IReadOnlyList<int>? OrderedDepartmentIds,
    AdminActor Actor);

/// <summary>Switch a department on or off (ADM-11, doc 08 §7.1's <c>IsActive</c>).</summary>
public sealed record SetDepartmentActiveCommand(
    int DepartmentId,
    bool IsActive,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Create a category under a department, optionally under a parent category (ADM-11, doc 11 P1 step 2).
/// <para>
/// It goes to the end of its sibling list. There is no "insert at position": a create followed by a
/// reorder is two decisions an admin can see the result of, where a create that silently renumbered
/// eleven siblings is one they cannot.
/// </para>
/// </summary>
public sealed record CreateCategoryCommand(
    int DepartmentId,
    int? ParentCategoryId,
    string? Name,
    string? Slug,
    string? Description,
    int? ImageAssetId,
    int? BannerAssetId,
    string? BannerLinkUrl,
    int? AgeGroupId,
    int? SizeGuideId,
    bool ShowInMegaMenu,
    byte? MegaMenuColumn,
    string? SeoTitle,
    string? SeoDescription,
    AdminActor Actor);

/// <summary>
/// Edit a category, <b>including moving it to another parent</b> (ADM-11; doc 11 P1 step 4 "or moves a
/// category under another parent. The move updates the path of the whole subtree in one transaction").
/// <para>
/// The move is part of the edit rather than an endpoint of its own because the parent is a field on the
/// form an admin is already filling in, and because the alternative — a separate move that cannot also
/// rename — makes "put this under Dresses and call it Midi" two writes, two audit rows and two chances
/// for the second to fail after the first.
/// </para>
/// <para>
/// <see cref="Contracts.AdminCategoryNodeDto.IsActive"/> is deliberately <b>not</b> a field here. It is
/// <see cref="SetCategoryActiveCommand"/>'s, so that taking a section of the shop down is a deliberate
/// act with its own confirmation and its own <c>StatusChange</c> audit row, and never a side effect of
/// saving a form whose active checkbox was stale.
/// </para>
/// </summary>
public sealed record UpdateCategoryCommand(
    int CategoryId,
    int? ParentCategoryId,
    string? Name,
    string? Slug,
    string? Description,
    int? ImageAssetId,
    int? BannerAssetId,
    string? BannerLinkUrl,
    int? AgeGroupId,
    int? SizeGuideId,
    bool ShowInMegaMenu,
    byte? MegaMenuColumn,
    string? SeoTitle,
    string? SeoDescription,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Put one parent's children in a new order (ADM-11, doc 11 P1 step 4).
/// <para>
/// <b>A whole-list operation, and the command's shape is the reason.</b> It takes every sibling id in the
/// order they should end up in, so "move Jeans above Dresses" is expressed as the list both admins would
/// agree on rather than as an increment to one row. Two admins reordering the same list at the same time
/// therefore cannot interleave into an order neither of them chose.
/// </para>
/// <para>
/// <b>Its concurrency story is not the single-row one, and the difference is worth stating.</b> This command
/// carries no row version and the handler makes no
/// <c>CatalogWriteSupport.CheckRowVersion</c> call — a reorder is a claim about a list, and a token per row
/// would be thirteen tokens for one decision. What protects it is the set check
/// (<see cref="CatalogConflictCodes.StaleOrder"/>: the list must be exactly the siblings the server holds,
/// so a list built before somebody else's create or move is refused rather than partly applied) and then
/// EF's own <c>WHERE RowVersion = @original</c> on each row it updates, which the loser of a genuine race
/// fails on — surfacing as <see cref="CatalogConflictCodes.StaleRowVersion"/> from the save rather than from
/// a check before it.
/// </para>
/// </summary>
/// <param name="ParentCategoryId">The parent whose children these are; <c>null</c> for a department's top level.</param>
public sealed record ReorderCategoriesCommand(
    int DepartmentId,
    int? ParentCategoryId,
    IReadOnlyList<int>? OrderedCategoryIds,
    AdminActor Actor);

/// <summary>
/// Switch a category on or off (ADM-11). Doc 08 §7.1: <c>IsActive</c> is the only switch an admin has for
/// taking a section off the storefront, so this is the write with the largest visible effect in the whole
/// of W1-12 — <see cref="Abstractions.IAdminCatalogReader.GetDeactivationImpactAsync"/> is what the screen
/// shows before it.
/// </summary>
public sealed record SetCategoryActiveCommand(
    int CategoryId,
    bool IsActive,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Remove a category (ADM-11's "remove"): a soft delete, refused while anything still points at it
/// (doc 08 §3.2 "Soft delete is blocked while the category has active products or child categories",
/// doc 11 P1 step 5).
/// <para>
/// A guard, never a cascade. Cascading would soft-delete child categories and orphan every
/// <c>Products.PrimaryCategoryId</c> underneath them — a NOT NULL column pointing at a deleted row, which
/// takes the products off the storefront with nothing on any screen to explain why.
/// </para>
/// </summary>
public sealed record RemoveCategoryCommand(
    int CategoryId,
    string? RowVersion,
    AdminActor Actor);
