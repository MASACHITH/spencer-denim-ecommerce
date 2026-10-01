using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Application.Catalog.Admin.Contracts;

namespace SpencerDenim.Application.Catalog.Admin;

/// <summary>Every department the admin screens list, active or not (ADM-11).</summary>
public sealed record GetAdminDepartmentsQuery;

/// <summary>One department's whole admin category tree (ADM-11).</summary>
public sealed record GetAdminCategoryTreeQuery(int DepartmentId);

/// <summary>What deactivating one category would do to the storefront (ADM-11, doc 08 §7.1).</summary>
public sealed record GetCategoryDeactivationImpactQuery(int CategoryId);

/// <summary>The age groups and size guides a category form may choose from (ADM-11, doc 11 P1 step 2).</summary>
public sealed record GetCategoryFormOptionsQuery;

/// <summary>
/// Hands the admin department list straight to the read port.
/// <para>
/// A handler with no logic in it is still the right shape here: it is what keeps the endpoint depending on
/// <see cref="IQueryHandler{TQuery,TResult}"/> like every other route rather than on an infrastructure
/// adapter, and it is where paging or a filter would go when one is asked for.
/// </para>
/// </summary>
public sealed class GetAdminDepartmentsQueryHandler(IAdminCatalogReader reader)
    : IQueryHandler<GetAdminDepartmentsQuery, IReadOnlyList<AdminDepartmentDto>>
{
    public Task<IReadOnlyList<AdminDepartmentDto>> HandleAsync(
        GetAdminDepartmentsQuery query,
        CancellationToken cancellationToken) =>
        reader.GetDepartmentsAsync(cancellationToken);
}

/// <inheritdoc cref="GetAdminDepartmentsQueryHandler"/>
public sealed class GetAdminCategoryTreeQueryHandler(IAdminCatalogReader reader)
    : IQueryHandler<GetAdminCategoryTreeQuery, AdminCategoryTreeDto?>
{
    public Task<AdminCategoryTreeDto?> HandleAsync(
        GetAdminCategoryTreeQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.GetCategoryTreeAsync(query.DepartmentId, cancellationToken);
    }
}

/// <inheritdoc cref="GetAdminDepartmentsQueryHandler"/>
public sealed class GetCategoryDeactivationImpactQueryHandler(IAdminCatalogReader reader)
    : IQueryHandler<GetCategoryDeactivationImpactQuery, CategoryDeactivationImpactDto?>
{
    public Task<CategoryDeactivationImpactDto?> HandleAsync(
        GetCategoryDeactivationImpactQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.GetDeactivationImpactAsync(query.CategoryId, cancellationToken);
    }
}

/// <inheritdoc cref="GetAdminDepartmentsQueryHandler"/>
public sealed class GetCategoryFormOptionsQueryHandler(IAdminCatalogReader reader)
    : IQueryHandler<GetCategoryFormOptionsQuery, CategoryFormOptionsDto>
{
    public Task<CategoryFormOptionsDto> HandleAsync(
        GetCategoryFormOptionsQuery query,
        CancellationToken cancellationToken) =>
        reader.GetFormOptionsAsync(cancellationToken);
}
