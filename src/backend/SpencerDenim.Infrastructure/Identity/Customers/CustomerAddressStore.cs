using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Accounts.Abstractions;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Reference;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Identity.Customers;

/// <summary>
/// <c>identity.CustomerAddresses</c>: a customer's address book (ACC-02, doc 11 P11 step 11).
/// </summary>
/// <remarks>
/// <b>Three rules, and each one needs the transaction it is written in.</b>
/// <list type="number">
/// <item><description>
/// <b>Ten per customer</b> (doc 08 §3.1's app cap). SQL Server cannot express "at most ten rows per
/// owner" declaratively, so the count and the insert are one transaction behind an application lock on
/// the owner. Without the lock, two tabs adding an eleventh address both count ten and both insert, and
/// the shopper ends up with eleven — a cap that holds except when somebody is quick is not a cap.
/// </description></item>
/// <item><description>
/// <b>One default per customer</b>, which <c>UX_CustomerAddresses_UserId_Default</c> enforces as a unique
/// filtered index. That makes the <i>order</i> of the two statements load-bearing: setting the new
/// default before clearing the old one violates the index. Clear, then set, in one transaction.
/// </description></item>
/// <item><description>
/// <b>Every statement filters on <c>UserId</c>.</b> Not "check the owner, then write by id" — the owner
/// is in the <c>WHERE</c> clause of the write itself, so there is no window between the check and the
/// write and no shape in which a caller can be trusted to have checked.
/// </description></item>
/// </list>
/// <para>
/// <b>The lock resource is per customer, so two shoppers never wait on each other.</b> It mirrors
/// <c>StaffUserRoleManagementStore</c>'s <c>rbac:manage-user:{id}</c>, with a prefix of its own:
/// serialising all address writes on one resource would be a global bottleneck on a screen every
/// customer uses.
/// </para>
/// </remarks>
internal sealed class CustomerAddressStore(AppDbContext dbContext, CustomerUserLookup lookup)
    : ICustomerAddressStore
{
    public async Task<IReadOnlyList<CustomerAddressDto>> ListAsync(
        int userId,
        CancellationToken cancellationToken) =>
        await ReadAsync(userId, cancellationToken);

    public async Task<CustomerAddressResult> AddAsync(
        int userId,
        CustomerAddressEdit edit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        return await InTransactionAsync(userId, async () =>
        {
            if (!await IsServedDestinationAsync(edit, cancellationToken))
            {
                return new CustomerAddressResult.UnknownDistrict();
            }

            var existing = await dbContext.CustomerAddresses
                .CountAsync(address => address.UserId == userId, cancellationToken);

            if (existing >= CustomerAddress.MaxPerCustomer)
            {
                return new CustomerAddressResult.LimitReached(CustomerAddress.MaxPerCustomer);
            }

            // The first address a customer saves is their default whatever the form said, because an
            // address book with one entry and no default would leave checkout nothing to pre-select and
            // the shopper nothing to understand. Doc 11 P11 step 11 asks for "one default"; it does not
            // ask the shopper to opt in to having one.
            var isDefault = edit.IsDefault || existing == 0;

            if (isDefault)
            {
                await ClearDefaultAsync(userId, cancellationToken);
            }

            dbContext.CustomerAddresses.Add(new CustomerAddress
            {
                UserId = userId,
                Label = Trimmed(edit.Label),
                RecipientName = edit.RecipientName.Trim(),
                Phone = edit.Phone.Trim(),
                Line1 = edit.Line1.Trim(),
                Line2 = Trimmed(edit.Line2),
                City = edit.City.Trim(),
                CountryCode = edit.CountryCode,
                DistrictId = edit.DistrictId,
                Region = Trimmed(edit.Region),
                PostalCode = Trimmed(edit.PostalCode),
                Landmark = Trimmed(edit.Landmark),
                IsDefault = isDefault,
            });

            await dbContext.SaveChangesAsync(cancellationToken);

            return new CustomerAddressResult.Saved(await ReadAsync(userId, cancellationToken));
        });
    }

    public async Task<CustomerAddressResult> UpdateAsync(
        int userId,
        int addressId,
        CustomerAddressEdit edit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        return await InTransactionAsync(userId, async () =>
        {
            if (!await IsServedDestinationAsync(edit, cancellationToken))
            {
                return new CustomerAddressResult.UnknownDistrict();
            }

            var address = await dbContext.CustomerAddresses
                .FirstOrDefaultAsync(
                    row => row.Id == addressId && row.UserId == userId,
                    cancellationToken);

            if (address is null)
            {
                return new CustomerAddressResult.NotFound();
            }

            // Doc 11 P11 step 11 has no "unset the default" action, and there must not be one: a book
            // with entries and no default leaves checkout nothing to pre-select. Clearing the tick on
            // the current default therefore leaves it as the default — the way to move it is to tick
            // another address, which is what `SetDefaultAsync` and the branch below do.
            var becomesDefault = edit.IsDefault && !address.IsDefault;

            if (becomesDefault)
            {
                await ClearDefaultAsync(userId, cancellationToken);

                // Re-read: the set-based clear above does not update the tracked instance, so saving it
                // without this would write the stale `IsDefault` back. The clear cannot have removed this
                // row — it was not the default.
                address = await dbContext.CustomerAddresses
                    .FirstAsync(row => row.Id == addressId && row.UserId == userId, cancellationToken);
            }

            address.Label = Trimmed(edit.Label);
            address.RecipientName = edit.RecipientName.Trim();
            address.Phone = edit.Phone.Trim();
            address.Line1 = edit.Line1.Trim();
            address.Line2 = Trimmed(edit.Line2);
            address.City = edit.City.Trim();
            address.CountryCode = edit.CountryCode;
            address.DistrictId = edit.DistrictId;
            address.Region = Trimmed(edit.Region);
            address.PostalCode = Trimmed(edit.PostalCode);
            address.Landmark = Trimmed(edit.Landmark);
            address.IsDefault = address.IsDefault || becomesDefault;

            await dbContext.SaveChangesAsync(cancellationToken);

            return new CustomerAddressResult.Saved(await ReadAsync(userId, cancellationToken));
        });
    }

    public async Task<CustomerAddressResult> DeleteAsync(
        int userId,
        int addressId,
        CancellationToken cancellationToken) =>
        await InTransactionAsync(userId, async () =>
        {
            var removed = await dbContext.CustomerAddresses
                .Where(row => row.Id == addressId && row.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);

            if (removed == 0)
            {
                return new CustomerAddressResult.NotFound();
            }

            await PromoteADefaultAsync(userId, cancellationToken);

            return new CustomerAddressResult.Saved(await ReadAsync(userId, cancellationToken));
        });

    public async Task<CustomerAddressResult> SetDefaultAsync(
        int userId,
        int addressId,
        CancellationToken cancellationToken) =>
        await InTransactionAsync(userId, async () =>
        {
            var exists = await dbContext.CustomerAddresses
                .AnyAsync(row => row.Id == addressId && row.UserId == userId, cancellationToken);

            if (!exists)
            {
                return new CustomerAddressResult.NotFound();
            }

            // Clear before set: UX_CustomerAddresses_UserId_Default is unique over (UserId) WHERE
            // IsDefault = 1, so the other order violates it.
            await ClearDefaultAsync(userId, cancellationToken);

            await dbContext.CustomerAddresses
                .Where(row => row.Id == addressId && row.UserId == userId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(row => row.IsDefault, true),
                    cancellationToken);

            return new CustomerAddressResult.Saved(await ReadAsync(userId, cancellationToken));
        });

    /// <summary>
    /// Runs <paramref name="work"/> inside a transaction, behind an exclusive lock on this customer's
    /// address book, with the retry strategy doc 08 §10.3 requires around the whole block.
    /// </summary>
    /// <remarks>
    /// <b>The customer is re-checked inside the transaction and not before it.</b> A session cookie says
    /// who the caller claims to be; whether that is still an active customer is a fact about the database
    /// at the moment of the write, and the foreign key would otherwise refuse the insert with an error no
    /// shopper can read.
    /// <para>
    /// <b><c>ChangeTracker.Clear()</c> on every attempt</b>, for the reason
    /// <c>StaffUserRoleManagementStore</c> records from its round-3 review: a retry would otherwise
    /// re-apply the failed attempt's tracked changes on top of state that was rolled back.
    /// </para>
    /// </remarks>
    private async Task<CustomerAddressResult> InTransactionAsync(
        int userId,
        Func<Task<CustomerAddressResult>> work)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(CancellationToken.None);

            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"EXEC sp_getapplock @Resource = {LockResource(userId)}, @LockMode = N'Exclusive', @LockOwner = N'Transaction';",
                CancellationToken.None);

            if (await lookup.FindCustomerByIdAsync(userId, CancellationToken.None) is null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new CustomerAddressResult.NotFound();
            }

            var result = await work();

            if (result is CustomerAddressResult.Saved)
            {
                await transaction.CommitAsync(CancellationToken.None);
            }
            else
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            return result;
        });
    }

    private static string LockResource(int userId) =>
        string.Create(CultureInfo.InvariantCulture, $"account:addresses:{userId}");

    /// <summary>
    /// Whether the district is one the shop serves: it exists and is active.
    /// </summary>
    /// <remarks>
    /// <b><c>IsActive</c> matters as much as existence.</b> An inactive district satisfies the foreign key
    /// W3-01 added and then has no delivery rate (doc 08 §3.5 N4), so an address saved against one could
    /// never be quoted for at checkout. <c>ref.Districts.IsActive</c> is admin-editable, so this is an
    /// ordinary state rather than an error — the address <i>already saved</i> against a district the shop
    /// later stops serving is kept, and the card says so (see <see cref="CustomerAddressDto"/>).
    /// </remarks>
    /// <summary>
    /// A Sri Lankan address needs an active district; an overseas one needs a country that exists and no
    /// district (owner decision 2026-10-21). The use case has already resolved the country, so the second branch
    /// is the belt to its braces.
    /// </summary>
    private async Task<bool> IsServedDestinationAsync(CustomerAddressEdit edit, CancellationToken cancellationToken)
    {
        if (!string.Equals(edit.CountryCode, Country.SriLankaCode, StringComparison.Ordinal))
        {
            return edit.DistrictId is null
                && await dbContext.Countries.AnyAsync(country => country.Code == edit.CountryCode, cancellationToken);
        }

        return edit.DistrictId is { } districtId
            && await dbContext.Districts.AnyAsync(
                district => district.Id == districtId && district.IsActive,
                cancellationToken);
    }

    private async Task ClearDefaultAsync(int userId, CancellationToken cancellationToken) =>
        await dbContext.CustomerAddresses
            .Where(row => row.UserId == userId && row.IsDefault)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsDefault, false),
                cancellationToken);

    /// <summary>
    /// Makes sure a customer with any addresses has exactly one default, promoting the newest if the one
    /// that was deleted was it.
    /// </summary>
    /// <remarks>
    /// <b>The newest, not the oldest.</b> A shopper who deletes their default has most likely just
    /// replaced it, and the replacement is the newest row.
    /// </remarks>
    private async Task PromoteADefaultAsync(int userId, CancellationToken cancellationToken)
    {
        if (await dbContext.CustomerAddresses.AnyAsync(
                row => row.UserId == userId && row.IsDefault,
                cancellationToken))
        {
            return;
        }

        var newest = await dbContext.CustomerAddresses
            .Where(row => row.UserId == userId)
            .OrderByDescending(row => row.Id)
            .Select(row => row.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (newest == 0)
        {
            return;
        }

        await dbContext.CustomerAddresses
            .Where(row => row.Id == newest)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsDefault, true),
                cancellationToken);
    }

    /// <summary>
    /// The book, default first then newest first, with the district joined in.
    /// </summary>
    /// <remarks>
    /// <b>The ordering is applied before the projection</b>, which is the thing #126's 500 got wrong:
    /// a positional-record projection with the ordering applied after the <c>Select</c> is what EF could
    /// not translate, and the whole promo-code admin API answered 500 for a day and a half.
    /// </remarks>
    private async Task<IReadOnlyList<CustomerAddressDto>> ReadAsync(
        int userId,
        CancellationToken cancellationToken) =>
        await dbContext.CustomerAddresses
            .AsNoTracking()
            .Where(address => address.UserId == userId)
            .OrderByDescending(address => address.IsDefault)
            .ThenByDescending(address => address.Id)
            .Select(address => new CustomerAddressDto(
                address.Id,
                address.Label,
                address.RecipientName,
                address.Phone,
                address.Line1,
                address.Line2,
                address.City,
                address.DistrictId,
                address.District != null ? address.District.Name : null,

                // An overseas address has no district to deactivate, so it is never flagged for one.
                address.District == null || address.District.IsActive,
                address.PostalCode,
                address.Landmark,
                address.IsDefault,
                address.CountryCode,
                address.Country!.Name,
                address.Region))
            .ToListAsync(cancellationToken);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
