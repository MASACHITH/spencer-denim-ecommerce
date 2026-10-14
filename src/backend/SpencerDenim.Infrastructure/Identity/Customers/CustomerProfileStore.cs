using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Accounts.Abstractions;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Identity.Customers;

/// <summary>
/// Doc 11 P11 step 10's profile half: a customer's own name, telephone number and default delivery
/// district (ACC-02).
/// </summary>
/// <remarks>
/// <b>This is the first reader <c>identity.Users.DefaultDistrictId</c> has ever had</b>, which is why
/// W3-01's migration gave the column its foreign key and its index — <c>AppUser</c>'s own remark named
/// this change as the one that owed both, "in the change that gives the column its first reader". GLB-06
/// reads it next: the header's district selector defaults to it.
/// <para>
/// <b>The email address is deliberately not editable here.</b> Doc 11 P11 step 10 includes it and that
/// flow is not built; the reasons are on <c>CustomerProfileEdit</c> and in the task report. A screen that
/// offered the field and quietly ignored it would be worse.
/// </para>
/// </remarks>
internal sealed class CustomerProfileStore(
    AppDbContext dbContext,
    CustomerUserLookup lookup,
    IClock clock) : ICustomerProfileStore
{
    public async Task<CustomerProfile?> FindAsync(int userId, CancellationToken cancellationToken) =>
        await ReadAsync(userId, cancellationToken);

    public async Task<CustomerProfile?> SaveAsync(
        int userId,
        CustomerProfileEdit edit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var user = await lookup.FindCustomerByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        // An unknown or inactive district is refused rather than stored. The foreign key would refuse an
        // unknown id anyway — with a constraint error no shopper can read — and an *inactive* one would
        // satisfy the constraint while having no delivery rate, so GLB-06's estimate would have nothing
        // to show and no explanation for why.
        var districtId = edit.DefaultDistrictId;

        if (districtId is { } candidate
            && !await dbContext.Districts.AnyAsync(
                district => district.Id == candidate && district.IsActive,
                cancellationToken))
        {
            districtId = null;
        }

        var phone = string.IsNullOrWhiteSpace(edit.Phone) ? null : edit.Phone.Trim();
        var lastName = string.IsNullOrWhiteSpace(edit.LastName) ? null : edit.LastName.Trim();

        // Set-based, naming exactly the four columns a customer may change. `userManager.UpdateAsync`
        // would write every column of the user row, including the `AccessFailedCount` and `LockoutEnd`
        // this instance was loaded with — #146's second finding, which applies to every save of a user
        // row and not only to the lockout path.
        //
        // `UpdatedAtUtc` and `UpdatedByUserId` are written by hand here for the same reason: a set-based
        // statement bypasses `AuditAndSoftDeleteInterceptor`, which is what stamps them on a tracked
        // save. Doc 08 §3.1 marks this table [A], so leaving them would be a silent gap in the audit
        // columns of the one table whose rows are people.
        var updated = await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE [identity].[Users]
            SET [FirstName] = {edit.FirstName.Trim()},
                [LastName] = {lastName},
                [PhoneNumber] = {phone},
                [DefaultDistrictId] = {districtId},
                [UpdatedAtUtc] = {clock.UtcNow.UtcDateTime},
                [UpdatedByUserId] = {userId}
            WHERE [Id] = {userId} AND [UserType] = {(byte)UserType.Customer} AND [IsActive] = 1
            """,
            cancellationToken);

        return updated == 0 ? null : await ReadAsync(userId, cancellationToken);
    }

    /// <summary>
    /// The profile as My account shows it, with the district's name joined in so the screen does not have
    /// to fetch the whole district list to label one id.
    /// </summary>
    /// <remarks>
    /// <b>A left join, written as a navigation-free sub-select</b>, because <c>AppUser</c> has no
    /// <c>District</c> navigation: W3-01 added the foreign key without one, for the reason
    /// <c>CartConfiguration</c> gives — a reference-data navigation on the Identity entity would put
    /// <c>ref</c> types on it for the benefit of one read. <c>AsNoTracking</c>, and the projection is
    /// positional into a record <b>after</b> the <c>Where</c>, which is the ordering #126's 500 got
    /// wrong.
    /// </remarks>
    private async Task<CustomerProfile?> ReadAsync(int userId, CancellationToken cancellationToken) =>
        await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && user.UserType == UserType.Customer && user.IsActive)
            .Select(user => new CustomerProfile(
                user.Id,
                user.Email!,
                user.EmailConfirmed,
                user.FirstName,
                user.LastName,
                user.PhoneNumber,
                user.DefaultDistrictId,
                dbContext.Districts
                    .Where(district => district.Id == user.DefaultDistrictId)
                    .Select(district => district.Name)
                    .FirstOrDefault()))
            .FirstOrDefaultAsync(cancellationToken);
}
