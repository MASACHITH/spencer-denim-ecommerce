using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Counts the SQL commands EF Core executes, so a test can prove a listing runs a bounded number of
/// round trips instead of one per row (doc 08 §10.3: storefront reads project, they never lazy-load).
/// </summary>
public sealed class CommandCountingInterceptor : DbCommandInterceptor
{
    /// <summary>SQL Server error 1205: this session was chosen as the deadlock victim.</summary>
    private const int DeadlockVictimError = 1205;

    private int _count;
    private int _deadlocks;

    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// How many commands this host ran that SQL Server killed as a deadlock victim (error 1205).
    /// </summary>
    /// <remarks>
    /// <b>Counted here, on the host's own commands, rather than read from
    /// <c>sys.dm_os_performance_counters</c>.</b> That counter is the whole server's, and the test server is
    /// shared: another run's deliberate contention would make a test about <i>this</i> code's locking fail.
    /// The execution strategy retries a victim silently (<c>EnableRetryOnFailure</c>), so without this a
    /// deadlock shows up only as slowness — or, past the retry limit, as an exception.
    /// </remarks>
    public int DeadlockCount => Volatile.Read(ref _deadlocks);

    public void Reset()
    {
        Volatile.Write(ref _count, 0);
        Volatile.Write(ref _deadlocks, 0);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        CountDeadlock(eventData.Exception);
        base.CommandFailed(command, eventData);
    }

    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        CountDeadlock(eventData.Exception);
        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    private void CountDeadlock(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && sql.Errors.Cast<SqlError>().Any(error => error.Number == DeadlockVictimError))
            {
                Interlocked.Increment(ref _deadlocks);
                return;
            }
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Interlocked.Increment(ref _count);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}
