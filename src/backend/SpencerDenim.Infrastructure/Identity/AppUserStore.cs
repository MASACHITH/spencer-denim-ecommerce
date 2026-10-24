using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Identity's EF user store with one change, made twice (issues #46 and #250): <b>saving a user this request loaded
/// writes the columns this request changed, and no others.</b>
/// <para>
/// Identity's own <c>UpdateAsync</c> calls <c>DbContext.Update</c>, which marks <b>every</b> column modified, so every
/// <c>UserManager</c> method that saves the user — storing a token, redeeming a recovery code, resetting the counter,
/// rehashing a password, stamping <c>LastLoginAtUtc</c> — wrote back <i>the whole row as it was when the user was
/// loaded at the start of the request</i>. For a request that spends its time on a password hash that is a stale copy,
/// and the stale copy undoes whatever happened to the account meanwhile (#46 found it for the two lockout columns, #250
/// for the rest): an administrator's deactivation became "active" again, and a password change was replaced by the old
/// hash and the old security stamp, which also brought back every session the change had ended.
/// </para>
/// <para>
/// <b>Now:</b> for a user the context already tracks, no column is marked modified wholesale. Change detection compares
/// each column with the value the user was loaded (or last saved) with, so only a column this request actually changed
/// is in the <c>UPDATE</c> — <c>ConcurrencyStamp</c>, which Identity always rewrites, and whatever the caller set.
/// The two lockout columns need no special case any more: <c>StaffAccountLockout</c> adopts the database's value as
/// both the current and the original one, so they stay out of every save that did not change them.
/// </para>
/// <para>
/// <b>What this does not do.</b> It is not optimistic concurrency: two requests that change the <i>same</i> column
/// still resolve as last writer wins (<c>ConcurrencyStamp</c> is not an EF concurrency token, and making it one needs a
/// migration). A sign-in therefore also makes its two decisions in conditional statements of their own
/// (<c>AdminAuthenticator</c>: the pending-sign-in write and the last-login write both name the account's security
/// stamp and <c>IsActive</c> in their own <c>WHERE</c>), because "the account is still what I decided on" has to be
/// part of the write that acts on the decision.
/// </para>
/// <para>
/// A user this context has not loaded (detached) has no original values to compare with, so it is still written in
/// full, as Identity always did.
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

        if (Context.Entry(user).State == EntityState.Detached)
        {
            // The base implementation, line for line: with nothing to compare against, every column is written.
            Context.Attach(user);
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
            Context.Update(user);
        }
        else
        {
            // Neither Attach nor Update: on a tracked entity Attach accepts its pending changes (the current values
            // become the original ones, so a deliberate change would look untouched) and Update marks every column.
            // Change detection, run explicitly in case a caller has switched it off, finds exactly what changed.
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
            Context.ChangeTracker.DetectChanges();
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
}
