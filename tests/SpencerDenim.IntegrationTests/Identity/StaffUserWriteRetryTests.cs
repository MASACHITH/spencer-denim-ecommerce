using System.Data.Common;
using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Identity;

/// <summary>
/// Every staff-user write must survive a transient SQL failure, because <c>EnableRetryOnFailure</c> is on
/// (<c>Infrastructure/DependencyInjection.cs</c>) and Azure SQL is the production target (doc 08 §10.5), where
/// transient failures are routine rather than exceptional.
/// <para>
/// <b>Why these tests exist (tester round 3, Major 1).</b> Each write wraps its transaction in
/// <c>CreateExecutionStrategy().ExecuteAsync</c> and the code said that retried the whole block. It did not: the
/// retry re-ran against a change tracker still holding the failed attempt's entities, so a single blip turned a
/// role change into <c>InvalidOperationException: User already in role</c> and a profile edit into
/// <c>Cannot insert explicit value for identity column</c> — an HTTP 500 either way. The second of those was
/// introduced by passing one <see cref="AuditLog"/> instance into the store, which came back carrying the
/// identity value EF had assigned it. Both are fixed by clearing the tracker and rebuilding per attempt, and the
/// audit row now comes from a factory.
/// </para>
/// <para>
/// <b>Two injection points, because one of them cannot see half the defect.</b>
/// <see cref="AuditInsertFailureInterceptor"/> throws from <c>ReaderExecuting</c>/<c>NonQueryExecuting</c>, so the
/// failure lands <i>before</i> the audit INSERT reaches SQL Server: the most state the transaction will ever hold,
/// and the point that proves the change tracker is cleared per attempt. But EF never reads an identity value back
/// on that attempt, so <c>AuditLog.Id</c> stays <c>0</c> and re-enlisting one shared instance is harmless — a test
/// injecting there cannot fail on the identity-value half of the defect, and with the factory reverted it does not.
/// <see cref="AuditCommitFailureInterceptor"/> exists for exactly that half: it throws at transaction
/// <i>commit</i>, after <c>SaveChangesAsync</c> has returned and written the generated key onto the entity, which
/// is the only moment from which a retry can carry a populated <c>AuditLog.Id</c> into a second INSERT.
/// </para>
/// </summary>
public sealed class StaffUserWriteRetryTests(SqlServerDatabaseFixture sqlServer)
{
    private static readonly RbacActorContext GrantsAllActor =
        new(HasGrantsAll: true, HasUsersManage: true, EffectivePermissions: new HashSet<string>());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_role_change_survives_one_transient_failure_and_leaves_exactly_one_audit_row()
    {
        var world = await ArrangeAsync("retry-roles");
        var marker = $"retry-roles-{Guid.NewGuid():N}";

        var result = await world.RunAsync((store, _) =>
            store.ReplaceRolesAsync(world.TargetId, [world.WideRoleId], GrantsAllActor, () => Audit(marker, world.TargetId), Token));

        Assert.IsType<ReplaceRolesStoreResult.Success>(result);
        Assert.Equal(1, world.Injector.Fired);

        await world.AssertRolesAsync(world.WideRoleId);
        Assert.Equal(1, await world.CountAuditRowsAsync(marker));
    }

    [Fact]
    public async Task A_profile_edit_survives_one_transient_failure_and_leaves_exactly_one_audit_row()
    {
        var world = await ArrangeAsync("retry-profile");
        var marker = $"retry-profile-{Guid.NewGuid():N}";

        var result = await world.RunAsync((store, _) =>
            store.UpdateProfileAsync(world.TargetId, "Renamed", "Target", newEmail: null, GrantsAllActor, () => Audit(marker, world.TargetId), Token));

        Assert.IsType<UpdateProfileStoreResult.Success>(result);
        Assert.Equal(1, world.Injector.Fired);

        await using var scope = world.Factory.Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking()
            .SingleAsync(u => u.Id == world.TargetId, Token);
        Assert.Equal("Renamed", user.FirstName);
        Assert.Equal(1, await world.CountAuditRowsAsync(marker));
    }

    /// <summary>
    /// The <c>Func&lt;AuditLog&gt;</c> half of tester round 3's Major 1 (review round 4, item 3). The transient
    /// arrives at <c>CommitAsync</c>, so the audit INSERT has already run and EF has written the identity value it
    /// read back onto the enlisted <see cref="AuditLog"/>. Hoist that row out of the retried block — one instance
    /// for every attempt, which is what this branch's first <c>Enlist</c> conversion did — and the retry re-adds an
    /// entity whose key is already set, which SQL Server refuses with
    /// <c>Cannot insert explicit value for identity column in table 'AuditLogs' when IDENTITY_INSERT is set to
    /// OFF</c>: the exact symptom reported for this defect. Taking the row from a factory is what makes this pass,
    /// and nothing else here does — <c>ChangeTracker.Clear()</c> cannot help, because detaching the instance does
    /// not clear the key EF put on it.
    /// </summary>
    [Fact]
    public async Task A_profile_edit_survives_one_transient_failure_at_commit_and_leaves_exactly_one_audit_row()
    {
        var world = await ArrangeAsync("retry-commit", injectFailure: false);
        var marker = $"retry-commit-{Guid.NewGuid():N}";

        // Armed only now: unlike the command injector, this one cannot filter by SQL text, so arming it before
        // the arrangement above would fire on whichever of its saves opened a transaction first.
        world.CommitInjector.Arm();

        var result = await world.RunAsync((store, _) =>
            store.UpdateProfileAsync(world.TargetId, "Renamed", "Target", newEmail: null, GrantsAllActor, () => Audit(marker, world.TargetId), Token));

        Assert.IsType<UpdateProfileStoreResult.Success>(result);
        Assert.Equal(1, world.CommitInjector.Fired);

        await using var scope = world.Factory.Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking()
            .SingleAsync(u => u.Id == world.TargetId, Token);
        Assert.Equal("Renamed", user.FirstName);

        // One row, not two: the first attempt's INSERT was rolled back with its transaction.
        Assert.Equal(1, await world.CountAuditRowsAsync(marker));
    }

    [Fact]
    public async Task A_deactivation_survives_one_transient_failure_and_leaves_exactly_one_audit_row()
    {
        var world = await ArrangeAsync("retry-deactivate");
        var marker = $"retry-deactivate-{Guid.NewGuid():N}";

        var result = await world.RunAsync((store, _) =>
            store.SetActiveAsync(world.TargetId, active: false, GrantsAllActor, () => Audit(marker, world.TargetId), Token));

        Assert.IsType<SetActiveStoreResult.Success>(result);
        Assert.Equal(1, world.Injector.Fired);

        await using var scope = world.Factory.Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking()
            .SingleAsync(u => u.Id == world.TargetId, Token);
        Assert.False(user.IsActive);
        Assert.Equal(1, await world.CountAuditRowsAsync(marker));
    }

    [Fact]
    public async Task Creating_a_user_survives_one_transient_failure_and_inserts_the_user_exactly_once()
    {
        var world = await ArrangeAsync("retry-create");
        var marker = $"retry-create-{Guid.NewGuid():N}";
        var email = $"retry-created-{Guid.NewGuid():N}@example.com";

        var result = await world.RunAsync((store, _) =>
            store.CreateStaffUserAsync(
                new NewStaffUserData(email, "Retry", "Created", "Correct-Password-1!", [world.WideRoleId]),
                newUserId => Audit(marker, newUserId),
                Token));

        Assert.IsType<CreateStaffUserStoreResult.Success>(result);
        Assert.Equal(1, world.Injector.Fired);

        await using var scope = world.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await dbContext.Users.AsNoTracking().CountAsync(u => u.Email == email, Token));
        Assert.Equal(1, await world.CountAuditRowsAsync(marker));
    }

    /// <summary>
    /// The control: with nothing injected the same call makes no retry at all, so a pass above cannot come from
    /// the injector never having fired. Without this, an injector that silently matched nothing would make every
    /// test here green for the wrong reason.
    /// </summary>
    [Fact]
    public async Task With_no_transient_failure_injected_nothing_retries()
    {
        var world = await ArrangeAsync("retry-control", injectFailure: false);
        var marker = $"retry-control-{Guid.NewGuid():N}";

        var result = await world.RunAsync((store, _) =>
            store.ReplaceRolesAsync(world.TargetId, [world.WideRoleId], GrantsAllActor, () => Audit(marker, world.TargetId), Token));

        Assert.IsType<ReplaceRolesStoreResult.Success>(result);
        Assert.Equal(0, world.Injector.Fired);
        Assert.Equal(1, await world.CountAuditRowsAsync(marker));
    }

    private static AuditLog Audit(string marker, int targetUserId) => new()
    {
        OccurredAtUtc = DateTime.UtcNow,
        Action = AuditActions.PermissionChange,
        EntityType = "User",
        EntityId = targetUserId.ToString(CultureInfo.InvariantCulture),
        UserAgent = marker,
    };

    private async Task<RetryWorld> ArrangeAsync(string label, bool injectFailure = true)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        var injector = new AuditInsertFailureInterceptor(armed: injectFailure);
        var commitInjector = new AuditCommitFailureInterceptor();
        var factory = new ApiFactory(
            sqlServer.ConnectionString,
            configureServices: services =>
            {
                services.AddSingleton<IInterceptor>(injector);
                services.AddSingleton<IInterceptor>(commitInjector);
            });

        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();

        var narrowRole = new AppRole { Name = $"narrow-{label}-{Guid.NewGuid():N}", GrantsAllPermissions = false, IsSystem = false };
        Assert.True((await roleManager.CreateAsync(narrowRole)).Succeeded);
        var wideRole = new AppRole { Name = $"wide-{label}-{Guid.NewGuid():N}", GrantsAllPermissions = false, IsSystem = false };
        Assert.True((await roleManager.CreateAsync(wideRole)).Succeeded);

        var email = $"{label}-{Guid.NewGuid():N}@example.com";
        var target = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = "Test",
            LastName = "Staff",
            IsActive = true,
        };
        Assert.True((await userManager.CreateAsync(target, "Correct-Password-1!")).Succeeded);
        Assert.True((await userManager.AddToRoleAsync(target, narrowRole.Name!)).Succeeded);

        return new RetryWorld(factory, injector, commitInjector, target.Id, wideRole.Id, wideRole.Name!);
    }

    private sealed record RetryWorld(
        ApiFactory Factory,
        AuditInsertFailureInterceptor Injector,
        AuditCommitFailureInterceptor CommitInjector,
        int TargetId,
        int WideRoleId,
        string WideRoleName)
    {
        /// <summary>Runs the write in its own scope, exactly as one HTTP request would, then arms nothing further.</summary>
        public async Task<T> RunAsync<T>(Func<IStaffUserRoleManagementStore, AppDbContext, Task<T>> write)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await write(
                scope.ServiceProvider.GetRequiredService<IStaffUserRoleManagementStore>(),
                scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        public async Task AssertRolesAsync(params int[] expectedRoleIds)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var actual = await dbContext.UserRoles.AsNoTracking()
                .Where(ur => ur.UserId == TargetId)
                .Select(ur => ur.RoleId)
                .ToListAsync(Token);
            Assert.Equal(expectedRoleIds.Order(), actual.Order());
        }

        public async Task<int> CountAuditRowsAsync(string marker)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs.AsNoTracking()
                .CountAsync(a => a.UserAgent == marker, Token);
        }
    }

    /// <summary>
    /// Throws a <see cref="TimeoutException"/> — which EF's SQL Server transient detector retries — the first time
    /// a command inserts into <c>ops.AuditLogs</c>, then never again. Registered as an <c>IInterceptor</c>, which
    /// the existing DI already picks up.
    /// </summary>
    private sealed class AuditInsertFailureInterceptor(bool armed) : DbCommandInterceptor
    {
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            FailOnce(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            FailOnce(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            FailOnce(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            FailOnce(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void FailOnce(DbCommand command)
        {
            if (!armed || !command.CommandText.Contains("AuditLogs", StringComparison.Ordinal))
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                throw new TimeoutException("Injected transient failure on the audit insert (tester round 3, Major 1).");
            }
        }
    }

    /// <summary>
    /// Throws a <see cref="TimeoutException"/> — which EF's SQL Server transient detector retries, exactly as it
    /// does for <see cref="AuditInsertFailureInterceptor"/> — the first time a transaction tries to commit, then
    /// never again. A commit timeout is one of the plainest Azure SQL transients there is (doc 08 §10.5).
    /// <para>
    /// <b>Why this exists alongside the command injector</b> (review round 4, item 3). A failure injected before
    /// the audit INSERT leaves <c>AuditLog.Id</c> at <c>0</c>, so re-enlisting one shared row on the retry inserts
    /// it as if it were new and nothing goes wrong: that injection point simply cannot observe the identity-value
    /// defect, which is why the suite stayed green when the <c>Func&lt;AuditLog&gt;</c> fix was reverted under it.
    /// By the time <c>CommitAsync</c> runs, <c>SaveChangesAsync</c> has returned and EF has written the key the
    /// database generated onto the tracked entity, so a retried attempt that reuses that instance carries the key
    /// into its own INSERT. The two injectors therefore test different things and neither replaces the other: the
    /// command one proves the change tracker is cleared per attempt, this one proves the audit row is rebuilt.
    /// </para>
    /// <para>
    /// It cannot filter by SQL text the way the command injector does, so it stays disarmed until
    /// <see cref="Arm"/> is called — otherwise it would fire on the arrangement's own saves.
    /// </para>
    /// </summary>
    private sealed class AuditCommitFailureInterceptor : DbTransactionInterceptor
    {
        private int _armed;
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public override InterceptionResult TransactionCommitting(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result)
        {
            FailOnce();
            return base.TransactionCommitting(transaction, eventData, result);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            FailOnce();
            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }

        private void FailOnce()
        {
            if (Volatile.Read(ref _armed) == 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                throw new TimeoutException("Injected transient failure at transaction commit (review round 4, item 3).");
            }
        }
    }
}
