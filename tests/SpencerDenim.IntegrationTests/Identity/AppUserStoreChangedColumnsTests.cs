using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Identity;

/// <summary>
/// Issue <b>#250</b> at its root: Identity saved the <b>whole</b> user row, so a save by a request that had loaded the
/// user a while ago put back every column it had loaded, and undid whatever happened to the account meanwhile. A user
/// the request tracks is now saved with the columns that request changed and no others (<see cref="AppUserStore"/>).
/// <para>
/// Every test here uses two scopes, which are two <c>DbContext</c>s, as two requests are: scope A loads the user, scope
/// B changes the account the way an administrator or the owner would, and scope A then saves through the
/// <c>UserManager</c> call under test. They did not need a sign-in, so they pin the store and not the sign-in. They
/// were red on the whole-row save, and each says which column it brought back.
/// </para>
/// </summary>
public sealed class AppUserStoreChangedColumnsTests(SqlServerDatabaseFixture sqlServer)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_save_after_a_deactivation_made_elsewhere_leaves_the_account_deactivated()
    {
        await using var factory = await StartAsync();
        var userId = await CreateUserAsync(factory);

        await using var requestA = factory.Services.CreateAsyncScope();
        var userManagerA = requestA.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManagerA.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));

        await DeactivateAsync(factory, userId);

        user!.LastLoginAtUtc = DateTime.UtcNow;
        var result = await userManagerA.UpdateAsync(user);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        var persisted = await ReadAsync(factory, userId);
        Assert.False(persisted.IsActive, "The save brought IsActive back to the value the request loaded.");
        Assert.NotNull(persisted.LastLoginAtUtc);
        Assert.NotNull(persisted.UpdatedAtUtc);
    }

    [Fact]
    public async Task A_save_after_a_password_change_made_elsewhere_keeps_the_new_hash_and_stamp()
    {
        await using var factory = await StartAsync();
        var userId = await CreateUserAsync(factory);

        await using var requestA = factory.Services.CreateAsyncScope();
        var userManagerA = requestA.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManagerA.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));

        string? newHash;
        string? newStamp;
        await using (var requestB = factory.Services.CreateAsyncScope())
        {
            var userManagerB = requestB.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var other = await userManagerB.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
            var change = await userManagerB.ChangePasswordAsync(other!, Password, "Another-Password-2!");
            Assert.True(change.Succeeded, string.Join("; ", change.Errors.Select(e => e.Description)));
        }

        var afterChange = await ReadAsync(factory, userId);
        newHash = afterChange.PasswordHash;
        newStamp = afterChange.SecurityStamp;
        Assert.NotEqual(user!.PasswordHash, newHash);

        user.FirstName = "Edited";
        var result = await userManagerA.UpdateAsync(user);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        var persisted = await ReadAsync(factory, userId);
        Assert.Equal("Edited", persisted.FirstName);
        Assert.True(string.Equals(persisted.PasswordHash, newHash, StringComparison.Ordinal), "The save put the old password hash back.");
        Assert.True(string.Equals(persisted.SecurityStamp, newStamp, StringComparison.Ordinal), "The save put the old security stamp back.");
    }

    /// <summary>The path the pending sign-in used: storing a token calls the same user save.</summary>
    [Fact]
    public async Task Storing_a_token_after_a_deactivation_made_elsewhere_leaves_the_account_deactivated()
    {
        await using var factory = await StartAsync();
        var userId = await CreateUserAsync(factory);

        await using var requestA = factory.Services.CreateAsyncScope();
        var userManagerA = requestA.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManagerA.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));

        await DeactivateAsync(factory, userId);

        var result = await userManagerA.SetAuthenticationTokenAsync(user!, "Test", "Token", "value");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        Assert.False((await ReadAsync(factory, userId)).IsActive, "Storing a token brought IsActive back.");
        Assert.Equal("value", await userManagerA.GetAuthenticationTokenAsync(user!, "Test", "Token"));
    }

    /// <summary>The path a second step with a recovery code takes: redeeming a code saves the user.</summary>
    [Fact]
    public async Task Redeeming_a_recovery_code_after_a_deactivation_made_elsewhere_leaves_the_account_deactivated()
    {
        await using var factory = await StartAsync();
        var userId = await CreateUserAsync(factory);

        string code;
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var userManager = setup.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var fresh = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
            var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(fresh!, 3);
            code = codes!.First();
        }

        await using var requestA = factory.Services.CreateAsyncScope();
        var userManagerA = requestA.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManagerA.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));

        await DeactivateAsync(factory, userId);

        var result = await userManagerA.RedeemTwoFactorRecoveryCodeAsync(user!, code);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        Assert.False((await ReadAsync(factory, userId)).IsActive, "Redeeming a code brought IsActive back.");
    }

    /// <summary>The path after a right code or a right password: Identity's own counter reset saves the user.</summary>
    [Fact]
    public async Task Resetting_the_failed_count_after_a_deactivation_made_elsewhere_still_resets_it_and_leaves_the_account_deactivated()
    {
        await using var factory = await StartAsync();
        var userId = await CreateUserAsync(factory);

        await using (var seed = factory.Services.CreateAsyncScope())
        {
            var dbContext = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            await dbContext.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.AccessFailedCount, 3), Ct);
        }

        await using var requestA = factory.Services.CreateAsyncScope();
        var userManagerA = requestA.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManagerA.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(3, user!.AccessFailedCount);

        await DeactivateAsync(factory, userId);

        var result = await userManagerA.ResetAccessFailedCountAsync(user);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        var persisted = await ReadAsync(factory, userId);
        Assert.Equal(0, persisted.AccessFailedCount);
        Assert.False(persisted.IsActive, "The counter reset brought IsActive back.");
    }

    /// <summary>The guarantee #46 gave and this change must not lose: a lockout set elsewhere survives a later save.</summary>
    [Fact]
    public async Task A_save_after_a_lockout_made_elsewhere_keeps_the_lockout()
    {
        await using var factory = await StartAsync();
        var userId = await CreateUserAsync(factory);

        await using var requestA = factory.Services.CreateAsyncScope();
        var userManagerA = requestA.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManagerA.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));

        var lockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
        await using (var requestB = factory.Services.CreateAsyncScope())
        {
            var dbContext = requestB.ServiceProvider.GetRequiredService<AppDbContext>();
            await dbContext.Users.Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, lockedUntil).SetProperty(u => u.AccessFailedCount, 2), Ct);
        }

        user!.LastLoginAtUtc = DateTime.UtcNow;
        Assert.True((await userManagerA.UpdateAsync(user)).Succeeded);

        var persisted = await ReadAsync(factory, userId);
        Assert.NotNull(persisted.LockoutEnd);
        Assert.True(persisted.LockoutEnd > DateTimeOffset.UtcNow, "The save wiped the lockout.");
        Assert.Equal(2, persisted.AccessFailedCount);
    }

    /// <summary>
    /// What "only the changed columns" means in the statement itself: a save that changed one column writes that
    /// column, the concurrency stamp Identity always rewrites, and the audit columns, and nothing of the rest of the row.
    /// </summary>
    [Fact]
    public async Task A_save_of_one_changed_column_writes_that_column_and_the_stamp_and_nothing_else()
    {
        var capture = new CapturedUserUpdates();
        await using var factory = await StartAsync(capture);
        var userId = await CreateUserAsync(factory);

        await using var request = factory.Services.CreateAsyncScope();
        var userManager = request.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));

        capture.Clear();
        user!.LastLoginAtUtc = DateTime.UtcNow;
        Assert.True((await userManager.UpdateAsync(user)).Succeeded);

        var statements = capture.Statements();
        Assert.True(statements.Count == 1, $"Expected one UPDATE of identity.Users, saw {statements.Count}: {string.Join(" | ", statements)}");
        var statement = statements[0];

        foreach (var written in new[] { "[LastLoginAtUtc]", "[ConcurrencyStamp]" })
        {
            Assert.Contains(written, statement, StringComparison.Ordinal);
        }

        foreach (var stale in new[]
        {
            "[IsActive]", "[PasswordHash]", "[SecurityStamp]", "[Email]", "[NormalizedEmail]", "[UserName]", "[FirstName]",
            "[TwoFactorEnabled]", "[AccessFailedCount]", "[LockoutEnd]", "[LockoutEnabled]", "[MustChangePassword]",
        })
        {
            Assert.DoesNotContain(stale, SetClause(statement), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The one case that is still written in full, as Identity always did: a user this context never loaded has no
    /// original values to compare with. Stated so the difference is a decision somebody can read.
    /// <para>
    /// <b>Only reachable by calling the store itself.</b> Through <c>UserManager</c> a user is never detached by the time
    /// it is saved: its validator loads the user first, which tracks a copy, and attaching a second instance with the same
    /// key throws (it did before this change too). So this branch protects no <c>UserManager</c> flow, and this test calls
    /// the store directly for that reason.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_user_this_context_never_loaded_is_still_written_in_full_when_the_store_is_called_directly()
    {
        var capture = new CapturedUserUpdates();
        await using var factory = await StartAsync(capture);
        var userId = await CreateUserAsync(factory);

        AppUser detached;
        await using (var reader = factory.Services.CreateAsyncScope())
        {
            var dbContext = reader.ServiceProvider.GetRequiredService<AppDbContext>();
            detached = await dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == userId, Ct);
        }

        await using var request = factory.Services.CreateAsyncScope();
        var store = Assert.IsType<AppUserStore>(request.ServiceProvider.GetRequiredService<IUserStore<AppUser>>());

        capture.Clear();
        detached.LastName = "Changed";
        Assert.True((await store.UpdateAsync(detached, Ct)).Succeeded);

        var statement = Assert.Single(capture.Statements());
        Assert.Contains("[IsActive]", SetClause(statement), StringComparison.Ordinal);
        Assert.Contains("[PasswordHash]", SetClause(statement), StringComparison.Ordinal);
        Assert.Equal("Changed", (await ReadAsync(factory, userId)).LastName);
    }

    [Fact]
    public async Task Every_save_gives_the_user_a_new_concurrency_stamp_and_an_updated_at()
    {
        await using var factory = await StartAsync();
        var userId = await CreateUserAsync(factory);
        var before = await ReadAsync(factory, userId);

        await using var request = factory.Services.CreateAsyncScope();
        var userManager = request.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
        user!.FirstName = "Edited";
        Assert.True((await userManager.UpdateAsync(user)).Succeeded);

        var after = await ReadAsync(factory, userId);
        Assert.NotEqual(before.ConcurrencyStamp, after.ConcurrencyStamp);
        Assert.NotNull(after.UpdatedAtUtc);
    }

    private const string Password = "Correct-Horse-Battery-1!";

    private async Task<ApiFactory> StartAsync(DbCommandInterceptor? interceptor = null)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Ct);
        return new ApiFactory(sqlServer.ConnectionString, commandInterceptor: interceptor);
    }

    private static async Task<int> CreateUserAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var email = $"store-{Guid.NewGuid():N}@example.com";
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = "Test",
            LastName = "Staff",
            IsActive = true,
        };

        var result = await userManager.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    private static async Task DeactivateAsync(ApiFactory factory, int userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), Ct);
    }

    private static async Task<AppUser> ReadAsync(ApiFactory factory, int userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == userId, Ct);
    }

    /// <summary>The columns an UPDATE sets: everything between SET and the first WHERE.</summary>
    private static string SetClause(string statement)
    {
        var set = statement.IndexOf(" SET ", StringComparison.Ordinal);
        var where = statement.IndexOf("WHERE", set < 0 ? 0 : set, StringComparison.Ordinal);
        return set < 0 || where < 0 ? statement : statement[set..where];
    }

    /// <summary>Keeps the text of every <c>UPDATE [identity].[Users]</c> a test makes.</summary>
    private sealed class CapturedUserUpdates : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _statements = new();

        public void Clear() => _statements.Clear();

        public IReadOnlyList<string> Statements() => [.. _statements];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            if (command.CommandText.Contains("UPDATE [identity].[Users]", StringComparison.Ordinal))
            {
                _statements.Enqueue(command.CommandText);
            }
        }
    }
}
