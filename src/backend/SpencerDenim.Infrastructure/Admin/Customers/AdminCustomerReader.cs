using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Admin.Customers;
using SpencerDenim.Application.Admin.Customers.Contracts;
using SpencerDenim.Application.Sales.Orders.Contracts;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Admin.Customers;

/// <summary>
/// The admin customers screen's reads (ADM-15, doc 11 P11 step 16).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every query filters and orders on the entity and projects last.</b> #126's promo-code API answered
/// 500 for a day and a half because a positional-record projection had <c>OrderBy</c> applied after the
/// <c>Select</c>, which EF cannot translate; <c>AdminCustomerEndpointTests</c> is this reader's backend
/// test, route by route.
/// </para>
/// <para>
/// <b>A customer's orders are the orders whose <c>CustomerUserId</c> is that customer, and nothing else.</b>
/// Never matched by email: a guest order placed with the same address joins the account only when doc 08
/// §3.1 D5 links it, after the customer confirms the address. Matching by email here would show staff, on
/// the account, orders that D5 deliberately keeps off it — and an address is only a claim until it is
/// confirmed. <c>AdminCustomerEndpointTests</c> seeds exactly that decoy.
/// </para>
/// <para>
/// <b>The statistics are computed live</b> for the rows on the page (doc 08 §3.1, simplification 4), as
/// three correlated aggregates per row on <c>IX_Orders_Customer_Placed</c> — keyed on
/// <c>(CustomerUserId, PlacedAtUtc)</c> and including <c>GrandTotal</c> and <c>PaidAtUtc</c>, which is every
/// column the aggregates read. <c>AdminCustomerQueryPlanTests</c> reads the plan of this reader's own
/// statement and asserts that index is the one used.
/// </para>
/// </remarks>
internal sealed class AdminCustomerReader(AppDbContext dbContext) : IAdminCustomerReader
{
    public async Task<AdminCustomerListPage> ReadListAsync(
        AdminCustomerSearchTerm? search,
        int? districtId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var total = await Matching(search, districtId).CountAsync(cancellationToken);

        var rows = total == 0
            ? []
            : await PageQuery(search, districtId, page, pageSize).ToListAsync(cancellationToken);

        var districts = await dbContext.Districts
            .AsNoTracking()
            .OrderBy(district => district.SortOrder)
            .ThenBy(district => district.Name)
            .Select(district => new AdminDistrictOption(district.Id, district.Name))
            .ToListAsync(cancellationToken);

        return new AdminCustomerListPage(
            rows
                .Select(row => new AdminCustomerListRow(
                    row.Id,
                    FullName(row.FirstName, row.LastName),
                    row.Email ?? string.Empty,
                    row.EmailConfirmed,
                    row.Phone,
                    row.DistrictName,
                    row.IsActive,
                    row.CreatedAtUtc,
                    row.OrderCount,
                    row.LifetimeSpend ?? 0m,
                    SalesCurrency.Lkr,
                    row.LastOrderAtUtc,
                    row.CountryName))
                .ToList(),
            page,
            pageSize,
            total,
            districts);
    }

    public async Task<AdminCustomerDetailDto?> ReadDetailAsync(int customerId, CancellationToken cancellationToken)
    {
        var row = await Customers()
            .Where(user => user.Id == customerId)
            .Select(user => new CustomerShape
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                EmailConfirmed = user.EmailConfirmed,
                Phone = user.PhoneNumber,
                IsActive = user.IsActive,
                CreatedAtUtc = user.CreatedAtUtc,
                LastLoginAtUtc = user.LastLoginAtUtc,
                DistrictName = dbContext.Districts
                    .Where(district => district.Id == user.DefaultDistrictId)
                    .Select(district => district.Name)
                    .FirstOrDefault(),

                // Owner decision 2026-10-21: the country of the customer's default saved address, on the
                // filtered unique index UX_CustomerAddresses_UserId_Default — one row at most.
                CountryName = dbContext.CustomerAddresses
                    .Where(address => address.UserId == user.Id && address.IsDefault)
                    .Select(address => address.Country!.Name)
                    .FirstOrDefault(),
                OrderCount = dbContext.Orders.Count(order => order.CustomerUserId == user.Id),
                LifetimeSpend = dbContext.Orders
                    .Where(order => order.CustomerUserId == user.Id && order.PaidAtUtc != null)
                    .Sum(order => (decimal?)order.GrandTotal),
                LastOrderAtUtc = dbContext.Orders
                    .Where(order => order.CustomerUserId == user.Id)
                    .Max(order => (DateTime?)order.PlacedAtUtc),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        // Default first, then in the order they were added — the order the customer's own address book
        // shows them in, so staff on the telephone and the customer are looking at the same list.
        var addresses = await dbContext.CustomerAddresses
            .AsNoTracking()
            .Where(address => address.UserId == customerId)
            .OrderByDescending(address => address.IsDefault)
            .ThenBy(address => address.Id)
            .Select(address => new AdminCustomerAddressDto(
                address.Id,
                address.Label,
                address.RecipientName,
                address.Phone,
                address.Line1,
                address.Line2,
                address.City,
                // Null for an overseas address, which has no district (owner decision 2026-10-21).
                address.District != null ? address.District.Name : null,
                address.PostalCode,
                address.Landmark,
                address.IsDefault,
                address.CountryCode,
                address.Country!.Name,
                address.Region))
            .ToListAsync(cancellationToken);

        return new AdminCustomerDetailDto(
            row.Id,
            row.FirstName,
            row.LastName,
            row.Email ?? string.Empty,
            row.EmailConfirmed,
            row.Phone,
            row.DistrictName,
            row.IsActive,
            row.CreatedAtUtc,
            row.LastLoginAtUtc,
            row.OrderCount,
            row.LifetimeSpend ?? 0m,
            SalesCurrency.Lkr,
            row.LastOrderAtUtc,
            addresses);
    }

    public async Task<AdminCustomerOrderPage?> ReadOrdersAsync(
        int customerId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (!await Customers().AnyAsync(user => user.Id == customerId, cancellationToken))
        {
            return null;
        }

        // The account's own orders, by the column D5 writes. See the class remarks for why never by email.
        var theirs = dbContext.Orders
            .AsNoTracking()
            .Where(order => order.CustomerUserId == customerId);

        var total = await theirs.CountAsync(cancellationToken);

        var rows = total == 0
            ? []
            : await theirs
                // Newest first, with the key as a tie-break so two orders placed in the same millisecond
                // cannot swap pages between requests. Ordered on the entity, before the projection.
                .OrderByDescending(order => order.PlacedAtUtc)
                .ThenByDescending(order => order.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(order => new
                {
                    order.PublicId,
                    order.OrderNumber,
                    order.Status,
                    order.Delivery.DeliveryMethodCode,
                    order.GrandTotal,
                    order.Currency,
                    order.PlacedAtUtc,
                    order.PaidAtUtc,
                })
                .ToListAsync(cancellationToken);

        return new AdminCustomerOrderPage(
            rows
                .Select(row => new AdminCustomerOrderRow(
                    row.PublicId,
                    row.OrderNumber,
                    row.Status.ToString(),
                    OrderStatusLabels.For(
                        row.Status,
                        string.Equals(row.DeliveryMethodCode, DeliveryMethod.PickupCode, StringComparison.Ordinal)),
                    row.GrandTotal,
                    row.Currency,
                    row.PlacedAtUtc,
                    row.PaidAtUtc))
                .ToList(),
            page,
            pageSize,
            total);
    }

    /// <summary>
    /// One page of the list, filtered, ordered and <i>then</i> projected — the statement
    /// <see cref="ReadListAsync"/> runs, exposed so <c>AdminCustomerQueryPlanTests</c> can read the plan of
    /// this reader's own query rather than of a copy of it written in the test.
    /// </summary>
    internal IQueryable<CustomerShape> PageQuery(
        AdminCustomerSearchTerm? search,
        int? districtId,
        int page,
        int pageSize) =>
        Matching(search, districtId)
            // Newest registration first: IX_Users_UserType_CreatedAtUtc is keyed (UserType, CreatedAtUtc),
            // so the unfiltered list is an ordered range read of the customer half of that index. The id
            // breaks ties so paging is stable.
            .OrderByDescending(user => user.CreatedAtUtc)
            .ThenByDescending(user => user.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(user => new CustomerShape
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                EmailConfirmed = user.EmailConfirmed,
                Phone = user.PhoneNumber,
                IsActive = user.IsActive,
                CreatedAtUtc = user.CreatedAtUtc,
                LastLoginAtUtc = user.LastLoginAtUtc,
                DistrictName = dbContext.Districts
                    .Where(district => district.Id == user.DefaultDistrictId)
                    .Select(district => district.Name)
                    .FirstOrDefault(),

                // Owner decision 2026-10-21: the country of the customer's default saved address, on the
                // filtered unique index UX_CustomerAddresses_UserId_Default — one row at most.
                CountryName = dbContext.CustomerAddresses
                    .Where(address => address.UserId == user.Id && address.IsDefault)
                    .Select(address => address.Country!.Name)
                    .FirstOrDefault(),
                OrderCount = dbContext.Orders.Count(order => order.CustomerUserId == user.Id),
                LifetimeSpend = dbContext.Orders
                    .Where(order => order.CustomerUserId == user.Id && order.PaidAtUtc != null)
                    .Sum(order => (decimal?)order.GrandTotal),
                LastOrderAtUtc = dbContext.Orders
                    .Where(order => order.CustomerUserId == user.Id)
                    .Max(order => (DateTime?)order.PlacedAtUtc),
            });

    /// <summary>Customer accounts only. A staff account is never on this screen.</summary>
    private IQueryable<Identity.AppUser> Customers() =>
        dbContext.Users
            .AsNoTracking()
            .Where(user => user.UserType == UserType.Customer);

    private IQueryable<Identity.AppUser> Matching(AdminCustomerSearchTerm? search, int? districtId)
    {
        var customers = Customers();

        if (districtId is { } district)
        {
            customers = customers.Where(user => user.DefaultDistrictId == district);
        }

        if (search is null)
        {
            return customers;
        }

        var value = search.Value;
        var upper = value.ToUpperInvariant();

        return search.Kind switch
        {
            // A prefix of the normalised address, which LIKE 'X%' can seek on UX_Users_NormalizedEmail.
            AdminCustomerSearchKind.Email => customers.Where(user =>
                user.NormalizedEmail != null && user.NormalizedEmail.StartsWith(value)),

            // The last digits, so a caller's 0771234567 finds a stored +94771234567. A suffix cannot seek
            // an index, and there is none on PhoneNumber to seek: this is a scan of the customer rows,
            // which is the cost of finding a customer by the number they are calling from.
            //
            // **The stored number is compared without its separators.** Registration keeps the number as
            // typed (Trim only) and LooksLikePhone accepts spaces, hyphens, dots, slashes and parentheses,
            // so "077 123 4567" is a stored value a plain EndsWith never matched (W3-07 tester). The six
            // REPLACEs are exactly IsPhonePunctuation's set; a leading '+' is before the suffix and harmless.
            AdminCustomerSearchKind.Phone => customers.Where(user =>
                user.PhoneNumber != null
                && user.PhoneNumber
                    .Replace(" ", string.Empty)
                    .Replace("-", string.Empty)
                    .Replace(".", string.Empty)
                    .Replace("/", string.Empty)
                    .Replace("(", string.Empty)
                    .Replace(")", string.Empty)
                    .EndsWith(value)),

            // Part of a name — first, last, or "first last" typed together — or part of the address, since
            // "nimali" is as likely to be the start of an email as a first name. A scan of the customer
            // rows; EF escapes the LIKE wildcards in the term.
            _ => customers.Where(user =>
                (user.FirstName + " " + (user.LastName ?? string.Empty)).Contains(value)
                || (user.NormalizedEmail != null && user.NormalizedEmail.Contains(upper))),
        };
    }

    private static string FullName(string firstName, string? lastName) =>
        string.IsNullOrWhiteSpace(lastName) ? firstName : $"{firstName} {lastName}";

    /// <summary>
    /// One customer row as the database returns it. A class with settable members rather than a positional
    /// record, so EF can compose it and the projection reads as the column list it is.
    /// </summary>
    internal sealed class CustomerShape
    {
        public int Id { get; init; }

        public string FirstName { get; init; } = string.Empty;

        public string? LastName { get; init; }

        public string? Email { get; init; }

        public bool EmailConfirmed { get; init; }

        public string? Phone { get; init; }

        public bool IsActive { get; init; }

        public DateTime CreatedAtUtc { get; init; }

        public DateTime? LastLoginAtUtc { get; init; }

        public string? DistrictName { get; init; }

        public string? CountryName { get; init; }

        public int OrderCount { get; init; }

        public decimal? LifetimeSpend { get; init; }

        public DateTime? LastOrderAtUtc { get; init; }
    }
}
