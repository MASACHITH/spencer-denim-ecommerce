using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpencerDenim.Infrastructure.Health;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// Establishes doc 08 §9's reference data at startup, and seeds the demo catalogue when
/// <c>Seed__RunOnStartup</c> asks for it. It does not apply migrations (doc 08 §10.4: the migration bundle
/// is a separate CI step), so a database that is behind is reported rather than silently upgraded by the
/// web host.
/// </summary>
/// <remarks>
/// <b>The reference data is not behind a flag, and that is the fix for a shop that could not take an
/// order.</b> It used to be: both phases sat behind <c>Seed__RunOnStartup</c>, which defaults to
/// <c>false</c> and which nothing in this repository sets. A fresh deploy on the documented defaults —
/// create the database with §1.1's collation, <c>dotnet ef database update</c>, start the API — came up
/// with no districts, no delivery methods, no zone rates and no payment providers, which is a checkout
/// that refuses every order and an <c>Orders.DeliveryDistrictId NOT NULL</c> that cannot be satisfied.
/// <see cref="CatalogSeedRunner"/> carries the reasoning; the short version is that a district is not
/// production data and the flag means "production data belongs to the client".
///
/// <para>
/// <b>Why it may not fail the host.</b> The API is required to start without a working database: liveness,
/// the OpenAPI document and the readiness probe all have to answer, and six integration tests hold that
/// line. A seeder that threw out of <see cref="StartAsync"/> would take down the endpoints that explain
/// the problem, and would turn a SQL Server thirty seconds slower to accept connections than the API into
/// a crash loop.
/// </para>
/// <para>
/// <b>So the failure goes somewhere a machine can read it.</b> The first version of this argument rested
/// on "readiness reports the database" and that was <b>false</b> — the only <c>ready</c> check was
/// <c>CanConnectAsync()</c>, so a connectable database with no migrations applied answered
/// <c>/health/ready</c> with <c>200 Healthy</c> while every checkout returned 500. Now
/// <see cref="ReferenceDataState"/> is written here and read by <see cref="ReferenceDataHealthCheck"/>,
/// which is <c>ready</c>-tagged: the host starts, liveness and OpenAPI answer, and readiness says
/// <b>Unhealthy</b> with the reason until the data is actually in place. And because a host that is never
/// ready is an outage nobody recovers from, the attempt is <b>retried on a timer</b> until it succeeds —
/// a database that arrives late, or migrations applied afterwards, bring the host up on their own.
/// </para>
/// <para>
/// <b>Only one instance seeds at a time, and that is a correctness requirement rather than tidiness.</b>
/// Two or more instances starting at once against one fresh database all try to insert the same seed rows.
/// Where a unique index exists, one wins and the losers get 2627/2601 — which made every multi-replica
/// deployment, rolling restart and overlapping container restart print "cannot take an order" on a
/// perfectly healthy startup, and left the loser aborted at the <i>first</i> seeder having never reached
/// the sales reference data. <b>Where no unique index exists it is worse than noise:</b>
/// <c>catalog.SizeGuides</c> has none on <c>Name</c>, so two instances both found "Women's apparel"
/// missing and both inserted it, and the next pass's
/// <c>ToDictionaryAsync(guide =&gt; guide.Name, …)</c> threw on the duplicate. That is seed data corrupted
/// by a deployment doing nothing unusual, and no amount of retrying fixes it after the fact.
/// </para>
/// <para>
/// So the phase is serialised on a SQL Server <b>application lock</b>
/// (<c>sp_getapplock</c>/<c>sp_releaseapplock</c>, resource <see cref="SeedLockResource"/>, owner
/// <c>Session</c>). It needs no schema change — the alternative, a unique index on
/// <c>SizeGuides.Name</c>, is a migration, and migration ordering on this project is somebody else's
/// sequencing problem this round — and it covers every seed row rather than only the ones an index
/// happens to protect. The lock is held on one explicitly-opened connection for the length of the pass and
/// released in a <c>finally</c>; <c>Session</c> ownership means a process that dies mid-seed releases it
/// when its session ends, so there is no lock to clear by hand. Losers wait, acquire it when the winner is
/// done, run their idempotent pass, insert nothing, and come up ready.
/// </para>
/// <para>
/// <b>The unique-violation retry is kept underneath it</b> rather than replaced, for the cases the lock
/// cannot cover: an instance still running an older build that does not take it, or the
/// <c>ImportClientAssets</c>-style CLI invoking the runner directly. A unique violation on a row whose job
/// is to exist means somebody else put it there, so the attempt runs again from the top with a fresh
/// <c>DbContext</c> — the pattern <c>NumberSeriesAllocator</c> and <c>CartStore</c> already use, applied
/// at the level that owns the scope, because a retry on the same context would re-send exactly the rows
/// its failed <c>SaveChanges</c> is still tracking.
/// </para>
/// <para>
/// <b>Still owed:</b> a unique index on <c>catalog.SizeGuides.Name</c>. Every other seeded reference table
/// has one, this one does not, and the lock makes the gap unreachable rather than absent. It belongs to
/// whoever next migrates <c>catalog</c>.
/// </para>
/// <para>
/// <b>Why it is not left to deployment instead.</b> It could be: a documented "run the seeder after
/// migrating" step, or an init container. Two things decided against it. There is no compose file and no
/// deploy script in the repository for such a step to live in, so the instruction would exist only in
/// prose — and the defect being fixed <i>is</i> an instruction that existed only in prose and was not
/// followed. And the data is not optional in the way a deployment step implies; the application cannot
/// serve its purpose without it. If the owner prefers it at deployment level, the phase to call is
/// <see cref="CatalogSeedRunner.RunReferenceDataAsync"/> and nothing else here changes.
/// </para>
/// </remarks>
internal sealed partial class CatalogSeedHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<SeedOptions> options,
    ReferenceDataState state,
    ILogger<CatalogSeedHostedService> logger)
    : IHostedService, IDisposable
{
    /// <summary>
    /// How many times one attempt re-runs when another instance wins a seed row.
    /// </summary>
    /// <remarks>
    /// <b>Not three, and the number is the reasoning.</b> Every pass re-reads and inserts only what is
    /// missing, so each pass strictly reduces the work left for everyone — but a pass can only lose on the
    /// step it has reached, and the reference data is about a dozen separate <c>SaveChanges</c> batches
    /// (departments, age groups, attribute options, colour families, sizes, size guides, categories,
    /// provinces, districts, delivery methods, zone rates, payment providers, home sections, tiles,
    /// benefits, announcements, settings). Three instances racing can therefore hand a loser a fresh
    /// collision on a *later* step each time round. Three attempts got as far as <c>ops.Settings</c> and
    /// gave up one step from the end, which is how this number was chosen rather than guessed: it is the
    /// batch count with room to spare, and a pass over a seeded database is about a dozen SELECTs and no
    /// writes.
    /// <para>
    /// Convergence is not luck: a collision means another instance <i>committed</i> that row, so the set
    /// of rows still missing anywhere strictly shrinks, and once any instance finishes, one clean pass by
    /// each of the others succeeds. <c>NumberSeriesAllocator</c> takes the same race with no cap at all;
    /// the cap here is only so that a genuinely repeating unique violation — a seed row colliding with
    /// something that is not another seeder — is eventually reported rather than looped on.
    /// </para>
    /// </remarks>
    private const int ContentionAttempts = 20;

    /// <summary>
    /// The longest pause before re-reading after losing a row, so the instance that won gets to commit
    /// the rest of its batch instead of all three racing the same step again. Jittered, and small enough
    /// that a single-instance startup — which never reaches it — pays nothing.
    /// </summary>
    private static readonly TimeSpan ContentionBackoff = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The <c>sp_getapplock</c> resource name. One string for the whole reference-data phase, because the
    /// phase is what has to be exclusive — a lock per seeder would let two instances interleave steps,
    /// which is the thing being prevented.
    /// </summary>
    private const string SeedLockResource = "SpencerDenim:ReferenceDataSeed";

    /// <summary>
    /// How long to wait for the lock before giving up and treating it as contention. Generously longer
    /// than a seeding pass takes (about a second against an empty database, a dozen SELECTs against a
    /// seeded one), so a waiter only ever times out if something is genuinely stuck.
    /// </summary>
    private static readonly TimeSpan SeedLockTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long to wait before trying again after a real failure. Long enough that a SQL Server coming up
    /// is not hammered, short enough that a deployment which applies migrations after starting the API is
    /// serving within the minute.
    /// </summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _stopping = new();

    private Task? _retrying;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (await TryEstablishAsync(cancellationToken))
        {
            return;
        }

        // Not with `cancellationToken`: that one is the *startup* token and is done with once StartAsync
        // returns. The retry loop lives until StopAsync.
        _retrying = Task.Run(() => RetryUntilEstablishedAsync(_stopping.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();

        if (_retrying is { } retrying)
        {
            // The loop swallows its own cancellation, so this is just "wait for it to notice".
            await retrying;
            _retrying = null;
        }
    }

    public void Dispose() => _stopping.Dispose();

    /// <summary>
    /// One attempt at the reference data, re-run when another instance wins a row. True once the data is
    /// in place; false when it could not be established, with the reason recorded for readiness.
    /// </summary>
    private async Task<bool> TryEstablishAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // A scope per attempt, deliberately: a failed SaveChangesAsync leaves its entities tracked,
                // and retrying on the same DbContext would re-send exactly the rows that collided.
                using var scope = scopeFactory.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<CatalogSeedRunner>();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Opened by hand and kept open, because `sp_getapplock` with owner `Session` lives on the
                // connection: letting EF take one from the pool per statement would release the lock
                // immediately and acquire nothing.
                await dbContext.Database.OpenConnectionAsync(cancellationToken);

                try
                {
                    if (!await TryAcquireSeedLockAsync(dbContext, cancellationToken))
                    {
                        LogLockBusy(logger, attempt);
                        await Task.Delay(Jitter(), cancellationToken);
                        continue;
                    }

                    try
                    {
                        var counts = await runner.RunReferenceDataAsync(cancellationToken);

                        // Ready as soon as the reference data is in. The demo catalogue is
                        // development-only sample data: a shop with no sample products is a shop, a shop
                        // with no districts is not.
                        state.Established();

                        await SeedDemoCatalogueIfAskedAsync(runner, counts, cancellationToken);

                        runner.LogCounts(counts);
                    }
                    finally
                    {
                        await ReleaseSeedLockAsync(dbContext);
                    }
                }
                finally
                {
                    await dbContext.Database.CloseConnectionAsync();
                }

                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down, or the host is going away. Not a seeding failure.
                throw;
            }
            catch (Exception exception)
                when (SqlUniqueViolation.Matches(exception) && attempt < ContentionAttempts)
            {
                // Another instance is seeding the same fresh database. Information, not Error: this is the
                // normal shape of a multi-replica or rolling startup, and it is about to succeed.
                LogContended(logger, attempt, exception);

                await Task.Delay(Jitter(), cancellationToken);
            }
            catch (Exception exception)
            {
                // Deliberately broad: anything that stops the reference data landing — an unreachable
                // server, a database behind on migrations, a permission — has the same consequence and the
                // same remedy, and none of them may stop the host explaining itself.
                state.Failed(
                    "The reference data could not be established, so this host cannot take an order: "
                    + Explain(exception));

                LogSeedingFailed(logger, exception);

                return false;
            }
        }
    }

    /// <summary>
    /// Keeps attempting until the reference data is established or the host stops, so a late database or
    /// late migrations do not leave a host permanently unready.
    /// </summary>
    private async Task RetryUntilEstablishedAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(RetryDelay);

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!await TryEstablishAsync(cancellationToken))
                {
                    continue;
                }

                LogEstablishedOnRetry(logger);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping. Nothing to report: readiness already says what the state is.
        }
    }

    /// <summary>
    /// The sample catalogue, which is development and staging only and still needs both flags. It runs
    /// inside the caller's scope and on the caller's counts, so the one "Catalogue seeding inserted …"
    /// line reports both phases together.
    /// </summary>
    /// <remarks>
    /// <b>Its failure is logged and no more.</b> It is not what readiness is about, and swallowing it here
    /// rather than letting it reach <see cref="TryEstablishAsync"/>'s handler is deliberate: a broken
    /// demo seeder must not make a correctly seeded production host report itself unable to take an order.
    /// </remarks>
    private async Task SeedDemoCatalogueIfAskedAsync(
        CatalogSeedRunner runner,
        SeedCounts counts,
        CancellationToken cancellationToken)
    {
        var seedOptions = options.Value;

        // Both flags. `RunOnStartup` keeps its meaning as "this host may seed optional data", so an
        // existing deployment's variables do exactly what they did before.
        if (!seedOptions.RunOnStartup || !seedOptions.IncludeDemoCatalogue)
        {
            return;
        }

        LogSeedingDemoCatalogue(logger);

        try
        {
            await runner.RunDemoCatalogueAsync(counts, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogDemoCatalogueFailed(logger, exception);
        }
    }

    /// <summary>
    /// Takes the exclusive application lock for the reference-data phase. False when it could not be had
    /// inside <see cref="SeedLockTimeout"/>, which the caller treats as contention and retries.
    /// </summary>
    /// <remarks>
    /// <c>sp_getapplock</c> returns 0 (granted) or 1 (granted after waiting) on success, and -1 (timeout),
    /// -2 (cancelled), -3 (deadlock victim) or -999 (bad parameter) otherwise. Only 0 and 1 are a lock.
    /// </remarks>
    private static async Task<bool> TryAcquireSeedLockAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = "sp_getapplock";
        command.CommandType = System.Data.CommandType.StoredProcedure;

        AddParameter(command, "@Resource", SeedLockResource);
        AddParameter(command, "@LockMode", "Exclusive");
        AddParameter(command, "@LockOwner", "Session");
        AddParameter(command, "@LockTimeout", (int)SeedLockTimeout.TotalMilliseconds);

        var result = command.CreateParameter();
        result.ParameterName = "@Result";
        result.DbType = System.Data.DbType.Int32;
        result.Direction = System.Data.ParameterDirection.ReturnValue;
        command.Parameters.Add(result);

        await command.ExecuteNonQueryAsync(cancellationToken);

        return result.Value is int outcome && outcome >= 0;
    }

    /// <summary>
    /// Releases the lock. Best effort and never throws: the connection is about to be closed, which ends
    /// the session and releases it anyway, and a failure here must not mask the seeding outcome the
    /// caller is in the middle of reporting.
    /// </summary>
    private async Task ReleaseSeedLockAsync(AppDbContext dbContext)
    {
        try
        {
            var connection = dbContext.Database.GetDbConnection();

            await using var command = connection.CreateCommand();
            command.CommandText = "sp_releaseapplock";
            command.CommandType = System.Data.CommandType.StoredProcedure;

            AddParameter(command, "@Resource", SeedLockResource);
            AddParameter(command, "@LockOwner", "Session");

            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (SqlException exception)
        {
            LogLockReleaseFailed(logger, exception);
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>A random pause up to <see cref="ContentionBackoff"/>, so the losers do not re-collide in step.</summary>
    private static TimeSpan Jitter() =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(1, (int)ContentionBackoff.TotalMilliseconds));

    /// <summary>
    /// The exception as one sentence, inner exceptions included.
    /// </summary>
    /// <remarks>
    /// EF wraps everything in "An error occurred while saving the entity changes. See the inner exception
    /// for details", which is the least useful sentence in .NET and was all that reached
    /// <c>/health/ready</c>. The inner chain is where "Invalid object name 'catalog.Departments'" and
    /// "Cannot insert duplicate key row" actually live, and those are the two an operator needs.
    /// </remarks>
    private static string Explain(Exception exception)
    {
        var parts = new List<string>();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            parts.Add(current.Message);
        }

        return string.Join(" -> ", parts);
    }

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Seeding the demo catalogue on startup (Seed__IncludeDemoCatalogue).")]
    private static partial void LogSeedingDemoCatalogue(ILogger logger);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Error,
        Message = "Could not establish the reference data at startup. Until this succeeds the shop has no "
            + "districts, delivery methods, zone rates or payment providers and cannot take an order, and "
            + "/health/ready reports this host as unhealthy. Check the database connection and run "
            + "'dotnet ef database update'. Retrying in the background.")]
    private static partial void LogSeedingFailed(ILogger logger, Exception exception);

    // The exception travels as the entry's exception rather than interpolated into the message: CA1873
    // forbids computing an argument that logging may discard, and a logger renders the inner chain anyway.
    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Information,
        Message = "Another instance seeded a reference-data row first on attempt {Attempt}; re-reading and "
            + "inserting what is still missing.")]
    private static partial void LogContended(ILogger logger, int attempt, Exception exception);

    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Information,
        Message = "Another instance holds the reference-data seed lock (attempt {Attempt}); waiting.")]
    private static partial void LogLockBusy(ILogger logger, int attempt);

    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Warning,
        Message = "Could not release the reference-data seed lock; closing the connection ends its session "
            + "and releases it, so this is reported rather than acted on.")]
    private static partial void LogLockReleaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Information,
        Message = "The reference data is now established; this host is ready.")]
    private static partial void LogEstablishedOnRetry(ILogger logger);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Error,
        Message = "The demo catalogue could not be seeded. The reference data is in place, so the shop "
            + "works; it simply has no sample products.")]
    private static partial void LogDemoCatalogueFailed(ILogger logger, Exception exception);
}
