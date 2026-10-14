using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Customers.Contracts;

namespace SpencerDenim.Application.Admin.Customers;

/// <summary>The customer list (ADM-15, doc 11 P11 step 16; <c>Customers.View</c>).</summary>
/// <param name="Search">What the staff member typed; see <see cref="AdminCustomerSearch"/>.</param>
/// <param name="DistrictId">Only customers whose default district is this one, or null for every customer.</param>
/// <param name="Page">1-based. Clamped, never refused.</param>
/// <param name="PageSize">Clamped to <see cref="AdminCustomerPaging.MaxPageSize"/>.</param>
public sealed record GetAdminCustomersQuery(string? Search, int? DistrictId, int? Page, int? PageSize);

/// <summary>A customer's detail.</summary>
public sealed record GetAdminCustomerQuery(int CustomerId);

/// <summary>One page of a customer's order history.</summary>
public sealed record GetAdminCustomerOrdersQuery(int CustomerId, int? Page, int? PageSize);

/// <summary>
/// The paging rules both customer lists share.
/// </summary>
/// <remarks>
/// <b>Clamped rather than refused</b>, the decision the order list took: a staff member reaches these
/// lists from a link, and a 400 for a hand-edited <c>?page=0</c> would be an error page with no customers
/// on it.
/// </remarks>
public static class AdminCustomerPaging
{
    public const int DefaultPageSize = 25;

    /// <summary>
    /// Each row is a name, an address, a telephone number and a money figure, so an unbounded page is a
    /// bulk copy of customer data anyone with <c>Customers.View</c> could make arbitrarily large. Doc 11
    /// P11 puts a customer export behind a permission of its own; this ceiling is what keeps the list from
    /// being one.
    /// </summary>
    public const int MaxPageSize = 100;

    public static (int Page, int PageSize) Clamp(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));
}

public sealed class GetAdminCustomersQueryHandler(IAdminCustomerReader reader)
    : IQueryHandler<GetAdminCustomersQuery, AdminCustomerListPage>
{
    public Task<AdminCustomerListPage> HandleAsync(GetAdminCustomersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = AdminCustomerPaging.Clamp(query.Page, query.PageSize);

        return reader.ReadListAsync(
            AdminCustomerSearch.Classify(query.Search),
            query.DistrictId,
            page,
            pageSize,
            cancellationToken);
    }
}

public sealed class GetAdminCustomerQueryHandler(IAdminCustomerReader reader)
    : IQueryHandler<GetAdminCustomerQuery, AdminCustomerDetailDto?>
{
    public Task<AdminCustomerDetailDto?> HandleAsync(GetAdminCustomerQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.ReadDetailAsync(query.CustomerId, cancellationToken);
    }
}

public sealed class GetAdminCustomerOrdersQueryHandler(IAdminCustomerReader reader)
    : IQueryHandler<GetAdminCustomerOrdersQuery, AdminCustomerOrderPage?>
{
    public Task<AdminCustomerOrderPage?> HandleAsync(
        GetAdminCustomerOrdersQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = AdminCustomerPaging.Clamp(query.Page, query.PageSize);

        return reader.ReadOrdersAsync(query.CustomerId, page, pageSize, cancellationToken);
    }
}
