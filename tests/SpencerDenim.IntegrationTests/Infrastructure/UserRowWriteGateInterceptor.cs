using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Holds back <b>one</b> write: the <c>UPDATE</c> of <c>identity.Users</c> that records a completed sign-in
/// (<c>LastLoginAtUtc</c>), for the stale-user-row defect of issue #250.
/// <para>
/// That write is the last thing a sign-in does before a session is issued, and it comes <i>after</i> the password (or
/// the code) has been checked against the account as it was when the request loaded it. The defect lives in the gap:
/// an administrator deactivates the account, or the owner changes their password, while the check is still running,
/// and the write then puts the old row back or lets a session out for an account that has just been closed. No
/// sequential test can reach it, so the request is stopped <b>at</b> the write, the change is made while it waits, and
/// it is let go to find out.
/// </para>
/// <para>
/// It matches on the statement's shape, so it works on both sides of the fix: before it, the write is Entity
/// Framework's <c>UPDATE</c> listing every column of the row, <c>LastLoginAtUtc</c> among them; after it, the write is
/// the conditional statement that sets <c>LastLoginAtUtc</c> and the two audit columns and nothing else. Locking
/// statements (<c>AccessFailedCount</c> and
/// <c>LockoutEnd</c>) do not mention <c>LastLoginAtUtc</c> and are never held. Usage: <see cref="Arm"/>, send the sign-in,
/// <c>await</c> <see cref="WaitUntilHeldAsync"/>, make the change, <see cref="Release"/>.
/// </para>
/// <para>
/// <b>It must not be able to pass by matching nothing.</b> If the statement's shape changes the gate never fires and
/// <see cref="WaitUntilHeldAsync"/> fails with a message naming the matcher. The held request waits at most
/// <see cref="HoldLimit"/>, so a test that fails before releasing cannot leave the host hanging.
/// </para>
/// </summary>
public sealed class UserRowWriteGateInterceptor : DbCommandInterceptor
{
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed;

    /// <summary>Off at construction, so setting a test up runs at full speed. Holds the first matching write only.</summary>
    public void Arm() => Volatile.Write(ref _armed, 1);

    /// <summary>Lets the held write go.</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>Completes when a write is stopped at the gate; throws if none arrives in time.</summary>
    public async Task WaitUntilHeldAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _held.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                "No request reached the sign-in's user-row write within " + timeout + ". Either the sign-in no longer "
                + "writes an UPDATE of identity.Users that names LastLoginAtUtc (AdminAuthenticator.SucceedAsync), or the "
                + "table or column was renamed; in both cases this gate's matcher is out of date and the test proves "
                + "nothing until it is updated.");
        }
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await HoldIfSignInWriteAsync(command, cancellationToken);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await HoldIfSignInWriteAsync(command, cancellationToken);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private async Task HoldIfSignInWriteAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (IsSignInWrite(command) && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
        {
            _held.TrySetResult();
            await _released.Task.WaitAsync(HoldLimit, cancellationToken);
        }
    }

    private static bool IsSignInWrite(DbCommand command)
    {
        var text = command.CommandText;

        return text.Contains("UPDATE [identity].[Users]", StringComparison.Ordinal)
            && text.Contains("[LastLoginAtUtc]", StringComparison.Ordinal);
    }
}
