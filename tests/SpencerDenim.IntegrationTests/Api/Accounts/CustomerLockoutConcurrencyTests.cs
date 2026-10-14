using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity.Customers;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Accounts;

/// <summary>
/// Doc 11 P11 step B.5's lockout counter under real contention, driven at the <b>store</b> rather than
/// over HTTP.
/// </summary>
/// <remarks>
/// <b>This exists because the HTTP version could not be made to fail, which makes it worthless as a
/// guard.</b> <c>CustomerSignInRaceTests</c> fires thirty concurrent sign-ins through
/// <c>WebApplicationFactory</c> behind a gate; replacing <c>CustomerAccountLockout</c>'s atomic
/// <c>SET [AccessFailedCount] = [AccessFailedCount] + 1</c> with a read-then-write — the exact bug #146
/// measured as 60 parallel failures leaving a count of 4 — leaves that test green. Measured, not assumed.
/// The requests in that harness do not overlap enough for a lost update to happen.
/// <para>
/// <b>So the HTTP layer is removed from the question.</b> Each caller gets its own DI scope, therefore
/// its own <c>AppDbContext</c> and its own connection, and runs on the thread pool behind a gate whose
/// continuations are asynchronous. This is the same shape as
/// <c>CustomerAddressConcurrencyTests</c>, which demonstrably does produce contention (the same
/// code takes 4 s with its application lock and 15–17 s without it).
/// </para>
/// <para>
/// <b>What it asserts is the invariant, not a count of replies.</b> Under contention the split between
/// "counted" and "tripped the lock" is genuinely non-deterministic; what must hold is that the account
/// ends up locked, and that exactly one caller is told it was the one that locked it — because that one
/// writes the single <c>Lockout</c> audit row.
/// </para>
/// </remarks>
public sealed class CustomerLockoutConcurrencyTests(SqlServerDatabaseFixture sqlServer)
    : CustomerAccountTestBase(sqlServer)
{
    /// <summary>
    /// Failures released together. Well above the threshold below, so a counter that loses even a few
    /// increments still has to lose a lot of them to leave the account open — which is what a
    /// read-then-write does.
    /// </summary>
    private const int Failures = 24;

    /// <summary>The threshold, configured low so the test is about the mechanism and not the number.</summary>
    private const int Threshold = 5;

    [Fact]
    public async Task Parallel_failures_cannot_outrun_the_counter()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:LockoutMaxFailedAttempts"] = Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CustomerSecurity:LockoutMinutes"] = "15",
        });

        var userId = await CreateCustomerAsync(factory);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, Failures).Select(_ => Task.Run(async () =>
        {
            // A scope each: its own AppDbContext and its own connection. A shared context would
            // serialise these by itself and the test would measure nothing.
            await using var scope = factory.Services.CreateAsyncScope();
            var lockout = scope.ServiceProvider.GetRequiredService<CustomerAccountLockout>();

            await gate.Task;

            return await lockout.RecordFailureAsync(userId, Token);
        })).ToList();

        gate.SetResult();

        var outcomes = await Task.WhenAll(attempts);

        // **Exactly one caller tripped it.** This is what the single `Lockout` audit row depends on: an
        // implementation that reported the lockout to every attempt that saw one would write twenty-four
        // rows for one event.
        Assert.Equal(1, outcomes.Count(outcome => outcome.TrippedByThisAttempt));

        // And the one that tripped it is the one that knows when it ends.
        var tripped = outcomes.Single(outcome => outcome.TrippedByThisAttempt);
        Assert.NotNull(tripped.LockoutEndUtc);
        Assert.True(tripped.LockoutEndUtc > DateTime.UtcNow);

        // **The invariant: the account is locked.** A read-modify-write counter loses increments under
        // this load and leaves it open.
        var customer = await ReadCustomerByIdAsync(factory, userId);
        Assert.NotNull(customer);
        Assert.NotNull(customer.LockoutEnd);
        Assert.True(
            customer.LockoutEnd > DateTimeOffset.UtcNow,
            $"{Failures} concurrent failures against a threshold of {Threshold} left the account "
            + "unlocked: the failure counter lost increments, which is #146's finding on the staff side.");
    }

    /// <summary>
    /// Doc 08 §3.1 DB10's "it expires by itself": a second storm against the locked account does not
    /// move the end time.
    /// </summary>
    [Fact]
    public async Task Parallel_failures_against_a_locked_account_do_not_move_the_end_time()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:LockoutMaxFailedAttempts"] = "3",
            ["CustomerSecurity:LockoutMinutes"] = "15",
        });

        var userId = await CreateCustomerAsync(factory);

        await StormAsync(factory, userId, 8);

        var locked = await ReadCustomerByIdAsync(factory, userId);
        Assert.NotNull(locked);
        Assert.NotNull(locked.LockoutEnd);

        await StormAsync(factory, userId, 16);

        var after = await ReadCustomerByIdAsync(factory, userId);
        Assert.NotNull(after);
        Assert.Equal(locked.LockoutEnd, after.LockoutEnd);
    }

    /// <summary>
    /// An account with <c>LockoutEnabled = 0</c> is never reported as locked.
    /// </summary>
    /// <remarks>
    /// <b>#146's round-1 tester caught the staff version reporting a lockout that did not exist.</b> The
    /// customer side has no screen that switches this off today, so the state is reachable only by a
    /// direct write — which is exactly why it is worth pinning: a future "do not lock this account"
    /// setting must not inherit a bug.
    /// </remarks>
    [Fact]
    public async Task An_account_exempt_from_lockout_is_never_reported_as_locked()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:LockoutMaxFailedAttempts"] = "2",
        });

        var userId = await CreateCustomerAsync(factory, lockoutEnabled: false);

        var outcomes = await StormAsync(factory, userId, 8);

        Assert.All(outcomes, outcome => Assert.False(outcome.IsLockedOut));
        Assert.All(outcomes, outcome => Assert.Null(outcome.LockoutEndUtc));
        Assert.DoesNotContain(outcomes, outcome => outcome.TrippedByThisAttempt);

        var customer = await ReadCustomerByIdAsync(factory, userId);
        Assert.NotNull(customer);
        Assert.Null(customer.LockoutEnd);
    }

    private static async Task<CustomerAccountLockout.FailureOutcome[]> StormAsync(
        ApiFactory factory,
        int userId,
        int failures)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, failures).Select(_ => Task.Run(async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var lockout = scope.ServiceProvider.GetRequiredService<CustomerAccountLockout>();

            await gate.Task;

            return await lockout.RecordFailureAsync(userId, Token);
        })).ToList();

        gate.SetResult();

        return await Task.WhenAll(attempts);
    }

    private static async Task<int> CreateCustomerAsync(ApiFactory factory, bool lockoutEnabled = true)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<SpencerDenim.Infrastructure.Identity.AppUser>>();

        var email = UniqueEmail("lockout-race");

        var user = new SpencerDenim.Infrastructure.Identity.AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Customer,
            FirstName = "Nimal",
            IsActive = true,
            LockoutEnabled = lockoutEnabled,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var result = await userManager.CreateAsync(user, GoodPassword);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));

        if (!lockoutEnabled)
        {
            // **`UserManager.CreateAsync` overrides the flag, and that caught this test out.**
            // `IdentityOptions.Lockout.AllowedForNewUsers` is true (`IdentityRegistration`), and
            // `CreateAsync` therefore calls `SetLockoutEnabledAsync(user, true)` on every new user
            // whatever the instance said. The first version of this test set the property on the object
            // and was handed an account that was not exempt at all, then failed because it *was* locked
            // — which was the product behaving correctly and the test being wrong. Set after the create,
            // by the one column.
            var dbContext = scope.ServiceProvider
                .GetRequiredService<SpencerDenim.Infrastructure.Persistence.AppDbContext>();

            await dbContext.Database.ExecuteSqlAsync(
                $"UPDATE [identity].[Users] SET [LockoutEnabled] = 0 WHERE [Id] = {user.Id}",
                Token);
        }

        return user.Id;
    }

    private static async Task<CustomerRow?> ReadCustomerByIdAsync(ApiFactory factory, int userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<SpencerDenim.Infrastructure.Persistence.AppDbContext>();

        return await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new CustomerRow(
                user.Id,
                user.UserType,
                user.EmailConfirmed,
                user.IsActive,
                user.AccessFailedCount,
                user.LockoutEnd))
            .FirstOrDefaultAsync(Token);
    }
}
