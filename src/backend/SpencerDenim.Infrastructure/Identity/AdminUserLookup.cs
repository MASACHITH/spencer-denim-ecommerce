using System.Globalization;
using Microsoft.AspNetCore.Identity;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Infrastructure.Identity;

public sealed class AdminUserLookup(UserManager<AppUser> userManager) : IAdminUserLookup
{
    public async Task<AdminUserSummary?> FindByIdAsync(int userId, CancellationToken cancellationToken)
    {
        // UserManager methods take no CancellationToken; this port is queried once per request via a fast,
        // indexed lookup, so there is nothing further to cancel.
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
        if (user is null || user.UserType != UserType.Staff)
        {
            return null;
        }

        return new AdminUserSummary(user.Id, user.Email ?? string.Empty, user.FirstName, user.LastName, user.IsActive, user.MustChangePassword, user.SecurityStamp);
    }
}
