using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// The one per-staff-user application lock (<c>sp_getapplock</c>, transaction-owned, waits rather than fails). Every
/// write to a staff user's account takes it inside its transaction before it reads the row, so two writers to the same
/// account serialise instead of one overwriting the other from a stale read (#250's class of defect).
/// <para>
/// Taken by <see cref="StaffUserRoleManagementStore"/> for every admin write (W1-08 tester round 1) and, since #254, by
/// <see cref="AdminPasswordChanger"/>: a member's own password change racing a Super Admin's reset used to win silently,
/// keeping a password chosen with the old (possibly compromised) one while the admin was told the reset had worked.
/// </para>
/// </summary>
internal static class StaffUserLocks
{
    public static string Resource(int userId) =>
        string.Create(CultureInfo.InvariantCulture, $"rbac:manage-user:{userId}");

    /// <summary>Acquire the lock for <paramref name="userId"/>. The caller must have a transaction open.</summary>
    public static Task<int> AcquireAsync(AppDbContext dbContext, int userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"EXEC sp_getapplock @Resource = {Resource(userId)}, @LockMode = N'Exclusive', @LockOwner = N'Transaction';",
            cancellationToken);
    }
}
