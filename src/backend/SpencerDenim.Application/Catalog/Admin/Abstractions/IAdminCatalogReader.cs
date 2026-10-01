using SpencerDenim.Application.Catalog.Admin.Contracts;

namespace SpencerDenim.Application.Catalog.Admin.Abstractions;

/// <summary>
/// Read port for the admin catalogue structure screens (ADM-11, W1-12).
/// <para>
/// Separate from <see cref="SpencerDenim.Application.Catalog.Abstractions.IDepartmentCatalogReader"/> on
/// purpose, and not a flag on it: that reader answers "what should the shop show", which means it drops
/// every deactivated row, and an admin screen that could not see a deactivated row could not switch one
/// back on. The two readers answer different questions about the same tables, so they are different
/// ports (ISP, doc 03 §3).
/// </para>
/// </summary>
public interface IAdminCatalogReader
{
    /// <summary>Every department in sort order, active or not, with its live category count.</summary>
    Task<IReadOnlyList<AdminDepartmentDto>> GetDepartmentsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One department's whole category tree in sibling order, active or not, or <c>null</c> when no
    /// department has that id. Soft-deleted categories are not in it: they are gone, not hidden.
    /// </summary>
    Task<AdminCategoryTreeDto?> GetCategoryTreeAsync(int departmentId, CancellationToken cancellationToken);

    /// <summary>
    /// What deactivating a category would do to the storefront, or <c>null</c> when no live category has
    /// that id.
    /// </summary>
    Task<CategoryDeactivationImpactDto?> GetDeactivationImpactAsync(int categoryId, CancellationToken cancellationToken);

    /// <summary>
    /// The age groups and size guides a category form may choose from (doc 11 P1 step 2). Both are small,
    /// seeded, admin-visible lists; neither is paged and neither needs a filter.
    /// </summary>
    Task<CategoryFormOptionsDto> GetFormOptionsAsync(CancellationToken cancellationToken);
}
