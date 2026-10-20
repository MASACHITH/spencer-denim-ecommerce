using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Removes <c>WITH (READCOMMITTEDLOCK)</c> from <b>one</b> statement of the code under test on its way to SQL
/// Server, so a test can run the production statement without the hint it carries. That is a mutation test
/// that needs no edit to the source: the same scenario is run with the hint and without it, and the pair shows
/// both that the scenario can fail and that the hint is what stops it (issue #185).
/// <para>
/// The statement is the application's own, executed by its own store, and the only change is the removed
/// table hint, so the run measures the production SQL and not a copy of it.
/// </para>
/// <para>
/// <b>It must not be able to pass by matching nothing.</b> <see cref="Stripped"/> counts the statements that
/// actually lost a hint; a test asserts it is one, so a selector that has drifted away from the statement's
/// text, or a hint that is no longer there, fails loudly instead of running the scenario unmodified.
/// </para>
/// </summary>
public sealed partial class HintStrippingInterceptor : DbCommandInterceptor
{
    private volatile Func<string, bool>? _selects;
    private int _stripped;

    /// <summary>How many selected statements lost a hint since <see cref="Arm"/>.</summary>
    public int Stripped => Volatile.Read(ref _stripped);

    /// <summary>
    /// From now on removes the hint from every command whose text <paramref name="selects"/> accepts, and
    /// restarts <see cref="Stripped"/> from zero.
    /// </summary>
    public void Arm(Func<string, bool> selects)
    {
        ArgumentNullException.ThrowIfNull(selects);

        Volatile.Write(ref _stripped, 0);
        _selects = selects;
    }

    /// <summary>Leaves every command alone again.</summary>
    public void Disarm() => _selects = null;

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Strip(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Strip(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Strip(DbCommand command)
    {
        var text = command.CommandText;

        if (_selects is not { } selects || !selects(text))
        {
            return;
        }

        var without = ReadCommittedLockHint().Replace(text, string.Empty);

        if (without != text)
        {
            command.CommandText = without;
            Interlocked.Increment(ref _stripped);
        }
    }

    [GeneratedRegex(@"\s*WITH\s*\(\s*READCOMMITTEDLOCK\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadCommittedLockHint();
}
