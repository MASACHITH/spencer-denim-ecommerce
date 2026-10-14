using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Accounts.Abstractions;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Domain.Identity;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Accounts;

/// <summary>
/// Doc 08 §3.1's "App cap: 10 addresses per customer", driven at the <b>store</b> rather than over HTTP.
/// </summary>
/// <remarks>
/// <b>This exists because the HTTP version of the same test could not be made to fail.</b> Eight
/// concurrent tabs, a <see cref="TaskCompletionSource"/> gate with
/// <c>RunContinuationsAsynchronously</c>, <c>Task.Run</c> per tab and a 150 ms delay injected into the
/// address <c>COUNT</c> all still produced one 201 and seven 409s with
/// <c>sp_getapplock</c> <i>removed</i> from the store — so the requests were not overlapping and the
/// test was proving nothing about the lock. That is the shape of defect this project has shipped
/// thirteen times, and the brief's instruction is to prove every test fails by mutation.
/// <para>
/// <b>So the HTTP layer is taken out of the question.</b> Each caller gets its own DI scope — therefore
/// its own <c>AppDbContext</c> and its own connection — and runs on the thread pool behind a gate. If the
/// cap still holds with the lock removed, then the lock is not what makes it hold and that is worth
/// knowing; if it does not, the lock is load-bearing and this is the test that says so. Either way the
/// answer is measured rather than assumed.
/// </para>
/// </remarks>
public sealed class CustomerAddressConcurrencyTests(SqlServerDatabaseFixture sqlServer)
    : CustomerAccountTestBase(sqlServer)
{
    /// <summary>How many callers race for the last slot.</summary>
    private const int Racers = 8;

    [Fact]
    public async Task Eight_concurrent_callers_cannot_exceed_the_cap()
    {
        await ReadyAsync();

        await using var factory = CreateHost(
            commandInterceptor: new DelaysTheAddressCount(TimeSpan.FromMilliseconds(250)));

        var userId = await CreateConfirmedCustomerAsync(factory);

        // Nine addresses, so one slot is left. Written straight to the table: the point of this test is
        // the tenth insert, and nine round trips through the store would only pay the delay nine times.
        for (var index = 0; index < CustomerAddress.MaxPerCustomer - 1; index++)
        {
            DbContext.CustomerAddresses.Add(new CustomerAddress
            {
                UserId = userId,
                RecipientName = "Nimal Perera",
                Phone = "+94771234567",
                Line1 = "221 Galle Road",
                City = "Colombo",
                DistrictId = 1,
                IsDefault = index == 0,
            });
        }

        await DbContext.SaveChangesAsync(Token);
        DbContext.ChangeTracker.Clear();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, Racers).Select(_ => Task.Run(async () =>
        {
            // A scope each: its own AppDbContext, its own connection. A shared context would serialise
            // these by itself and the test would measure nothing, which is the trap the HTTP version
            // fell into.
            await using var scope = factory.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ICustomerAddressStore>();

            await gate.Task;

            try
            {
                return await store.AddAsync(
                    userId,
                    new CustomerAddressEdit(
                        "Race",
                        "Nimal Perera",
                        "+94771234567",
                        "221 Galle Road",
                        null,
                        "Colombo",
                        1,
                        null,
                        null,
                        IsDefault: false),
                    Token);
            }
            catch (DbUpdateException)
            {
                // A loser that the database refused rather than the cap. Counted as a refusal: what the
                // cap promises is that no eleventh row exists, not which layer said no.
                return new CustomerAddressResult.NotFound();
            }
        })).ToList();

        gate.SetResult();

        var results = await Task.WhenAll(attempts);

        var saved = results.Count(result => result is CustomerAddressResult.Saved);
        var refused = results.Length - saved;

        // **The invariant, and it is the row count rather than the replies.** However the losers were
        // refused, the table may not hold an eleventh address.
        DbContext.ChangeTracker.Clear();

        var rows = await DbContext.CustomerAddresses
            .AsNoTracking()
            .CountAsync(address => address.UserId == userId, Token);

        Assert.Equal(CustomerAddress.MaxPerCustomer, rows);

        // And exactly one caller may believe it saved one, because each of the others has to tell its
        // shopper something true.
        Assert.Equal(1, saved);
        Assert.Equal(Racers - 1, refused);
    }

    /// <summary>A confirmed customer, created directly, since this test is not about registration.</summary>
    private static async Task<int> CreateConfirmedCustomerAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<SpencerDenim.Infrastructure.Identity.AppUser>>();

        var email = UniqueEmail("address-store-race");

        var user = new SpencerDenim.Infrastructure.Identity.AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Customer,
            FirstName = "Nimal",
            LastName = "Perera",
            IsActive = true,
            LockoutEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var result = await userManager.CreateAsync(user, GoodPassword);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));

        return user.Id;
    }

    /// <summary>
    /// Delays the address-book <c>COUNT</c>, so the window between it and the insert is wide enough for
    /// a concurrent caller to land in.
    /// </summary>
    /// <remarks>
    /// #146's technique — its parallel tests ran "with delayed token writes to force the worst case".
    /// The delay is on the <c>SELECT</c> only, so the insert, the transaction and the lock behave exactly
    /// as they do in production; what changes is only how likely the race is to be observed.
    /// </remarks>
    private sealed class DelaysTheAddressCount(TimeSpan delay) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal)
                && command.CommandText.Contains("CustomerAddresses", StringComparison.Ordinal))
            {
                await Task.Delay(delay, cancellationToken);
            }

            return result;
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);

            // EF may issue a COUNT as a scalar rather than through a reader depending on the shape of
            // the query, so both hooks are here. Covering only one is how a delay that was supposed to
            // widen a window turns out never to have fired.
            if (command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal)
                && command.CommandText.Contains("CustomerAddresses", StringComparison.Ordinal))
            {
                await Task.Delay(delay, cancellationToken);
            }

            return result;
        }
    }
}
