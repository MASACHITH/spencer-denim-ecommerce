using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Interceptors;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// The staff lockout bookkeeping (doc 08 §3.1 N14, doc 03 §3a): at most
/// <see cref="AdminSecurityOptions.LockoutMaxFailedAttempts"/> wrong passwords or codes per lockout window, <b>however
/// many arrive at once</b> (issue #46). Every sign-in step that checks a secret — the password step, the 2FA code
/// step, and the current-password re-checks of change-password and the 2FA settings — goes through here.
/// <para>
/// <b>Why not Identity's own <c>AccessFailedAsync</c>.</b> It is a read-modify-write: the counter loaded with the
/// user at the start of the request is incremented in memory and the whole row saved, with no concurrency check
/// (<see cref="AppDbContext"/> is a plain <c>DbContext</c>, so <c>ConcurrencyStamp</c> is not a concurrency token).
/// Sixty parallel wrong passwords all read 0 or 1, all wrote 1 or 2, and the account ended with
/// <c>AccessFailedCount = 4</c> and no lockout. Here a failure is <b>one</b> <c>UPDATE</c> that increments,
/// locks on reaching the threshold, and refuses an account that is already locked, all under the row's update lock
/// — the same shape, for the same reason, as <c>NumberSeriesAllocator</c>'s doc 08 §3.5 statement. A second
/// caller waits for the first's lock and then sees what the first wrote, so exactly the threshold number of
/// failures is ever counted, and the one that reaches it is the only one told it locked the account (so exactly
/// one <c>Lockout</c> audit row).
/// </para>
/// <para>
/// <b>A right secret racing a lockout is refused too.</b> The lock check Identity makes before a password check
/// reads the user as loaded at the start of the request, and the hash takes long enough for a burst of parallel
/// failures to lock the account meanwhile. So a correct password is accepted only if the account is still unlocked
/// <i>after</i> it was verified (<see cref="FindActiveLockoutAsync"/>): a burst of guesses then gets no more than
/// the threshold's worth of chances, because a right guess counts only when fewer failures than that landed before
/// it. Sequentially that second look always agrees with the first, so a sequential caller sees no difference.
/// </para>
/// <para>
/// The other half of the defect — a stale copy of the user saved later in a request writing the old counter and
/// <c>LockoutEnd</c> back — is closed in <see cref="AppUserStore"/>. Between the two, the lockout columns are
/// written only by the statements below or by a save that deliberately changed them.
/// </para>
/// </summary>
public sealed class StaffAccountLockout(
    AppDbContext dbContext,
    SignInManager<AppUser> signInManager,
    IOptions<IdentityOptions> identityOptions,
    IClock clock,
    AuditAndSoftDeleteInterceptor auditColumns)
{
    /// <summary>
    /// The password check every staff flow uses. Identity still verifies the password (constant-time hash, rehash,
    /// the counter reset on success for an account without 2FA), but with <c>lockoutOnFailure: false</c>: the
    /// failure itself is counted by <see cref="RecordFailedAttemptAsync"/>.
    /// </summary>
    public async Task<StaffSecretCheck> CheckPasswordAsync(AppUser user, string password)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);

        var result = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: false);

        if (result.IsLockedOut)
        {
            return StaffSecretCheck.LockedOut(user.LockoutEnd ?? DateTimeOffset.UtcNow, wasNewlyLockedOut: false);
        }

        if (result.Succeeded)
        {
            return await FindActiveLockoutAsync(user) is { } lockoutEnd
                ? StaffSecretCheck.LockedOut(lockoutEnd, wasNewlyLockedOut: false)
                : StaffSecretCheck.Succeeded;
        }

        // NotAllowed (an unconfirmed account) is refused before any password check, exactly as Identity does, and
        // like Identity it does not count: nothing was guessed. No staff account can be in that state today.
        return result.IsNotAllowed ? StaffSecretCheck.Invalid : await RecordFailedAttemptAsync(user);
    }

    /// <summary>
    /// Counts one wrong password or code, atomically. Locks the account when this failure reaches
    /// <see cref="LockoutOptions.MaxFailedAccessAttempts"/> (and, like Identity, resets the counter for the next
    /// window); an account that is already locked is answered as locked and not counted.
    /// </summary>
    public async Task<StaffSecretCheck> RecordFailedAttemptAsync(AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var lockout = identityOptions.Value.Lockout;

        // Identity's own clock for lockout (UserManager.IsLockedOutAsync compares LockoutEnd with
        // DateTimeOffset.UtcNow), so this statement and every later IsLockedOutAsync agree on "now". IClock is for
        // the audit column only, exactly as AuditAndSoftDeleteInterceptor would stamp it.
        var now = DateTimeOffset.UtcNow;
        var lockoutEnd = now.Add(lockout.DefaultLockoutTimeSpan);

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = false;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
            openedHere = true;
        }

        try
        {
            await using var command = connection.CreateCommand();

            // [identity] is bracketed because IDENTITY is a reserved word in T-SQL. Right-hand sides of SET all read
            // the row as it was before this UPDATE, so both CASEs test the same pre-increment count. Identity treats
            // LockoutEnd >= now as locked, hence "< @now" for unlocked. When the row is not updated (already locked),
            // the second statement reports the lockout that stopped it.
            command.CommandText =
                """
                UPDATE [identity].[Users]
                   SET AccessFailedCount = CASE WHEN AccessFailedCount + 1 >= @maxFailedAttempts THEN 0 ELSE AccessFailedCount + 1 END,
                       LockoutEnd        = CASE WHEN AccessFailedCount + 1 >= @maxFailedAttempts THEN @lockoutEnd ELSE LockoutEnd END,
                       UpdatedAtUtc      = @updatedAtUtc,
                       UpdatedByUserId   = @updatedByUserId
                OUTPUT inserted.AccessFailedCount,
                       inserted.LockoutEnd,
                       CAST(CASE WHEN deleted.AccessFailedCount + 1 >= @maxFailedAttempts THEN 1 ELSE 0 END AS bit),
                       CAST(0 AS bit)
                 WHERE Id = @userId
                   AND (LockoutEnabled = 0 OR LockoutEnd IS NULL OR LockoutEnd < @now);

                IF @@ROWCOUNT = 0
                    SELECT AccessFailedCount,
                           LockoutEnd,
                           CAST(0 AS bit),
                           CAST(CASE WHEN LockoutEnabled = 1 AND LockoutEnd >= @now THEN 1 ELSE 0 END AS bit)
                      FROM [identity].[Users]
                     WHERE Id = @userId;
                """;

            // The caller's transaction when there is one; no sign-in flow opens one today.
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

            AddParameter(command, "@userId", DbType.Int32, user.Id);
            AddParameter(command, "@maxFailedAttempts", DbType.Int32, lockout.MaxFailedAccessAttempts);
            AddParameter(command, "@now", DbType.DateTimeOffset, now);
            AddParameter(command, "@lockoutEnd", DbType.DateTimeOffset, lockoutEnd);
            AddParameter(command, "@updatedAtUtc", DbType.DateTime2, clock.UtcNow.UtcDateTime);
            AddParameter(command, "@updatedByUserId", DbType.Int32, (object?)auditColumns.ResolveCurrentUserId() ?? DBNull.Value);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync() && !(await reader.NextResultAsync() && await reader.ReadAsync()))
            {
                // The user row is gone. Users are never deleted (doc 08 §1.6), so this is not reachable in practice;
                // answering it as a plain wrong secret reveals nothing either way.
                return StaffSecretCheck.Invalid;
            }

            var count = reader.GetInt32(0);
            DateTimeOffset? storedLockoutEnd = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
            var lockedByThisAttempt = reader.GetBoolean(2);
            var alreadyLockedOut = reader.GetBoolean(3);

            AdoptPersistedLockoutState(user, count, storedLockoutEnd);

            if (lockedByThisAttempt)
            {
                return StaffSecretCheck.LockedOut(storedLockoutEnd ?? lockoutEnd, wasNewlyLockedOut: true);
            }

            if (alreadyLockedOut && storedLockoutEnd is { } activeLockoutEnd)
            {
                return StaffSecretCheck.LockedOut(activeLockoutEnd, wasNewlyLockedOut: false);
            }

            // Counted below the threshold — or, in the microseconds between the two statements, a lockout that
            // expired just then, which is treated as the plain wrong secret it is.
            return StaffSecretCheck.Invalid;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// The account's lockout as it is in the database right now, not as it was when the user was loaded: the end of
    /// an active lockout, or <see langword="null"/> when the account is not locked. Called after a right secret has
    /// been verified (see the class remarks).
    /// </summary>
    public async Task<DateTimeOffset?> FindActiveLockoutAsync(AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var stored = await dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == user.Id)
            .Select(u => new { u.AccessFailedCount, u.LockoutEnd, u.LockoutEnabled })
            .SingleOrDefaultAsync();

        if (stored is null)
        {
            return null;
        }

        AdoptPersistedLockoutState(user, stored.AccessFailedCount, stored.LockoutEnd);
        return stored.LockoutEnabled && stored.LockoutEnd >= DateTimeOffset.UtcNow ? stored.LockoutEnd : null;
    }

    /// <summary>
    /// Brings the tracked user in line with what the database now holds, as both its current and its original
    /// values, so the entity tells the truth for the rest of the request and a later save of it (the audit writer
    /// shares this <see cref="DbContext"/>) has nothing of these two columns to write.
    /// </summary>
    private void AdoptPersistedLockoutState(AppUser user, int accessFailedCount, DateTimeOffset? lockoutEnd)
    {
        var entry = dbContext.Entry(user);
        if (entry.State == EntityState.Detached)
        {
            user.AccessFailedCount = accessFailedCount;
            user.LockoutEnd = lockoutEnd;
            return;
        }

        var count = entry.Property(u => u.AccessFailedCount);
        count.CurrentValue = accessFailedCount;
        count.OriginalValue = accessFailedCount;
        count.IsModified = false;

        var end = entry.Property(u => u.LockoutEnd);
        end.CurrentValue = lockoutEnd;
        end.OriginalValue = lockoutEnd;
        end.IsModified = false;
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>What <see cref="StaffAccountLockout"/> made of a password or code.</summary>
public enum StaffSecretCheckStatus
{
    Succeeded,
    Invalid,
    LockedOut,
}

/// <summary>
/// The outcome of one secret check. <see cref="WasNewlyLockedOut"/> is true only for the one failure that tripped the
/// lock, whatever else was racing it — the attempt the <c>Lockout</c> audit row belongs to.
/// </summary>
public readonly record struct StaffSecretCheck(StaffSecretCheckStatus Status, DateTimeOffset? LockoutEnd, bool WasNewlyLockedOut)
{
    public static StaffSecretCheck Succeeded => new(StaffSecretCheckStatus.Succeeded, null, false);

    public static StaffSecretCheck Invalid => new(StaffSecretCheckStatus.Invalid, null, false);

    public static StaffSecretCheck LockedOut(DateTimeOffset lockoutEnd, bool wasNewlyLockedOut) =>
        new(StaffSecretCheckStatus.LockedOut, lockoutEnd, wasNewlyLockedOut);
}
