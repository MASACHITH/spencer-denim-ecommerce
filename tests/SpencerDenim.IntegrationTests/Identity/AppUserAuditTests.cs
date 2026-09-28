using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Identity;

/// <summary>
/// Regression tests for issue #18: <c>identity.Users</c> is tagged <b>[A]</b> in doc 08 §1.5/§3.1, but
/// <see cref="AppUser"/> implemented neither <see cref="SpencerDenim.Domain.Common.IAuditable"/> nor
/// <see cref="SpencerDenim.Domain.Common.ISoftDeletable"/>, so <c>AuditAndSoftDeleteInterceptor</c> skipped it
/// entirely — <c>UpdatedAtUtc</c> stayed <c>NULL</c> forever, no matter how many times a user row changed.
/// <para>
/// Doc 08 §3.1 does not tag <c>identity.Roles</c> [A] (no <c>[A]</c> on that table's own heading, and its column
/// list has no <c>*ByUserId</c> columns), so <see cref="AppRole"/> is deliberately left alone here — see the
/// PR/issue for that reading.
/// </para>
/// </summary>
public sealed class AppUserAuditTests(SqlServerDatabaseFixture sqlServer)
{
    [Fact]
    public async Task Creating_a_user_stamps_CreatedAtUtc_and_leaves_UpdatedAtUtc_null()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var beforeCreate = DateTime.UtcNow;
        var user = NewStaffUser();
        var result = await userManager.CreateAsync(user, "Correct-Password-1!");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        Assert.True(user.CreatedAtUtc >= beforeCreate.AddSeconds(-1), "CreatedAtUtc should be stamped by the interceptor, not left at default.");
        Assert.Null(user.UpdatedAtUtc);
    }

    /// <summary>
    /// Before issue #18's fix, this is exactly the defect: <c>UpdatedAtUtc</c> stayed <c>NULL</c> after this
    /// update, because <c>AppUser</c> wasn't <c>IAuditable</c> and the interceptor skipped it entirely.
    /// </summary>
    [Fact]
    public async Task Updating_a_user_stamps_UpdatedAtUtc_without_moving_CreatedAtUtc()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = NewStaffUser();
        var createResult = await userManager.CreateAsync(user, "Correct-Password-1!");
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));
        var createdAtUtc = user.CreatedAtUtc;

        user.FirstName = "Changed";
        var updateResult = await userManager.UpdateAsync(user);
        Assert.True(updateResult.Succeeded, string.Join("; ", updateResult.Errors.Select(e => e.Description)));

        Assert.NotNull(user.UpdatedAtUtc);
        Assert.True(user.UpdatedAtUtc >= createdAtUtc);
        Assert.Equal(createdAtUtc, user.CreatedAtUtc);

        // Reload from the database (not the tracked in-memory instance) so the assertion proves the value was
        // actually persisted, not just set on the entity by the interceptor and never saved. Same scope's
        // AppDbContext, owned by the container — not disposed here.
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(persisted.UpdatedAtUtc);
        Assert.Equal(createdAtUtc, persisted.CreatedAtUtc);
    }

    private static AppUser NewStaffUser()
    {
        var email = $"audit-{Guid.NewGuid():N}@example.com";
        return new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = "Test",
            LastName = "Staff",
            IsActive = true,
        };
    }
}
