using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Identity.Customers;

/// <summary>
/// Doc 11 P11 step B.5's customer lockout — 10 failures, 15 minutes — counted with atomic statements, so
/// parallel attempts cannot outrun it.
/// </summary>
/// <remarks>
/// <b>Why this is not <c>SignInManager.CheckPasswordSignInAsync(lockoutOnFailure: true)</c>.</b> Identity's
/// failed-attempt counter is a read-modify-write on <c>AccessFailedCount</c> through
/// <c>UserManager.AccessFailedAsync</c>, and <c>AppDbContext</c> is a plain <c>DbContext</c> with no
/// concurrency token on the user row, so nothing detects a concurrent edit. Tharanga measured the staff
/// side of exactly this on #146: <b>60 parallel wrong passwords for one admin left the count at 4 and the
/// account unlocked.</b> The same race is the same race here, and the customer threshold is twice as
/// high, which makes the window twice as wide.
/// <para>
/// <b>#146 is open, not merged, and this is deliberately not a second copy of it.</b> That PR adds
/// <c>StaffAccountLockout</c> and replaces the EF user store with an <c>AppUserStore</c> that keeps
/// <c>AccessFailedCount</c> and <c>LockoutEnd</c> out of unrelated saves. The store replacement is a
/// change to a shared startup path in the middle of his review, so this class does not touch it: every
/// statement below is set-based and materialises no user, so there is no tracked copy for an unrelated
/// <c>SaveChanges</c> to write back over a lockout. The two classes will be near-identical and
/// <b>should become one once #146 lands</b> — the task report says so, and so does the boundary note on
/// #105.
/// </para>
/// <para>
/// <b>The three properties the statements have to provide.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <b>A failure counts exactly once, and the lockout is reported to exactly one attempt.</b> The two
/// <c>UPDATE</c>s below have mutually exclusive predicates over the row's own current count, so for any
/// row state exactly one of them can apply — and only the one caller whose statement actually set
/// <c>LockoutEnd</c> is told it tripped the lock, so there is one <c>Lockout</c> audit row however many
/// requests are in flight.
/// </description></item>
/// <item><description>
/// <b>A correct password is accepted only if the account is still unlocked <i>after</i> it was
/// verified.</b> This is the guard #146 names and the brief singles out. Without it an attacker fires the
/// right password alongside twenty wrong ones: every request passes the lockout pre-check before any of
/// them writes, and the right one opens a session on an account that is locked by the time it answers.
/// <see cref="IsStillUnlockedAsync"/> is a fresh keyed read, and the sign-in path calls it on the success
/// branch.
/// </description></item>
/// <item><description>
/// <b>An account with <c>LockoutEnabled = 0</c> is never reported as locked.</b> #146's round-1 tester
/// caught the staff version reporting a lockout that did not exist. Here both statements exclude such a
/// row and the fallback reads the real <c>LockoutEnd</c>, which for that account is null.
/// </description></item>
/// </list>
/// <para>
/// <b>Why two <c>ExecuteSqlAsync</c> statements rather than one <c>UPDATE … OUTPUT</c>.</b>
/// <c>EmailOutboxStore</c> records why an <c>OUTPUT</c>-reading statement has to be a raw
/// <see cref="System.Data.Common.DbCommand"/>: "<c>ExecuteSqlInterpolated</c> returns a row count and
/// discards them, and <c>SqlQuery</c> composes the SQL into a subquery, which an <c>UPDATE</c> cannot
/// be." A raw command also has to name the execution strategy by hand and manage its own connection.
/// The row count is all this class needs, so two conditional updates give the same guarantee with none
/// of that — and EF wraps each of them in doc 08 §10.3's retry strategy by itself.
/// </para>
/// </remarks>
internal sealed class CustomerAccountLockout(
    AppDbContext dbContext,
    IClock clock,
    IOptions<CustomerSecurityOptions> options)
{
    /// <summary>What one failed attempt did to the account.</summary>
    /// <param name="IsLockedOut">True when the account is locked now, however it got there.</param>
    /// <param name="LockoutEndUtc">When the lock expires, or null when there is none.</param>
    /// <param name="TrippedByThisAttempt">
    /// True for the one attempt whose own increment reached the threshold. The caller writes the single
    /// <c>Lockout</c> audit row on this and nothing else.
    /// </param>
    public readonly record struct FailureOutcome(
        bool IsLockedOut,
        DateTime? LockoutEndUtc,
        bool TrippedByThisAttempt);

    /// <summary>
    /// Records one failed attempt against <paramref name="userId"/> atomically.
    /// </summary>
    /// <remarks>
    /// <b>Each statement's <c>WHERE</c> clause is four guards, not one.</b> <c>UserType = Customer</c>
    /// keeps these statements off a staff row even if a caller passes the wrong id — doc 08 §3.1 DB10's
    /// scoping rule in the database and not only in the use case. <c>LockoutEnabled = 1</c> leaves an
    /// exempt account alone. An account that is <i>already</i> locked is not counted further, so a lock
    /// cannot be extended indefinitely by hammering a locked account: doc 08 §3.1 DB10's "it expires by
    /// itself, so it can't permanently lock out the store" applies to a shopper too. And the count
    /// comparison is what makes the two statements exclusive.
    /// <para>
    /// <b>The second statement resets the count to zero as it locks.</b> That is Identity's own behaviour
    /// and the right one: the next window starts from zero, so an account is not locked again by one
    /// stray attempt fifteen minutes later.
    /// </para>
    /// </remarks>
    public async Task<FailureOutcome> RecordFailureAsync(int userId, CancellationToken cancellationToken)
    {
        var security = options.Value;
        var nowUtc = clock.UtcNow.UtcDateTime;
        var max = security.LockoutMaxFailedAttempts;

        // Below the threshold: count it and say nothing else.
        var counted = await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE [identity].[Users]
            SET [AccessFailedCount] = [AccessFailedCount] + 1
            WHERE [Id] = {userId}
              AND [UserType] = {(byte)UserType.Customer}
              AND [LockoutEnabled] = 1
              AND ([LockoutEnd] IS NULL OR [LockoutEnd] <= {nowUtc})
              AND [AccessFailedCount] + 1 < {max}
            """,
            cancellationToken);

        if (counted == 1)
        {
            return new FailureOutcome(IsLockedOut: false, LockoutEndUtc: null, TrippedByThisAttempt: false);
        }

        // At the threshold: lock it, and reset the counter for the window after this one. Exactly one
        // concurrent caller can affect a row here — the first to take the row lock sets `LockoutEnd` into
        // the future, which makes every other caller's own `LockoutEnd <= now` guard false.
        var lockoutEnd = nowUtc.AddMinutes(security.LockoutMinutes);

        var tripped = await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE [identity].[Users]
            SET [AccessFailedCount] = 0,
                [LockoutEnd] = {lockoutEnd}
            WHERE [Id] = {userId}
              AND [UserType] = {(byte)UserType.Customer}
              AND [LockoutEnabled] = 1
              AND ([LockoutEnd] IS NULL OR [LockoutEnd] <= {nowUtc})
              AND [AccessFailedCount] + 1 >= {max}
            """,
            cancellationToken);

        if (tripped == 1)
        {
            return new FailureOutcome(IsLockedOut: true, LockoutEndUtc: lockoutEnd, TrippedByThisAttempt: true);
        }

        // Neither statement applied. The account is exempt from lockout, already locked, or not a
        // customer row at all. None of those is a state to invent an answer for, so what is true now is
        // read rather than guessed.
        var existing = await ReadLockoutEndAsync(userId, cancellationToken);

        return new FailureOutcome(
            IsLockedOut: existing is not null && existing > nowUtc,
            LockoutEndUtc: existing is not null && existing > nowUtc ? existing : null,
            TrippedByThisAttempt: false);
    }

    /// <summary>
    /// Whether the account is unlocked <b>now</b>, read fresh. The success branch of a sign-in calls this
    /// after the password has been verified, which is the guard #146's own summary states: "a correct
    /// password/code is accepted only if a fresh read shows the account still unlocked".
    /// </summary>
    public async Task<bool> IsStillUnlockedAsync(int userId, CancellationToken cancellationToken)
    {
        var end = await ReadLockoutEndAsync(userId, cancellationToken);

        return end is null || end <= clock.UtcNow.UtcDateTime;
    }

    /// <summary>
    /// Clears the failure count after a successful sign-in, with one set-based statement.
    /// </summary>
    /// <remarks>
    /// <b>It does not clear <c>LockoutEnd</c>, and that is on purpose.</b> The caller only reaches this
    /// after <see cref="IsStillUnlockedAsync"/> said the account is unlocked, so any end time still on
    /// the row is in the past and harmless; blanking it would destroy the only evidence on the row that
    /// the account was ever locked. Identity's own reset writes both, which is one of the reasons #146 had
    /// to stop the user store writing these columns on unrelated saves.
    /// </remarks>
    public async Task ResetFailureCountAsync(int userId, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE [identity].[Users]
            SET [AccessFailedCount] = 0
            WHERE [Id] = {userId}
              AND [UserType] = {(byte)UserType.Customer}
              AND [AccessFailedCount] <> 0
            """,
            cancellationToken);

    /// <summary>
    /// <c>LockoutEnd</c> as the database holds it, or null. A keyed read on the clustered index.
    /// </summary>
    /// <remarks>
    /// <b>Raw SQL rather than <c>dbContext.Users.Where(...)</c>, so nothing is tracked.</b> A tracked
    /// <c>AppUser</c> loaded here would be a stale copy that a later <c>SaveChanges</c> in the same scope
    /// could write back over an atomic lockout — the second half of #146's finding, and the reason that
    /// PR needed a custom user store. This class never materialises a user, so there is nothing to write
    /// back.
    /// </remarks>
    /// <summary>
    /// <c>AccessFailedCount</c> as the database holds it.
    /// </summary>
    /// <remarks>
    /// <b>Nothing in the production path calls this, and that is the point.</b> It exists so the
    /// mutation run can turn <see cref="RecordFailureAsync"/>'s atomic increment into the
    /// read-modify-write Identity does — which is the bug #146 measured as 60 parallel failures leaving a
    /// count of 4 — and watch <c>Thirty_parallel_wrong_passwords_still_lock_the_account</c> go red. A
    /// guard whose test has never been seen to fail is a guard nobody has checked.
    /// </remarks>
    internal async Task<int> ReadFailureCountAsync(int userId, CancellationToken cancellationToken)
    {
        var values = await dbContext.Database
            .SqlQuery<int>($"""
                SELECT [AccessFailedCount] AS [Value]
                FROM [identity].[Users]
                WHERE [Id] = {userId} AND [UserType] = {(byte)UserType.Customer}
                """)
            .ToListAsync(cancellationToken);

        return values.Count == 1 ? values[0] : 0;
    }

    private async Task<DateTime?> ReadLockoutEndAsync(int userId, CancellationToken cancellationToken)
    {
        var values = await dbContext.Database
            .SqlQuery<DateTimeOffset?>($"""
                SELECT [LockoutEnd] AS [Value]
                FROM [identity].[Users]
                WHERE [Id] = {userId} AND [UserType] = {(byte)UserType.Customer}
                """)
            .ToListAsync(cancellationToken);

        return values.Count == 1 && values[0] is { } end ? end.UtcDateTime : null;
    }
}
