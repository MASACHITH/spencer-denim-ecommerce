using System.Linq.Expressions;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Infrastructure.Catalog;

/// <summary>
/// What "a shopper may see this" means, written once (GLB-11).
/// <para>
/// These predicates were inline in <see cref="ProductCatalogReader"/> and
/// <see cref="DepartmentCatalogReader"/>, which was fine while those two were the only readers. The
/// sitemap is a third, and it enumerates exactly the rows the other two serve — so a copy of the rule
/// in it would be a second definition of "visible", free to drift the moment an admin gains a new way
/// to hide something. W1-12 has just given the client the ability to deactivate and soft-delete
/// categories, so that drift would start immediately and silently: a deactivated listing would stay in
/// the sitemap, which is a 404 handed to a crawler on purpose.
/// </para>
/// <para>
/// Soft deletion is <b>not</b> repeated here. <c>AppDbContext.SoftDeleteFilter</c> is a global query
/// filter on every <c>ISoftDeletable</c> entity, so a deleted row is already absent from any query
/// these compose into. Repeating it would read as though the filter might not be there.
/// </para>
/// </summary>
internal static class CatalogVisibility
{
    /// <summary>
    /// A product on the storefront: published, with its stats row, in an active department and an
    /// active primary category.
    /// <para>
    /// Doc 08 §7.1: <c>IsActive</c> is the only switch an admin has for taking a section off the
    /// storefront, so a product leaves with its department or its primary category. Its other category
    /// links do not keep it visible — a product whose primary category is gone has a breadcrumb into
    /// nothing, which is a dead end reachable from a live page.
    /// </para>
    /// <para>
    /// <c>Stats != null</c> is 1:1 with a product and written in the same transaction, so it is never
    /// false in practice; it keeps the join an inner one and the projection total. The conjunction
    /// translates to the same <c>WHERE … AND …</c> that the chained <c>Where</c> calls it replaced
    /// produced, so the filtered indexes of DB28 still match.
    /// </para>
    /// </summary>
    public static readonly Expression<Func<Product, bool>> VisibleProduct = product =>
        product.IsPublished
        && product.Stats != null
        && product.Department!.IsActive
        && product.PrimaryCategory!.IsActive;

    /// <summary>A department a shopper may reach. Deactivating one takes it off the storefront whole.</summary>
    public static readonly Expression<Func<Department, bool>> VisibleDepartment = department =>
        department.IsActive;

    /// <summary>
    /// A category a shopper may reach, considered on its own row. Whether its <i>ancestors</i> are also
    /// active is a question about the tree rather than the row, and the callers that assemble a tree
    /// answer it by only ever descending from an active parent.
    /// </summary>
    public static readonly Expression<Func<Category, bool>> VisibleCategory = category =>
        category.IsActive;
}
