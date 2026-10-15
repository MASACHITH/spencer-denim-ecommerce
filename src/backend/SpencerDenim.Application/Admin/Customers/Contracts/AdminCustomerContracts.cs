namespace SpencerDenim.Application.Admin.Customers.Contracts;

/// <summary>
/// One page of the admin customer list (ADM-15, doc 11 P11 step 16; <c>Customers.View</c>).
/// </summary>
/// <param name="Rows">Newest registration first.</param>
/// <param name="Page">1-based.</param>
/// <param name="PageSize">How many rows a page holds.</param>
/// <param name="TotalCount">Every customer matching the search and the district filter, not only this page's.</param>
/// <param name="Districts">
/// The district filter's options, from <c>ref.Districts</c> in its own sort order, so the screen invents
/// none. Twenty-five short rows; sent with every page rather than from a route of its own because the
/// filter is useless without them and the screen would otherwise need two round trips to draw itself.
/// </param>
public sealed record AdminCustomerListPage(
    IReadOnlyList<AdminCustomerListRow> Rows,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<AdminDistrictOption> Districts);

/// <summary>A district the list can be filtered by.</summary>
public sealed record AdminDistrictOption(int Id, string Name);

/// <summary>
/// One registered customer on the list.
/// </summary>
/// <param name="CustomerId">
/// <c>identity.Users.Id</c>. An admin URL carries it, as the staff-user and product screens' do; GLB-11's
/// "no ids in URLs" is a storefront rule.
/// </param>
/// <param name="Name">First and last name as the account holds them.</param>
/// <param name="Email">The account's address.</param>
/// <param name="EmailConfirmed">
/// Whether the address is confirmed. It is the one fact that explains a support call about missing orders:
/// doc 08 §3.1 D5 links earlier guest orders <b>only after</b> confirmation, so an unconfirmed customer's
/// guest orders are, correctly, not on their account.
/// </param>
/// <param name="Phone">The profile's telephone number, or <see langword="null"/>.</param>
/// <param name="DistrictName">
/// The customer's <b>default delivery district</b> (<c>identity.Users.DefaultDistrictId</c>, doc 08 §3.1;
/// set on the profile, doc 11 P11 step 10), or <see langword="null"/> when they have not chosen one. This
/// is the only district a customer account has; a saved address's district is shown on the detail.
/// </param>
/// <param name="IsActive">False for an account staff or the system have disabled.</param>
/// <param name="RegisteredAtUtc">When the account was created.</param>
/// <param name="OrderCount">
/// Every order on the account, in any status — doc 08 §8.1 counts orders by placement, paid or not.
/// Computed live (doc 08 §3.1, simplification 4).
/// </param>
/// <param name="LifetimeSpend">
/// Σ <c>GrandTotal</c> of the account's orders that were <b>paid</b> (<c>PaidAtUtc</c> set), which is doc 08
/// §8.1's gross-revenue rule applied to one customer: an order later refunded still counts, because
/// refunds are recorded manually in v1 and there is no refund row to subtract. Computed live.
/// </param>
/// <param name="Currency">Always LKR (<c>CK_Orders_Currency</c>); sent so the screen never assumes.</param>
/// <param name="LastOrderAtUtc">When the newest order on the account was placed, or <see langword="null"/>.</param>
public sealed record AdminCustomerListRow(
    int CustomerId,
    string Name,
    string Email,
    bool EmailConfirmed,
    string? Phone,
    string? DistrictName,
    bool IsActive,
    DateTime RegisteredAtUtc,
    int OrderCount,
    decimal LifetimeSpend,
    string Currency,
    DateTime? LastOrderAtUtc);

/// <summary>
/// One customer in full (ADM-15, doc 11 P11 step 16: "detail with addresses and order history").
/// </summary>
/// <remarks>
/// <b>The order history is not on this payload.</b> It is paged from its own route
/// (<see cref="AdminCustomerOrderPage"/>), because a long-standing customer's history has no upper bound.
/// <para>
/// <b>There is no anonymise action here, and nothing on this payload says one is possible.</b> That is
/// doc 11 P17, behind <c>Customers.Delete</c>, and it is a separate task.
/// </para>
/// </remarks>
/// <param name="LastSignInAtUtc">
/// <c>identity.Users.LastLoginAtUtc</c>, which the customer sign-in writes, or <see langword="null"/>.
/// </param>
public sealed record AdminCustomerDetailDto(
    int CustomerId,
    string FirstName,
    string? LastName,
    string Email,
    bool EmailConfirmed,
    string? Phone,
    string? DistrictName,
    bool IsActive,
    DateTime RegisteredAtUtc,
    DateTime? LastSignInAtUtc,
    int OrderCount,
    decimal LifetimeSpend,
    string Currency,
    DateTime? LastOrderAtUtc,
    IReadOnlyList<AdminCustomerAddressDto> Addresses);

/// <summary>One saved address (<c>identity.CustomerAddresses</c>, doc 08 §3.1). Default first.</summary>
public sealed record AdminCustomerAddressDto(
    int AddressId,
    string? Label,
    string RecipientName,
    string Phone,
    string Line1,
    string? Line2,
    string City,
    string DistrictName,
    string? PostalCode,
    string? Landmark,
    bool IsDefault);

/// <summary>
/// One page of a customer's order history, newest first.
/// </summary>
/// <param name="Rows">The orders, newest placed first.</param>
/// <param name="Page">1-based.</param>
/// <param name="PageSize">How many rows a page holds.</param>
/// <param name="TotalCount">Every order on the account.</param>
public sealed record AdminCustomerOrderPage(
    IReadOnlyList<AdminCustomerOrderRow> Rows,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>
/// One order on a customer's history.
/// </summary>
/// <remarks>
/// <b>Only orders whose <c>CustomerUserId</c> is this customer</b> — never matched by email. A guest
/// order placed with the same address belongs to the account only once doc 08 §3.1 D5 has linked it, which
/// happens after the customer confirms the address; matching by email here would put on the account the
/// very orders D5 deliberately keeps off it.
/// </remarks>
/// <param name="OrderPublicId">The token the admin order screen is opened with.</param>
public sealed record AdminCustomerOrderRow(
    Guid OrderPublicId,
    string OrderNumber,
    string Status,
    string StatusLabel,
    decimal GrandTotal,
    string Currency,
    DateTime PlacedAtUtc,
    DateTime? PaidAtUtc);
