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
/// is in the <c>UPDATE</c>: whatever the caller set (<c>UserManager</c> sets the hash and stamp of a password change,
/// the email and its normalised form of an email change, and so on), <c>ConcurrencyStamp</c>, which Identity always
/// rewrites, and the audit columns <c>UpdatedAtUtc</c> and <c>UpdatedByUserId</c>, which the audit interceptor stamps on
/// every save of a modified row. The two lockout columns need no special case any more: <c>StaffAccountLockout</c>
/// adopts the database's value as both the current and the original one, so they stay out of every save that did not
/// change them.
/// </para>
/// <para>
/// Change detection is switched on for the duration of the save, whatever the caller left it at. With it off, the
/// columns the caller changed would not be found and the audit columns the interceptor sets just before the write
/// would not be written either (the interceptor assigns the properties, and only change detection notices an
/// assignment). Nothing in the code base turns automatic detection off today; a test pins the behaviour anyway.
/// </para>
/// <para>
/// <b>What this does not do.</b> It is not optimistic concurrency: two requests that change the <i>same</i> column
/// still resolve as last writer wins (<c>ConcurrencyStamp</c> is not an EF concurrency token, and making it one needs a
/// migration). A sign-in therefore also makes its two decisions in conditional statements of their own
/// (<c>AdminAuthenticator</c>: the pending-sign-in write and the last-login write both name the account's security
/// stamp, <c>IsActive</c> and the lockout in their own <c>WHERE</c>), because "the account is still what I decided on"
/// has to be part of the write that acts on the decision. A rehash that <c>UserManager.CheckPasswordAsync</c> makes of a
/// password hash that is out of date is a save of the hash and stamp this request computed, and is not guarded; no hash
/// needs a rehash today (docs/journal/2026-10-25-tharanga-250-stale-user-row.md).
/// </para>
/// <para>
/// <b>Side effects of the sign-in writes leaving the user row alone, intended:</b> a sign-in or a pending sign-in no
/// longer moves <c>ConcurrencyStamp</c> (nothing reads it), and the password step no longer stamps <c>UpdatedAtUtc</c>
/// or overwrites <c>UpdatedByUserId</c> with null for an anonymous request; the last-login write stamps both itself.
/// </para>
/// <para>
/// <b>A user marked modified wholesale</b> (<c>Update(user)</c> or <c>Entry(user).State = Modified</c> before
/// <c>UserManager.UpdateAsync</c>) is still written in full, the two lockout columns included: they used to be held
/// out of such a save, and are not any more. No caller does this today; the one that does must adopt the lockout state
/// first or accept that it is written back.
/// </para>
/// <para>
/// A user this context has not loaded (detached) has no original values to compare with, so it is still written in
/// full, as Identity always did. That branch is reachable only by calling this store directly: through
/// <c>UserManager</c> its validator loads the user first, so the user is tracked by the time it is saved (and attaching
/// a second instance with the same key throws, as it always did).
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
            // The save's own change detection finds exactly what changed (see the class remarks for why it is forced on).
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
        }

        var autoDetectChanges = Context.ChangeTracker.AutoDetectChangesEnabled;
        Context.ChangeTracker.AutoDetectChangesEnabled = true;
        try
        {
            await SaveChanges(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return IdentityResult.Failed(ErrorDescriber.ConcurrencyFailure());
        }
        finally
        {
            Context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }

        return IdentityResult.Success;
    }
}
