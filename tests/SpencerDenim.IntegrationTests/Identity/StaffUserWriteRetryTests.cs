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
/// The failure is injected at the <i>audit insert</i> — the last write inside each transaction — because that is
/// the latest possible point, where the most state has accumulated and a retry has the most to get wrong.
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
        var factory = new ApiFactory(
            sqlServer.ConnectionString,
            configureServices: services => services.AddSingleton<IInterceptor>(injector));

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

        return new RetryWorld(factory, injector, target.Id, wideRole.Id, wideRole.Name!);
    }

    private sealed record RetryWorld(ApiFactory Factory, AuditInsertFailureInterceptor Injector, int TargetId, int WideRoleId, string WideRoleName)
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
}
