using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Admin.Users;

/// <summary>
/// <see cref="ISecurityAlertRecipients"/> over the identity tables: active staff, with an address, and (for the list)
/// holding a role whose <c>GrantsAllPermissions</c> flag is set — doc 08 §3.8 DB2's "every active GrantsAll holder".
/// Anonymised users are excluded; they have no address to write to.
/// </summary>
internal sealed class SecurityAlertRecipients(AppDbContext dbContext) : ISecurityAlertRecipients
{
    public async Task<IReadOnlyList<StaffAddressee>> ListActiveSuperAdminsAsync(CancellationToken cancellationToken)
    {
        var holders = dbContext.UserRoles.AsNoTracking()
            .Where(ur => dbContext.Roles.Any(r => r.Id == ur.RoleId && r.GrantsAllPermissions))
            .Select(ur => ur.UserId);

        return await ActiveStaff()
            .Where(user => holders.Contains(user.Id))
            .OrderBy(user => user.Id)
            .Select(user => new StaffAddressee(user.Id, user.Email!, user.FirstName))
            .ToListAsync(cancellationToken);
    }

    public async Task<StaffAddressee?> FindActiveStaffAsync(int userId, CancellationToken cancellationToken) =>
        await ActiveStaff()
            .Where(user => user.Id == userId)
            .Select(user => new StaffAddressee(user.Id, user.Email!, user.FirstName))
            .FirstOrDefaultAsync(cancellationToken);

    private IQueryable<AppUser> ActiveStaff() =>
        dbContext.Users.AsNoTracking()
            .Where(user =>
                user.UserType == UserType.Staff
                && user.IsActive
                && user.AnonymisedAtUtc == null
                && user.Email != null
                && user.Email != string.Empty);
}
