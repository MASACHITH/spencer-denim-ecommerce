using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// <see cref="IDuePaymentEvents"/> over <c>sales.PaymentEvents</c>: the <c>RetryPaymentEvents</c> job's claim
/// (doc 08 §3.5 <c>IX_PaymentEvents_Retry</c>).
/// <para>
/// <b>The claim is the guard, and it is the one the email outbox uses</b> (<c>EmailOutboxStore</c>): an
/// <c>UPDATE TOP (n) … OUTPUT</c> that selects due rows with <c>UPDLOCK, READPAST</c> and pushes each row's
/// <c>NextAttemptAtUtc</c> forward by a lease in the same atomic statement. A second sweep that overlaps the
/// first <i>skips</i> the rows the first holds instead of queueing behind them, and finds nothing due once the
/// first has committed; a worker that dies leaves a row whose lease runs out and which is then due again.
/// There is no <c>ops.JobLeases</c> table in this build, and none is needed: a lease would make the <i>job</i>
/// run once, and the claim makes each <i>event</i> be worked once, which is what matters.
/// </para>
/// <para>
/// <b>Raw ADO, for the outbox's reason</b>: the claimed rows are in the statement's <c>OUTPUT</c> set, which
/// <c>ExecuteSqlInterpolated</c> discards and <c>SqlQuery</c> cannot wrap. The statuses and the source are
/// <b>literals in the command text</b> (DB28): <c>ProcessingStatus IN (0, 3)</c> is the filter of
/// <c>IX_PaymentEvents_Retry</c>, and a parameter in its place would scan the table of stored payloads every
/// sweep.
/// </para>
/// </summary>
internal sealed class DuePaymentEvents(AppDbContext dbContext) : IDuePaymentEvents
{
    /// <summary>
    /// The statement, verbatim. <c>EventSource = 1</c> is the verified webhook: the log also holds our own API
    /// responses and the buyer's return redirect, which are records and not work.
    /// </summary>
    internal const string ClaimSql =
        """
        UPDATE TOP (@take) e
           SET NextAttemptAtUtc = @leaseUntil
        OUTPUT inserted.Id, inserted.EventType, inserted.PayloadJson, inserted.AttemptCount, inserted.ReceivedAtUtc
          FROM sales.PaymentEvents e WITH (UPDLOCK, READPAST)
         WHERE e.ProviderCode = @provider
           AND e.EventSource = 1
           AND e.ProcessingStatus IN (0, 3)
           AND e.NextAttemptAtUtc <= @now;
        """;

    public async Task<IReadOnlyList<DuePaymentEvent>> ClaimDueAsync(
        string providerCode,
        DateTime nowUtc,
        TimeSpan lease,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerCode);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);

        // Named explicitly, for the outbox's reason: EF wraps its own single operations in the execution
        // strategy but cannot wrap a raw DbCommand. The statement is atomic and conditional, so a retry after
        // a lost answer claims nothing it should not.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(() => ClaimOnceAsync(providerCode, nowUtc, lease, take, cancellationToken));
    }

    private async Task<IReadOnlyList<DuePaymentEvent>> ClaimOnceAsync(
        string providerCode,
        DateTime nowUtc,
        TimeSpan lease,
        int take,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = false;

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
            openedHere = true;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = ClaimSql;

            Add(command, "@take", DbType.Int32, take);
            Add(command, "@leaseUntil", DbType.DateTime2, nowUtc + lease);
            Add(command, "@provider", DbType.AnsiString, providerCode);
            Add(command, "@now", DbType.DateTime2, nowUtc);

            var claimed = new List<DuePaymentEvent>();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(new DuePaymentEvent(
                    reader.GetInt64(0),
                    providerCode,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt16(3),
                    DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)));
            }

            return claimed.OrderBy(candidate => candidate.ReceivedAtUtc).ThenBy(candidate => candidate.EventId).ToList();
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
