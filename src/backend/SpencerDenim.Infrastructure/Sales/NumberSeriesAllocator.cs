using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales;

/// <summary>
/// <c>sales.NumberSeries</c> allocation, exactly as doc 08 §3.5 specifies it.
/// <para>
/// <b>Why one statement and not a transaction around two.</b>
/// <c>UPDATE … SET NextValue = NextValue + 1 OUTPUT deleted.NextValue</c> reads and writes under the
/// same row lock, so two callers cannot be handed the same value: the second waits for the first's lock,
/// then increments whatever it finds. A <c>SELECT</c> followed by an <c>UPDATE</c> — even inside a
/// transaction at the default isolation level — gives both callers the same number, and the unique index
/// on <c>Orders.OrderNumber</c> would then reject one of them <i>after</i> its stock had been reserved.
/// </para>
/// <para>
/// <b>Why raw ADO and not EF.</b> The value is in the statement's <c>OUTPUT</c> result set.
/// <c>ExecuteSqlInterpolated</c> returns a row count and discards it, and <c>SqlQuery</c> composes the SQL
/// into a subquery, which an <c>UPDATE</c> cannot be. A <c>DbCommand</c> on EF's own connection and
/// transaction is the only shape that both runs the statement verbatim and reads what it emitted.
/// </para>
/// </summary>
internal sealed class NumberSeriesAllocator(AppDbContext dbContext, IClock clock) : INumberSeriesAllocator
{
    /// <summary>
    /// Passes of the allocate loop. The loser of an insert race needs exactly one more — its retry finds
    /// the winner's row and updates it — so three bounds the loop without being able to spin.
    /// </summary>
    private const int Attempts = 3;


    public async Task<int> AllocateAsync(string seriesKey, string prefix, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        for (var attempt = 1; ; attempt++)
        {
            var allocated = await TryAllocateAsync(seriesKey, cancellationToken);
            if (allocated is { } value)
            {
                return value;
            }

            if (attempt >= Attempts)
            {
                throw new InvalidOperationException(
                    $"Could not allocate from number series '{seriesKey}' after {Attempts} attempts.");
            }

            try
            {
                // Doc 08 §3.5's lazy insert: the first document of a new Colombo year creates the series.
                // NextValue starts at 1 and this call does *not* claim it — the loop goes back to the
                // UPDATE, which is the only place a value is ever handed out. That matters for the race:
                // when two callers both try to create the series, one inserts and the other gets 2627, and
                // then *both* allocate through the UPDATE and get 1 and 2 in some order. Claiming 1 as part
                // of the insert would have made the two paths different, and one of them untested.
                await InsertSeriesAsync(seriesKey, prefix, cancellationToken);
            }
            catch (Exception exception) when (SqlUniqueViolation.Matches(exception))
            {
                // Somebody else created it first. The next pass's UPDATE allocates from their row.
            }
        }
    }

    public async Task<string> AllocateOrderNumberAsync(
        DateTimeOffset placedAt,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            // Doc 08 §3.5 is explicit that order numbers are allocated in a *separate* transaction, "so
            // checkouts don't serialise on the row". Enlisting in a caller's transaction would hold the
            // series row for the length of that transaction — which, for the placement transaction, means
            // for the length of every stock reservation in the order. Every other checkout in the shop
            // would queue behind it. An exception rather than a silent enlistment, because the symptom
            // would be a throughput problem nobody could attribute.
            throw new InvalidOperationException(
                "An order number must be allocated before the placement transaction opens (doc 08 §3.5). "
                + "Use AllocateAsync for a number that belongs inside an existing transaction, such as an "
                + "invoice number.");
        }

        var year = ColomboTime.YearOf(placedAt);
        var seriesKey = DocumentNumber.OrderSeriesKey(year);

        // Its own transaction, committed before this returns. The execution strategy wraps it so a
        // deadlock or a transient fault re-runs the whole short unit; a re-run may consume a number, and
        // doc 08 and doc 11 P6 both allow gaps for exactly this reason.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        var sequence = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            var value = await AllocateAsync(seriesKey, DocumentNumber.OrderNumberPrefix, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return value;
        });

        return DocumentNumber.Format(DocumentNumber.OrderNumberPrefix, year, sequence);
    }

    /// <summary>
    /// Doc 08 §3.5's statement, verbatim. <see langword="null"/> when no row matched, which means the
    /// series does not exist yet.
    /// </summary>
    private async Task<int?> TryAllocateAsync(string seriesKey, CancellationToken cancellationToken)
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

            command.CommandText =
                """
                UPDATE sales.NumberSeries
                   SET NextValue = NextValue + 1, UpdatedAtUtc = @now
                OUTPUT deleted.NextValue
                 WHERE SeriesKey = @seriesKey;
                """;

            // The caller's transaction when there is one, so an invoice number allocated inside the Paid
            // transaction rolls back with it. Order numbers are allocated with a transaction of this
            // allocator's own (see AllocateOrderNumberAsync), which is also found here.
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

            AddParameter(command, "@now", clock.UtcNow.UtcDateTime);
            AddParameter(command, "@seriesKey", seriesKey);

            var scalar = await command.ExecuteScalarAsync(cancellationToken);

            return scalar is int value ? value : null;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task InsertSeriesAsync(string seriesKey, string prefix, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO sales.NumberSeries (SeriesKey, Prefix, NextValue, UpdatedAtUtc)
            VALUES ({seriesKey}, {prefix}, 1, {clock.UtcNow.UtcDateTime});
            """,
            cancellationToken);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

}
