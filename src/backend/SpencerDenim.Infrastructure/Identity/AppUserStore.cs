using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Identity's EF user store with one change (issue #46): a save of a tracked user does not write
/// <c>AccessFailedCount</c> or <c>LockoutEnd</c> unless this request actually changed them.
/// <para>
/// Identity's <c>UpdateAsync</c> calls <c>DbContext.Update</c>, which marks <b>every</b> column modified, so every
/// <c>UserManager</c> method that saves the user — storing the 2FA pending sign-in, redeeming a recovery code,
/// resetting the counter, <c>AdminAuthenticator</c> stamping <c>LastLoginAtUtc</c> — wrote back the lockout state
/// as it was when the user was loaded at the start of the request. A request that loaded the user just before a
/// burst of parallel failures locked it would then unlock it again, or wind the counter back. With
/// <see cref="StaffAccountLockout"/> counting failures in the database rather than on the entity, those two
/// columns are written only by its atomic statements or by a save that deliberately changed them
/// (<c>SetLockoutEndDateAsync</c>, Identity's own counter reset), and a stale copy can no longer undo a lockout.
/// </para>
/// <para>
/// Only the two lockout columns, and only for a user this context already tracks (the only kind any flow saves; a
/// detached user has no loaded values to compare with, so it is still written in full, as before). Every other
/// column keeps Identity's write-everything behaviour: narrowing that is a wider change than this issue.
/// </para>
/// </summary>
public sealed class AppUserStore(AppDbContext context, IdentityErrorDescriber? describer = null)
    : UserStore<AppUser, AppRole, AppDbContext, int>(context, describer)
{
    public override async Task<IdentityResult> UpdateAsync(AppUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(user);

        // Decided before Attach: on an entity this context already tracks, Attach accepts its pending changes (the
        // original values become the current ones), after which a deliberate change — Identity's own counter reset
        // setting AccessFailedCount to 0 — would look exactly like an untouched, stale value.
        var entry = Context.Entry(user);
        var wasTracked = entry.State != EntityState.Detached;
        var keepOutAccessFailedCount = wasTracked && IsUnchanged(entry.Property(u => u.AccessFailedCount));
        var keepOutLockoutEnd = wasTracked && IsUnchanged(entry.Property(u => u.LockoutEnd));

        // Then the base implementation, line for line, with those columns taken back out of the UPDATE between
        // DbContext.Update (which marks every column modified) and the save.
        Context.Attach(user);
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        Context.Update(user);

        entry = Context.Entry(user);
        if (keepOutAccessFailedCount)
        {
            entry.Property(u => u.AccessFailedCount).IsModified = false;
        }

        if (keepOutLockoutEnd)
        {
            entry.Property(u => u.LockoutEnd).IsModified = false;
        }

        try
        {
            await SaveChanges(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return IdentityResult.Failed(ErrorDescriber.ConcurrencyFailure());
        }

        return IdentityResult.Success;
    }

    /// <summary>Unchanged since the user was loaded (or last saved) means this request has nothing to say about it.</summary>
    private static bool IsUnchanged<TProperty>(PropertyEntry<AppUser, TProperty> property) =>
        EqualityComparer<TProperty>.Default.Equals(property.OriginalValue, property.CurrentValue);
}
