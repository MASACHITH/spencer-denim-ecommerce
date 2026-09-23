using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Catalog.Departments;

/// <summary>Query behind <c>GET /api/catalog/departments</c> (GLB-05 mega menu, DEPT-01).</summary>
public sealed record GetDepartmentsQuery;

/// <summary>Reads the navigation tree. The tree is about 200 rows, so it is fetched whole (doc 08 §3.2).</summary>
public sealed class GetDepartmentsQueryHandler(IDepartmentCatalogReader reader)
    : IQueryHandler<GetDepartmentsQuery, IReadOnlyList<DepartmentDto>>
{
    public Task<IReadOnlyList<DepartmentDto>> HandleAsync(GetDepartmentsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.GetDepartmentsAsync(cancellationToken);
    }
}
