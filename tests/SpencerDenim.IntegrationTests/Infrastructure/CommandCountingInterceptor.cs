using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Counts the SQL commands EF Core executes, so a test can prove a listing runs a bounded number of
/// round trips instead of one per row (doc 08 §10.3: storefront reads project, they never lazy-load).
/// </summary>
public sealed class CommandCountingInterceptor : DbCommandInterceptor
{
    private int _count;

    /// <summary>
    /// How many executed commands contained each watched substring, by substring.
    /// </summary>
    /// <remarks>
    /// <b>This exists to prove a race test is a race</b> (W3-05, <c>CheckoutPromoRaceTests</c>). Counting
    /// how many concurrent attempts <i>reach</i> a contended statement is a different question from what
    /// the end state is, and only the first can tell a guard that works from a test that could not fail:
    /// a four-way race in which three attempts short-circuited before the contended statement produces
    /// the same end state as one that was correctly serialised, and the guard can then be deleted with
    /// nothing going red.
    /// <para>
    /// A substring of the command text rather than a parsed statement, because what a test wants to
    /// identify is "that statement", and a distinctive fragment of it is the cheapest honest way to say
    /// so. A fragment that stops matching counts zero and the assertion fails loudly, which is the right
    /// failure: the test is then measuring nothing and says so.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, int> _watched = new(StringComparer.Ordinal);

    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Starts counting commands whose text contains <paramref name="substring"/>, from zero. Calling it
    /// again resets that substring's count, so one test can measure round after round.
    /// </summary>
    public void Watch(string substring)
    {
        ArgumentException.ThrowIfNullOrEmpty(substring);

        _watched[substring] = 0;
    }

    /// <summary>
    /// How many commands containing <paramref name="substring"/> have executed since
    /// <see cref="Watch"/>. Throws rather than answering zero for a substring nobody asked to watch: a
    /// test that measured the wrong string would otherwise read as a test that measured nothing.
    /// </summary>
    public int CountOf(string substring)
    {
        ArgumentException.ThrowIfNullOrEmpty(substring);

        return _watched.TryGetValue(substring, out var count)
            ? count
            : throw new InvalidOperationException(
                $"Nothing is watching commands containing '{substring}'. Call Watch first.");
    }

    public void Reset() => Volatile.Write(ref _count, 0);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Observe(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Observe(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Observe(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Observe(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <summary>
    /// Counts one executed command: the total, and each watched substring it contains.
    /// </summary>
    /// <remarks>
    /// <c>Interlocked</c> and <c>AddOrUpdate</c> rather than plain increments, because the whole point of
    /// the watches is concurrency: four HTTP requests in flight at once reach this from four threads.
    /// </remarks>
    private void Observe(DbCommand command)
    {
        Interlocked.Increment(ref _count);

        if (_watched.IsEmpty)
        {
            return;
        }

        var text = command.CommandText;

        foreach (var substring in _watched.Keys)
        {
            if (text.Contains(substring, StringComparison.Ordinal))
            {
                _watched.AddOrUpdate(substring, 1, (_, current) => current + 1);
            }
        }
    }
}
