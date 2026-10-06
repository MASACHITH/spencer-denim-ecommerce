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
    /// <c>SqlException.Number</c> for "Execution Timeout Expired" — the client giving up on a command,
    /// not the server refusing one. The 258 in its message is the inner Win32 wait code.
    /// </summary>
    private const int ClientTimeout = -2;

    /// <summary>
    /// How long <c>sp_getapplock</c> waits for the lock before answering -1, which the caller treats as
    /// contention and retries.
    /// </summary>
    /// <remarks>
    /// <b>Fifteen seconds, and the number has to sit under ADO's command timeout to mean anything.</b>
    /// The first version asked SQL Server to wait 60 s on a raw <c>DbCommand</c> whose
    /// <see cref="System.Data.Common.DbCommand.CommandTimeout"/> was left at ADO's 30 s default — so the
    /// client gave up at 32 s with "Execution Timeout Expired", SQL Server never reached its own timeout,
    /// and <c>@LockTimeout</c> was configuration that could not take effect. Worse, the resulting
    /// <c>SqlException</c> is not a unique violation, so it landed in the broad catch and reported
    /// "cannot take an order" with the wrong remedy, on a database that was fine. The graceful
    /// contention branch never fired once.
    /// <para>
    /// So the two are now set together and deliberately apart: SQL Server answers -1 first and the
    /// contention path is the one that runs.
    /// </para>
    /// <para>
    /// <b>Five seconds, not sixty, and the reason is <see cref="StartAsync"/>.</b> Hosted services are
    /// started before Kestrel listens, so every second spent waiting here is a second in which the host
    /// answers nothing at all — including the readiness probe that exists to explain the wait. A minute of
    /// patience would have made the signal unreachable for a minute, which is the same defect this lock's
    /// timeout already had in a different costume. Five seconds is generous for what the lock now covers:
    /// the reference data alone, about a second against an empty database and a dozen SELECTs against a
    /// seeded one, with the demo catalogue outside. Three attempts of it bound the startup delay at about
    /// fifteen seconds, and after that the retry timer takes over with the host up and answering.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan SeedLockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The ADO command timeout for the <c>sp_getapplock</c> call. Above <see cref="SeedLockTimeout"/> on
    /// purpose, so the server's own "-1, busy" is what comes back rather than the client's exception.
    /// </summary>
    private static readonly TimeSpan SeedLockCommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many times to wait for the lock before leaving it to the retry timer.
    /// </summary>
    /// <remarks>
    /// <b>A cap, because the first version had none.</b> The lock-busy branch was
    /// <c>for (var attempt = 1; ; attempt++)</c> with a bare <c>continue</c>, so a holder that never
    /// released would have been waited on inside <c>StartAsync</c> for ever. The unique-violation branch
    /// has had a cap all along and this one now matches it in kind: when the attempts run out the host
    /// says it is still waiting — at Information, not Error — and the 10 s retry loop keeps trying, which
    /// is the same self-healing path a late database takes.
    /// <para>
    /// Three, with <see cref="SeedLockTimeout"/> at five seconds, so the most a contended startup can
    /// delay the host answering anything is about fifteen seconds. The cap is not "give up": nothing is
    /// given up, the waiting simply moves off the startup path and onto the timer.
    /// </para>
    /// </remarks>
    private const int SeedLockAttempts = 3;

    /// <summary>
    /// How long to wait before trying again after a real failure. Long enough that a SQL Server coming up
    /// is not hammered, short enough that a deployment which applies migrations after starting the API is
    /// serving within the minute.
    /// </summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _stopping = new();

    private Task? _retrying;

    /// <summary>
    /// Whether the failure has already been reported at <see cref="LogLevel.Error"/> with its stack trace.
    /// </summary>
    /// <remarks>
    /// <b>The retry loop logged the whole exception at Error every ten seconds</b> — thirty SQL stack
    /// traces in five minutes of testing, which is how a signal stops being one. The first failure is the
    /// news and gets the stack trace; every repeat after it is a one-line Warning, because readiness
    /// already carries the state and the diagnosis has not changed. A failure after a success starts over.
    /// </remarks>
    private bool _reportedFailure;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (await TryEstablishAsync(cancellationToken))
        {
            return;
        }

        // **The token is taken here, not inside the lambda, and that is not style.** Reading
        // `_stopping.Token` from the thread-pool thread means reading it after `Dispose` may already have
        // run — `CancellationTokenSource.get_Token()` throws `ObjectDisposedException` once disposed, the
        // task faults, and `StopAsync`'s `await` rethrows it, which is the same shutdown exception by a
        // second route. A `CancellationToken` is a struct and stays readable after its source is gone.
        var stopping = _stopping.Token;

        // Not with `cancellationToken`: that one is the *startup* token and is done with once StartAsync
        // returns. The retry loop lives until StopAsync.
        _retrying = Task.Run(() => RetryUntilEstablishedAsync(stopping), CancellationToken.None);
    }

    /// <summary>
    /// Stops the retry loop and waits for it. Safe in any order relative to <see cref="Dispose"/>, and
    /// safe to call more than once.
    /// </summary>
    /// <remarks>
    /// <b>Nothing orders <see cref="StopAsync"/> and <see cref="Dispose"/>, and assuming otherwise was a
    /// defect.</b> This is a singleton hosted service, so the service provider disposes it — and
    /// <c>WebApplicationFactory.DisposeAsync()</c> disposes the provider and then calls
    /// <c>Host.StopAsync</c>, which arrives at a <see cref="CancellationTokenSource"/> that has already
    /// been disposed. <c>CancelAsync()</c> on a disposed source throws <c>ObjectDisposedException</c>, so
    /// shutdown threw. In CI it took down <c>SizeGuideContentTests</c>, whose own body had passed: a
    /// teardown exception is attributed to whichever host happens to be disposing, which is why the
    /// victim looked arbitrary.
    /// <para>
    /// <b>It is not test-only.</b> Any host that disposes before it stops — and the framework is free to —
    /// would log an exception on a perfectly normal container stop. That is the same shape as the defect
    /// this class was changed to remove in the first place: a healthy operation producing an alarming
    /// error. So both paths are idempotent and order-independent rather than the symptom being guarded.
    /// </para>
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        RequestStop();

        // Taken rather than read, so two concurrent stops do not both await it and the second does not
        // then null a field the first already cleared.
        if (Interlocked.Exchange(ref _retrying, null) is { } retrying)
        {
            // The loop swallows its own cancellation, so this is just "wait for it to notice".
            await retrying;
        }
    }

    /// <summary>
    /// Releases the cancellation source, having first asked the retry loop to stop. Safe before or after
    /// <see cref="StopAsync"/>, and safe to call twice — <c>CancellationTokenSource.Dispose()</c> is
    /// itself idempotent.
    /// </summary>
    /// <remarks>
    /// It cannot wait for the loop, because disposal is synchronous. Cancelling first is what makes that
    /// acceptable: the loop is signalled before the source goes, so it is already unwinding, and a
    /// <see cref="StopAsync"/> arriving afterwards still waits for it through the task.
    /// </remarks>
    public void Dispose()
    {
        RequestStop();
        _stopping.Dispose();
    }

    /// <summary>
    /// Cancels the retry loop if the source is still alive.
    /// </summary>
    /// <remarks>
    /// <b>The catch is the whole point, not a shrug.</b> A disposed source means <see cref="Dispose"/> ran
    /// first, and <see cref="Dispose"/> cancels before it disposes — so the loop has already been told,
    /// and there is genuinely nothing left to do. Testing <c>IsCancellationRequested</c> instead would be
    /// a race: the source can be disposed between the test and the call.
    /// </remarks>
    private void RequestStop()
    {
        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

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

                SeedCounts counts;

                try
                {
                    if (!await TryAcquireSeedLockAsync(dbContext, cancellationToken))
                    {
                        if (attempt >= SeedLockAttempts)
                        {
                            // Out of patience, not in trouble. Readiness stays red because this host is
                            // genuinely not ready, but it says what is actually happening instead of
                            // naming a database problem that does not exist, and the retry timer carries
                            // on from here.
                            state.Failed(
                                "Another instance is still establishing the reference data; this host is "
                                + "waiting for it and will be ready once it finishes.");

                            LogStillWaiting(logger, attempt);

                            // Not reported as a failure, so the next real failure still gets its stack
                            // trace: waiting for another instance is not the thing that log line is for.

                            return false;
                        }

                        LogLockBusy(logger, attempt);
                        await Task.Delay(Jitter(), cancellationToken);
                        continue;
                    }

                    try
                    {
                        counts = await runner.RunReferenceDataAsync(cancellationToken);
                    }
                    finally
                    {
                        await ReleaseSeedLockAsync(dbContext);
                    }

                    // **Established after the lock is released, and before the demo catalogue.** The
                    // reference data is committed, so the shop can take an order; holding a lock while
                    // claiming to be ready was the previous shape, and the demo catalogue is
                    // development-only sample data that readiness has no opinion about.
                    state.Established();
                    _reportedFailure = false;

                    // **Outside the lock, deliberately — see `SeedDemoCatalogueIfAskedAsync`.**
                    await SeedDemoCatalogueIfAskedAsync(runner, counts, cancellationToken);

                    runner.LogCounts(counts);
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

                if (_reportedFailure)
                {
                    LogStillFailing(logger);
                }
                else
                {
                    _reportedFailure = true;
                    LogSeedingFailed(logger, exception);
                }

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
        catch (ObjectDisposedException)
        {
            // The same thing arriving by a different door: `Dispose` ran while this loop was between
            // ticks, so the timer or the token source went out from under it. A faulted task here would
            // be rethrown by `StopAsync` and turn a clean shutdown into an error, which is the defect
            // this method is on the other side of.
        }
    }

    /// <summary>
    /// The sample catalogue, which is development and staging only and still needs both flags. It runs
    /// in the caller's scope and on the caller's counts, so the one "Catalogue seeding inserted …" line
    /// reports both phases together.
    /// </summary>
    /// <remarks>
    /// <b>It runs outside the seed lock, and that is the fix for contention being reachable at all.</b> It
    /// used to run inside, before the release — and it is the expensive phase: 45 products, 480 variants
    /// and 149 media assets held the lock for about eleven seconds on a fresh database <i>with the
    /// placeholder generator switched off</i>, and with <c>Media__PublicRoot</c> configured, as
    /// development and staging have it, <c>DemoMediaPlaceholderGenerator</c> draws 149 images in there
    /// too. That made a second instance's wait — and so the whole contention path — reachable in a normal
    /// development environment rather than only when somebody holds a lock by hand.
    /// <para>
    /// <b>Nothing needs it serialised.</b> The lock exists because <c>catalog.SizeGuides</c> has no unique
    /// index on <c>Name</c>, so two instances could both insert the same reference row; the demo catalogue
    /// is gated behind <c>Seed__RunOnStartup</c> <i>and</i> <c>Seed__IncludeDemoCatalogue</c>, which no
    /// multi-instance deployment sets, and its own seeder is idempotent on keys that are indexed. Two
    /// developers racing it on one database is not a scenario this exists for.
    /// </para>
    /// <para>
    /// <b>Its failure is logged and no more.</b> It is not what readiness is about, and swallowing it here
    /// rather than letting it reach <see cref="TryEstablishAsync"/>'s handler is deliberate: a broken
    /// demo seeder must not make a correctly seeded production host report itself unable to take an order.
    /// </para>
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
    /// Takes the exclusive application lock for the reference-data phase. False when it could not be had,
    /// which the caller treats as contention and retries.
    /// </summary>
    /// <remarks>
    /// <c>sp_getapplock</c> returns 0 (granted) or 1 (granted after waiting) on success, and -1 (timeout),
    /// -2 (cancelled), -3 (deadlock victim) or -999 (bad parameter) otherwise. Only 0 and 1 are a lock.
    /// <para>
    /// <b>The client must not give up before the server does</b>, which is why
    /// <see cref="SeedLockCommandTimeout"/> is set explicitly: a raw <c>DbCommand</c> takes ADO's 30 s
    /// default rather than anything EF is configured with, so a 60 s <c>@LockTimeout</c> was unreachable
    /// and produced an exception instead of the -1 this method is written to read.
    /// </para>
    /// <para>
    /// <b>And a client timeout is still contention, not a failure.</b> If one ever does arrive — a
    /// cancelled command, a server pausing — <c>SqlException.Number == -2</c> means "I gave up waiting",
    /// which is the same answer as -1 and must not be reported as a database that cannot be seeded. The
    /// two are mapped to the same false here rather than left for the broad catch to mislabel.
    /// </para>
    /// </remarks>
    private static async Task<bool> TryAcquireSeedLockAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = "sp_getapplock";
        command.CommandType = System.Data.CommandType.StoredProcedure;
        command.CommandTimeout = (int)SeedLockCommandTimeout.TotalSeconds;

        AddParameter(command, "@Resource", SeedLockResource);
        AddParameter(command, "@LockMode", "Exclusive");
        AddParameter(command, "@LockOwner", "Session");
        AddParameter(command, "@LockTimeout", (int)SeedLockTimeout.TotalMilliseconds);

        var result = command.CreateParameter();
        result.ParameterName = "@Result";
        result.DbType = System.Data.DbType.Int32;
        result.Direction = System.Data.ParameterDirection.ReturnValue;
        command.Parameters.Add(result);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == ClientTimeout)
        {
            return false;
        }

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

    // No exception parameter and no stack trace: the first report carried both, readiness carries the
    // reason, and repeating a diagnosis every ten seconds is how a log stops being read.
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Warning,
        Message = "The reference data still could not be established; retrying. See the first error above "
            + "for the reason, or /health/ready for the current one.")]
    private static partial void LogStillFailing(ILogger logger);

    [LoggerMessage(
        EventId = 1009,
        Level = LogLevel.Information,
        Message = "Another instance has held the reference-data seed lock for {Attempt} waits; leaving it "
            + "to the retry timer. This host is not ready yet and nothing is wrong with the database.")]
    private static partial void LogStillWaiting(ILogger logger, int attempt);

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
