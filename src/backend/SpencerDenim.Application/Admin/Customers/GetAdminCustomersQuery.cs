using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Customers.Contracts;

namespace SpencerDenim.Application.Admin.Customers;

/// <summary>The customer list (ADM-15, doc 11 P11 step 16; <c>Customers.View</c>).</summary>
/// <param name="Search">What the staff member typed; see <see cref="AdminCustomerSearch"/>.</param>
/// <param name="DistrictId">Only customers whose default district is this one, or null for every customer.</param>
/// <param name="Page">1-based. Clamped, never refused.</param>
/// <param name="PageSize">Clamped to <see cref="AdminCustomerPaging.MaxPageSize"/>.</param>
/// <param name="CountryCode">
/// Only customers whose <b>default saved address</b> is in this country (ISO 3166-1 alpha-2, any case), or null
/// or blank for every customer. A district is a Sri Lankan thing, so overseas customers are reached through this.
/// </param>
public sealed record GetAdminCustomersQuery(
    string? Search,
    int? DistrictId,
    int? Page,
    int? PageSize,
    string? CountryCode = null);

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

    /// <summary>
    /// The highest page honoured: <c>(page - 1) * pageSize</c> must fit an <c>int</c> at the largest page
    /// size, or it wraps negative and SQL Server refuses the OFFSET (W3-07 tester: page=2147483647 with
    /// pageSize=100 answered 500). Twenty-one million pages of a hundred is far past any real list.
    /// </summary>
    public const int MaxPage = int.MaxValue / MaxPageSize;

    public static (int Page, int PageSize) Clamp(int? page, int? pageSize) =>
        (Math.Clamp(page ?? 1, 1, MaxPage), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));
}

public sealed class GetAdminCustomersQueryHandler(IAdminCustomerReader reader)
    : IQueryHandler<GetAdminCustomersQuery, AdminCustomerListPage>
{
    /// <summary>Blank is "every country"; anything else is compared upper-case, as the code is stored.</summary>
    private static string? NormaliseCountryCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    public Task<AdminCustomerListPage> HandleAsync(GetAdminCustomersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = AdminCustomerPaging.Clamp(query.Page, query.PageSize);

        return reader.ReadListAsync(
            AdminCustomerSearch.Classify(query.Search),
            query.DistrictId,
            NormaliseCountryCode(query.CountryCode),
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
