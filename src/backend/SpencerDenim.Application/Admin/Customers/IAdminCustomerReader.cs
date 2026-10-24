using SpencerDenim.Application.Admin.Customers.Contracts;

namespace SpencerDenim.Application.Admin.Customers;

/// <summary>
/// Every read the admin customers screen needs (ADM-15). Reads only: nothing on this screen writes, and
/// nothing here writes to <c>identity</c>.
/// </summary>
/// <remarks>
/// <b>A customer is a registered account</b> — <c>identity.Users</c> with <c>UserType = Customer</c>. Doc 08
/// §3.1 tags that table ADM-15 and computes the statistics "from <c>sales.Orders</c> for the rows on screen
/// (<c>IX_Orders_Customer_Placed</c>)", an index keyed on <c>CustomerUserId</c>; doc 11 P11 step 16's detail
/// shows <i>addresses</i>, which only an account has. A guest who never registered is found by searching
/// the order list by email, and is anonymised by email under doc 11 P17 — neither is this screen.
/// </remarks>
public interface IAdminCustomerReader
{
    Task<AdminCustomerListPage> ReadListAsync(
        AdminCustomerSearchTerm? search,
        int? districtId,
        string? countryCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>One customer, or <see langword="null"/> when there is no customer account with that id.</summary>
    /// <remarks>A <b>staff</b> account's id is answered with null too: this screen never shows a staff user.</remarks>
    Task<AdminCustomerDetailDto?> ReadDetailAsync(int customerId, CancellationToken cancellationToken);

    /// <summary>
    /// One page of a customer's orders, or <see langword="null"/> when there is no such customer.
    /// </summary>
    Task<AdminCustomerOrderPage?> ReadOrdersAsync(
        int customerId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
