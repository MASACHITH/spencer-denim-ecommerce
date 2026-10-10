using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Holds back every <c>UPDATE</c> of <c>identity.UserTokens</c> by a fixed delay while <see cref="Enabled"/>. That table
/// is where a redeemed recovery code and the last accepted authenticator step are recorded, so with the delay on,
/// every request of a parallel burst has read the old state and passed the code check before any of them records
/// that the code was used. It turns the timing-dependent "N requests with one code all get through" race into the
/// worst case on every run, which is what the pending-sign-in claim has to survive (issue #46).
/// </summary>
public sealed class TokenWriteDelayInterceptor(TimeSpan delay) : DbCommandInterceptor
{
    private volatile bool _enabled;

    /// <summary>Off at construction, so setting a test up (enrolment, the password step) runs at full speed.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await DelayTokenWriteAsync(command, cancellationToken);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await DelayTokenWriteAsync(command, cancellationToken);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private async Task DelayTokenWriteAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (_enabled && command.CommandText.Contains("UPDATE [identity].[UserTokens]", StringComparison.Ordinal))
        {
            await Task.Delay(delay, cancellationToken);
        }
    }
}
