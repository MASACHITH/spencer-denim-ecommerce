using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Holds back <b>one</b> read: the look <c>StaffAccountLockout.FindActiveLockoutAsync</c> takes at the account's
/// lockout <i>after</i> a right password or a right code has been verified. That second look is the only thing that
/// refuses a right secret which a burst of wrong ones has outrun (issue #46, <c>StaffAccountLockout</c>'s "a right
/// secret racing a lockout is refused too"), and no sequential test can reach it: a fresh request is stopped by
/// Identity's own check before the second look ever matters. So the request is stopped <b>at</b> the second look,
/// the account is locked while it waits, and it is let go to find out.
/// <para>
/// Usage: <see cref="Arm"/>, send the right secret, <c>await</c> <see cref="WaitUntilHeldAsync"/> (the request has
/// verified its secret against an account that was unlocked at that moment), lock the account, <see cref="Release"/>.
/// </para>
/// <para>
/// <b>It must not be able to pass by matching nothing.</b> If the query's shape changes the gate never fires, and
/// <see cref="WaitUntilHeldAsync"/> then fails with a message naming the matcher instead of letting the test run on
/// against a request that was never held. The held request waits at most <see cref="HoldLimit"/>, so a test that
/// fails before releasing cannot leave the host hanging.
/// </para>
/// </summary>
public sealed class LockoutRecheckGateInterceptor : DbCommandInterceptor
{
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed;

    /// <summary>Off at construction, so setting a test up runs at full speed. Holds the first matching read only.</summary>
    public void Arm() => Volatile.Write(ref _armed, 1);

    /// <summary>Lets the held read go.</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>Completes when a request is stopped at the re-check; throws if none arrives in time.</summary>
    public async Task WaitUntilHeldAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _held.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                "No request reached the lockout re-check within " + timeout + ". Two causes, and the first is the "
                + "regression this test exists to catch: (1) the second look was REMOVED — a right password or code is "
                + "no longer checked against the account's lockout after it is verified (StaffAccountLockout."
                + "CheckPasswordAsync, AdminAuthenticator.CompleteTwoFactorSignInAsync); or (2) FindActiveLockoutAsync no "
                + "longer issues a SELECT of AccessFailedCount, LockoutEnd and LockoutEnabled without PasswordHash, so "
                + "this gate's matcher is out of date and the test proves nothing until it is updated.");
        }
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (IsLockoutRecheck(command) && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
        {
            _held.TrySetResult();
            await _released.Task.WaitAsync(HoldLimit, cancellationToken);
        }

        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <summary>
    /// <c>FindActiveLockoutAsync</c> projects exactly three columns of the user. Identity's own loads take the whole
    /// row, so the absence of <c>PasswordHash</c> is what separates the two.
    /// </summary>
    private static bool IsLockoutRecheck(DbCommand command)
    {
        var text = command.CommandText;

        return text.Contains("FROM [identity].[Users]", StringComparison.Ordinal)
            && text.Contains("[AccessFailedCount]", StringComparison.Ordinal)
            && text.Contains("[LockoutEnd]", StringComparison.Ordinal)
            && text.Contains("[LockoutEnabled]", StringComparison.Ordinal)
            && !text.Contains("[PasswordHash]", StringComparison.Ordinal);
    }
}
