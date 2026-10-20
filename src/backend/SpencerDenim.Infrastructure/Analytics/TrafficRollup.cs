using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>
/// Doc 08 §8.3's traffic half of <c>NightlyStats</c>: "rebuild <c>TrafficDaily</c> and <c>PageDaily</c> for the
/// last 3 Colombo days, idempotently: <c>DELETE</c> range + <c>INSERT … SELECT</c>".
/// <para>
/// <b>Idempotent and safe to run twice at once.</b> The delete and both inserts are one transaction, which
/// first takes an exclusive application lock (<c>sp_getapplock</c>, doc 08 §1.6), so two hosts — or a host and
/// a test — rebuilding the same days run one after the other instead of interleaving a delete with an insert.
/// Running it again over the same days produces the same rows. No isolation level is set: the transaction is
/// at the connection's default, and the connection goes back to the pool as it came.
/// </para>
/// </summary>
internal sealed class TrafficRollup(AppDbContext dbContext)
{
    /// <summary>The application lock's resource name.</summary>
    public const string LockResource = "analytics.TrafficRollup";

    /// <summary>How long a rebuild waits for another to finish before giving up.</summary>
    public const int LockTimeoutMilliseconds = 60_000;

    /// <summary>Rebuilds <c>TrafficDaily</c> and <c>PageDaily</c> for the Colombo days <paramref name="from"/>–<paramref name="to"/>.</summary>
    public async Task RebuildAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (to < from)
        {
            return;
        }

        var fromUtc = ReportPeriod.ColomboMidnightUtc(from);
        var toUtc = ReportPeriod.ColomboMidnightUtc(to.AddDays(1));

        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync(
                $"""
                DECLARE @lock int;
                EXEC @lock = sp_getapplock @Resource = N'{LockResource}', @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction', @LockTimeout = {LockTimeoutMilliseconds};
                IF @lock < 0 THROW 50403, 'Another traffic rollup is still running.', 1;
                """,
                cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync(
                "DELETE FROM [analytics].[TrafficDaily] WHERE [Date] >= @fromDay AND [Date] <= @toDay;"
                + " DELETE FROM [analytics].[PageDaily] WHERE [Date] >= @fromDay AND [Date] <= @toDay;",
                [Day("@fromDay", from), Day("@toDay", to)],
                cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync(
                TrafficSql.InsertTrafficDaily(),
                [Utc(TrafficSql.FromUtc, fromUtc), Utc(TrafficSql.ToUtc, toUtc)],
                cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync(
                TrafficSql.InsertPageDaily(),
                [Utc(TrafficSql.FromUtc, fromUtc), Utc(TrafficSql.ToUtc, toUtc)],
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });
    }

    /// <summary>
    /// Colombo days from <paramref name="from"/> up to, not including, <paramref name="before"/> that have raw views
    /// but were never rolled up.
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> FindUnbuiltDaysAsync(DateOnly from, DateOnly before, CancellationToken cancellationToken)
    {
        if (before <= from)
        {
            return [];
        }

        var days = await dbContext.Database
            .SqlQueryRaw<DateOnly>(
                TrafficSql.UnbuiltDays,
                Utc(TrafficSql.FromUtc, ReportPeriod.ColomboMidnightUtc(from)),
                Utc(TrafficSql.ToUtc, ReportPeriod.ColomboMidnightUtc(before)))
            .ToListAsync(cancellationToken);

        return days.Order().ToList();
    }

    /// <summary>
    /// The oldest <paramref name="take"/> never-built days before <paramref name="before"/> that still have raw rows,
    /// oldest first, with no lower bound (<see cref="TrafficSql.OldestUnbuiltDaysBefore"/>).
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> FindOldestUnbuiltDaysAsync(DateOnly before, int take, CancellationToken cancellationToken)
    {
        if (take <= 0)
        {
            return [];
        }

        var days = await dbContext.Database
            .SqlQueryRaw<DateOnly>(
                TrafficSql.OldestUnbuiltDaysBefore,
                Utc(TrafficSql.ToUtc, ReportPeriod.ColomboMidnightUtc(before)),
                new SqlParameter("@take", System.Data.SqlDbType.Int) { Value = take })
            .ToListAsync(cancellationToken);

        return days.Order().ToList();
    }

    internal static SqlParameter Utc(string name, DateTime value) =>
        new(name, System.Data.SqlDbType.DateTime2) { Value = value, Scale = 0 };

    internal static SqlParameter Day(string name, DateOnly value) =>
        new(name, System.Data.SqlDbType.Date) { Value = value.ToDateTime(TimeOnly.MinValue) };
}
