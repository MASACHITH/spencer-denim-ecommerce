using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Holds back <b>one</b> write: the <c>INSERT</c> or <c>UPDATE</c> of the pending-2FA-sign-in row in
/// <c>identity.UserTokens</c> that a password step makes when it opens a pending sign-in (issue #145).
/// <para>
/// The race the issue describes lives between that step's <i>read</i> of the account's state and its <i>write</i>, and
/// no sequential test can reach it: a request that runs after another sees the other's result. So the password step is
/// stopped <b>at</b> the write, the other request is run to completion while it waits, and it is let go to find out
/// that what it read is no longer true.
/// </para>
/// <para>
/// Usage: <see cref="Arm"/>, send the password step, <c>await</c> <see cref="WaitUntilHeldAsync"/>, run whatever must
/// happen in between, <see cref="Release"/>.
/// </para>
/// <para>
/// <b>It must not be able to pass by matching nothing.</b> If the statement's shape changes the gate never fires and
/// <see cref="WaitUntilHeldAsync"/> fails with a message naming the matcher, instead of the test running on against a
/// request that was never held. The held request waits at most <see cref="HoldLimit"/>, so a test that fails before
/// releasing cannot leave the host hanging.
/// </para>
/// </summary>
public sealed class PendingSignInWriteGateInterceptor : DbCommandInterceptor
{
    /// <summary>
    /// <c>AdminTokenNames.PendingTwoFactorSignIn</c>. That class is internal to the infrastructure assembly, so the
    /// literal is repeated here; if it ever changes, the gate stops matching and says so.
    /// </summary>
    private const string PendingTokenName = "PendingTwoFactorSignIn";

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
                "No request reached the pending sign-in write within " + timeout + ". Either the password step no "
                + "longer writes an INSERT or UPDATE of identity.UserTokens whose Name parameter is "
                + $"'{PendingTokenName}' (AdminAuthenticator.OpenPendingSignInAsync), or that token was renamed; in "
                + "both cases this gate's matcher is out of date and the test proves nothing until it is updated.");
        }
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await HoldIfPendingSignInWriteAsync(command, cancellationToken);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await HoldIfPendingSignInWriteAsync(command, cancellationToken);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private async Task HoldIfPendingSignInWriteAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (IsPendingSignInWrite(command) && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
        {
            _held.TrySetResult();
            await _released.Task.WaitAsync(HoldLimit, cancellationToken);
        }
    }

    private static bool IsPendingSignInWrite(DbCommand command)
    {
        var text = command.CommandText;

        return text.Contains("[identity].[UserTokens]", StringComparison.Ordinal)
            && (text.Contains("INSERT INTO [identity].[UserTokens]", StringComparison.Ordinal)
                || text.Contains("UPDATE [identity].[UserTokens]", StringComparison.Ordinal))
            && command.Parameters.Cast<DbParameter>().Any(p => p.Value is string name
                && string.Equals(name, PendingTokenName, StringComparison.Ordinal));
    }
}
