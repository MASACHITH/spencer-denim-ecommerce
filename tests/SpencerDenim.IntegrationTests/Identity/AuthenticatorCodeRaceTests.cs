using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Identity;

/// <summary>
/// #48: an authenticator code is accepted once only <i>even when the same code arrives on many requests at
/// once</i>. <see cref="ReplayProtectedAuthenticatorTokenProvider"/> used to read the last accepted time step and
/// write the new one as two separate round trips, so every request that read before the first one wrote saw the
/// old step and accepted the code too. These tests call the provider the way a request does — one DI scope, one
/// <see cref="AppDbContext"/> and one <see cref="UserManager{TUser}"/> each — and release every caller at the same
/// instant, against a real SQL Server.
/// <para>
/// <b>Why below the HTTP layer as well as through it.</b> Through the API each request makes several round trips
/// before it reaches the code, which spreads twenty requests out enough that they may never meet at the watermark
/// (<c>AdminTwoFactorEndpointTests.Concurrent_second_steps_with_one_code_open_exactly_one_session</c> passed against
/// the unfixed provider on a loaded server). Here the callers are released at the provider itself, and against the
/// unfixed provider all three tests failed: six of twenty callers accepted one code over an existing row, and over
/// no row the losing inserts surfaced as an unhandled primary-key violation — a 500 on an account's first sign-in.
/// </para>
/// <para>
/// <b>Two tests, because the watermark has two shapes.</b> The first accepted code for an account has no row to
/// update and must insert one, and two inserts race differently from two updates: the losing insert finds the row
/// already there instead of a value that no longer matches.
/// </para>
/// </summary>
public sealed class AuthenticatorCodeRaceTests(SqlServerDatabaseFixture sqlServer)
{
    /// <summary>Callers released together. Enough to interleave reliably; the pool covers them comfortably.</summary>
    private const int Callers = 20;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_code_an_account_ever_uses_is_accepted_by_one_concurrent_caller_only()
    {
        await using var factory = await StartAsync();
        var (userId, key) = await CreateUserWithAuthenticatorKeyAsync(factory, "totp-race-first");

        // No watermark row exists yet: every caller that matches the code has to create it.
        Assert.Null(await ReadWatermarkAsync(factory, userId));

        var accepted = await VerifyConcurrentlyAsync(factory, userId, TotpTestCodes.Current(key));

        Assert.Equal(1, accepted);
        Assert.NotNull(await ReadWatermarkAsync(factory, userId));
    }

    [Fact]
    public async Task A_later_code_is_accepted_by_one_concurrent_caller_only()
    {
        await using var factory = await StartAsync();
        var (userId, key) = await CreateUserWithAuthenticatorKeyAsync(factory, "totp-race-later");

        // An accepted code first, so the race below is over an existing row: the conditional UPDATE path.
        Assert.Equal(1, await VerifyConcurrentlyAsync(factory, userId, TotpTestCodes.Current(key), callers: 1));
        var before = await ReadWatermarkAsync(factory, userId);
        Assert.NotNull(before);

        var accepted = await VerifyConcurrentlyAsync(factory, userId, TotpTestCodes.Next(key, 1));

        Assert.Equal(1, accepted);
        Assert.NotEqual(before, await ReadWatermarkAsync(factory, userId));
    }

    /// <summary>
    /// A loser of the race is refused because its step is no longer later than the newest accepted one — not
    /// because the provider gives up after any collision. A different, later code from the same account still
    /// signs in afterwards, sequentially, exactly as it would have without the race.
    /// </summary>
    [Fact]
    public async Task After_a_race_the_next_step_is_still_accepted()
    {
        await using var factory = await StartAsync();
        var (userId, key) = await CreateUserWithAuthenticatorKeyAsync(factory, "totp-race-next");

        Assert.Equal(1, await VerifyConcurrentlyAsync(factory, userId, TotpTestCodes.Current(key)));
        Assert.Equal(1, await VerifyConcurrentlyAsync(factory, userId, TotpTestCodes.Next(key, 1), callers: 1));
    }

    private async Task<ApiFactory> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);
        return new ApiFactory(sqlServer.ConnectionString);
    }

    private static async Task<(int UserId, string Key)> CreateUserWithAuthenticatorKeyAsync(ApiFactory factory, string label)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var email = $"{label}-{Guid.NewGuid():N}@example.com";
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = "Race",
            LastName = "Test",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var created = await userManager.CreateAsync(user, "Correct-Horse-Battery-1!");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        var reset = await userManager.ResetAuthenticatorKeyAsync(user);
        Assert.True(reset.Succeeded, string.Join("; ", reset.Errors.Select(e => e.Description)));

        var key = await userManager.GetAuthenticatorKeyAsync(user);
        Assert.False(string.IsNullOrWhiteSpace(key));
        return (user.Id, key!);
    }

    /// <summary>
    /// Every caller loads the user in its own scope first, then waits at a gate, so the verifications themselves
    /// start together rather than staggered by however long a scope and a user lookup take.
    /// </summary>
    private static async Task<int> VerifyConcurrentlyAsync(ApiFactory factory, int userId, string code, int callers = Callers)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new int[1];
        var allReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture))
                ?? throw new InvalidOperationException("The test user is missing.");

            if (Interlocked.Increment(ref ready[0]) == callers)
            {
                allReady.SetResult();
            }

            await gate.Task;
            return await userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code);
        }, Token)).ToArray();

        await allReady.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        gate.SetResult();

        var results = await Task.WhenAll(attempts);
        return results.Count(accepted => accepted);
    }

    private static async Task<string?> ReadWatermarkAsync(ApiFactory factory, int userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.Set<IdentityUserToken<int>>()
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.LoginProvider == AdminTokenNames.LoginProvider && t.Name == AdminTokenNames.LastAuthenticatorStep)
            .Select(t => t.Value)
            .SingleOrDefaultAsync(Token);
    }
}
