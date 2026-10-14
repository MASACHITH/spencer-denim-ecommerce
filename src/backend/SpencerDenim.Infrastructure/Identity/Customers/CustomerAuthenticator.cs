using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Accounts.Abstractions;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Identity.Customers;

/// <summary>
/// Doc 11 P11 step B.5: checks a customer's password. It never signs a cookie in — that is the endpoint's
/// job, exactly as on the admin side (<c>IAdminAuthenticator</c>).
/// </summary>
/// <remarks>
/// <b>It does not use <see cref="SignInManager{TUser}"/> at all</b>, which is the one structural
/// difference from <c>AdminAuthenticator</c> and is deliberate. <c>CheckPasswordSignInAsync</c> brings
/// three things with it: Identity's read-modify-write lockout counter (the race #146 measured at 60
/// parallel failures leaving an account unlocked), the two-factor branch customers do not have, and the
/// "remember this browser" scheme that is not registered. The password hash is the only part worth
/// having, and <see cref="IPasswordHasher{TUser}"/> gives that directly.
/// <para>
/// <b>Three properties this class exists to hold, each with a defect behind it.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <b>A correct password opens a session only if the account is still unlocked after the hash was
/// verified.</b> #146's own summary: "a correct password/code is accepted only if a fresh read shows the
/// account still unlocked", and that guard shipped on the staff side with zero coverage. Without it,
/// firing the right password alongside twenty wrong ones gets a session on a locked account, because
/// every request passes the lockout check before any of them writes.
/// </description></item>
/// <item><description>
/// <b>Every refusal costs the same.</b> #101 is open on the staff routes. An unknown address, a staff
/// address, a deactivated account and an account whose hash was blanked by PDPA anonymisation all run
/// <see cref="CustomerUserLookup.SpendPasswordHashCost"/>, so none of them answers faster than a genuine
/// wrong password.
/// </description></item>
/// <item><description>
/// <b>A rehash is not silently dropped.</b> Identity's hasher answers
/// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> when the stored hash used fewer
/// iterations than the current format. Treating that as a failure would lock out every customer after a
/// framework upgrade; treating it as a plain success would leave the weak hash in the database for ever.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class CustomerAuthenticator(
    AppDbContext dbContext,
    UserManager<AppUser> userManager,
    IPasswordHasher<AppUser> passwordHasher,
    CustomerUserLookup lookup,
    CustomerAccountLockout lockout,
    IClock clock) : ICustomerAuthenticator
{
    public async Task<CustomerSignInResult> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);

        // Doc 08 §3.1 DB10: filtered UserType = Customer before UserManager is consulted. A staff row is
        // not "wrong type" here — it is simply not found, which is what makes the two indistinguishable.
        var user = await lookup.FindCustomerByEmailAsync(email, cancellationToken);

        if (user?.PasswordHash is null)
        {
            lookup.SpendPasswordHashCost(password);
            return new CustomerSignInResult.InvalidCredentials();
        }

        // Before the hash: a locked account is told so without its password being checked, so a lockout
        // cannot be probed for a correct password. The post-verification re-check below is the one that
        // closes the race; this one is the ordinary answer.
        if (user.LockoutEnd is { } lockedUntil && lockedUntil.UtcDateTime > clock.UtcNow.UtcDateTime)
        {
            lookup.SpendPasswordHashCost(password);
            return new CustomerSignInResult.LockedOut(lockedUntil.UtcDateTime, WasNewlyLockedOut: false);
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);

        if (verification == PasswordVerificationResult.Failed)
        {
            var outcome = await lockout.RecordFailureAsync(user.Id, cancellationToken);

            return outcome.IsLockedOut && outcome.LockoutEndUtc is { } end
                ? new CustomerSignInResult.LockedOut(end, outcome.TrippedByThisAttempt)
                : new CustomerSignInResult.InvalidCredentials();
        }

        // **The guard #146 names.** The password was right; the question is whether the account was still
        // unlocked by the time it finished being checked. A fresh read, because the `user` instance was
        // loaded before the hash and says nothing about what the twenty parallel wrong passwords did
        // while it was being computed.
        if (!await lockout.IsStillUnlockedAsync(user.Id, cancellationToken))
        {
            // No further failure is recorded: the password was correct, so this is not a failed attempt,
            // and counting it would let a legitimate customer extend their own lockout. The end time is
            // re-read rather than guessed.
            var lockedEnd = await ReadLockoutEndAsync(user.Id, cancellationToken);

            return new CustomerSignInResult.LockedOut(
                lockedEnd ?? clock.UtcNow.UtcDateTime,
                WasNewlyLockedOut: false);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await RehashAsync(user, password, cancellationToken);
        }

        await lockout.ResetFailureCountAsync(user.Id, cancellationToken);
        await RecordSignInAsync(user.Id, cancellationToken);

        var stamp = await userManager.GetSecurityStampAsync(user);

        return new CustomerSignInResult.Success(new CustomerSession(
            user.Id,
            user.Email!,
            user.FirstName,
            user.LastName,
            user.EmailConfirmed,
            stamp));
    }

    public async Task<CustomerSession?> FindSessionAsync(int userId, CancellationToken cancellationToken)
    {
        var user = await lookup.FindCustomerByIdAsync(userId, cancellationToken);

        return user?.Email is null
            ? null
            : new CustomerSession(
                user.Id,
                user.Email,
                user.FirstName,
                user.LastName,
                user.EmailConfirmed,
                await userManager.GetSecurityStampAsync(user));
    }

    /// <summary>
    /// Doc 11 P11 step B.6's last clause: "records last sign-in time".
    /// </summary>
    /// <remarks>
    /// <b>A set-based <c>UPDATE</c> and not <c>userManager.UpdateAsync(user)</c>.</b> Identity's user
    /// store marks every column modified, so saving the tracked user here would write the
    /// <c>AccessFailedCount</c> and <c>LockoutEnd</c> this instance was loaded with — values that are
    /// stale by exactly the parallel failures the atomic lockout just counted. That is the second half of
    /// #146's finding ("any later save of a user loaded before the lockout … wrote the old count /
    /// <c>LockoutEnd</c> back and could undo a lockout"), and this is the sign-in path's own version of
    /// it. One column, by key.
    /// <para>
    /// <b>This is also where a customer sign-in is recorded, and no <c>ops.AuditLogs</c> row is
    /// written.</b> Doc 08 §3.1's explicit security-event list is about staff
    /// (<c>StaffDeactivated</c>, <c>AdminPasswordReset</c>, <c>RoleAssigned</c>…) and gives
    /// <c>LastLoginAtUtc</c> as the column for "when did this user last sign in". A <c>Login</c> row per
    /// shopper per visit would be the highest-volume thing in that table with no screen that reads it,
    /// and a <c>LoginFailed</c> row would store an address somebody <i>typed</i> — which, for an address
    /// with no account, carries <c>UserId = null</c> and so is invisible to §3.1 step 2's
    /// anonymisation-by-user-id. <c>Lockout</c> <i>is</i> written, once, by the handler. The reasoning is
    /// in the task report for the owner to overrule.
    /// </para>
    /// </remarks>
    private async Task RecordSignInAsync(int userId, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE [identity].[Users]
            SET [LastLoginAtUtc] = {clock.UtcNow.UtcDateTime}
            WHERE [Id] = {userId}
            """,
            cancellationToken);

    /// <summary>
    /// Stores the password again under the current hash format, after a
    /// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/>.
    /// </summary>
    /// <remarks>
    /// <b>Identity's own behaviour, made explicit, and written as one column by key.</b>
    /// <c>SignInManager</c> does this inside <c>CheckPasswordSignInAsync</c>; since this class does not
    /// use <c>SignInManager</c>, leaving it out would mean a hash upgrade never happened and every
    /// customer kept the old iteration count for ever. It must <b>not</b> go through
    /// <c>userManager.UpdateAsync</c>, for the reason <see cref="RecordSignInAsync"/> gives: that writes
    /// every column of a user instance loaded before the lockout counter moved.
    /// <para>
    /// <b>The security stamp is deliberately not bumped.</b> The password has not changed — only the way
    /// it is stored — so signing the customer out of their other browsers would be a cost with no cause.
    /// </para>
    /// <para>
    /// <b>The update is conditional on the hash still being the one that was verified.</b> If the
    /// customer changed their password in another tab between the verification and this write, that
    /// password must win; without the guard, this would quietly restore the old one.
    /// </para>
    /// </remarks>
    private async Task RehashAsync(AppUser user, string password, CancellationToken cancellationToken)
    {
        var rehashed = passwordHasher.HashPassword(user, password);

        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE [identity].[Users]
            SET [PasswordHash] = {rehashed}
            WHERE [Id] = {user.Id} AND [PasswordHash] = {user.PasswordHash}
            """,
            cancellationToken);
    }

    private async Task<DateTime?> ReadLockoutEndAsync(int userId, CancellationToken cancellationToken)
    {
        var values = await dbContext.Database
            .SqlQuery<DateTimeOffset?>($"""
                SELECT [LockoutEnd] AS [Value]
                FROM [identity].[Users]
                WHERE [Id] = {userId}
                """)
            .ToListAsync(cancellationToken);

        return values.Count == 1 && values[0] is { } end ? end.UtcDateTime : null;
    }
}
