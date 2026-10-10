using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Records every SQL command EF Core ran that SQL Server chose as a <b>deadlock victim</b> (error 1205),
/// with the start of its text, so a test can say not only <i>that</i> two code paths deadlocked but <i>which
/// statement lost</i>.
/// <para>
/// <b>Why a count of exceptions is the only honest measure.</b> A deadlock is invisible from outside: EF's
/// <c>SqlServerRetryingExecutionStrategy</c> treats 1205 as transient and re-runs the transaction, so the
/// request succeeds, every assertion on rows passes, and the cost shows up only as latency and as victims.
/// A test that asserted "ends consistent" alone would be green on a design that deadlocks every other
/// request.
/// </para>
/// <para>
/// It is a separate class from <see cref="CommandCountingInterceptor"/> on purpose: that one counts round
/// trips for the query-plan tests and this one counts failures, and a test should be able to register only
/// the question it is asking.
/// </para>
/// </summary>
public sealed partial class DeadlockVictimRecorder : DbCommandInterceptor
{
    private const int SqlDeadlockVictim = 1205;

    private readonly ConcurrentQueue<string> _victims = new();

    /// <summary>How many commands were chosen as a victim.</summary>
    public int Count => _victims.Count;

    /// <summary>
    /// The first 240 characters of each victim command, whitespace collapsed to single spaces, in the order
    /// they failed.
    /// </summary>
    public IReadOnlyList<string> Victims => [.. _victims];

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        Record(command, eventData.Exception);
        base.CommandFailed(command, eventData);
    }

    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData.Exception);
        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex Whitespace();

    private void Record(DbCommand command, Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException { Number: SqlDeadlockVictim })
            {
                var text = Whitespace().Replace(command.CommandText, " ").Trim();

                _victims.Enqueue(text.Length <= 240 ? text : text[..240]);
                return;
            }
        }
    }
}
