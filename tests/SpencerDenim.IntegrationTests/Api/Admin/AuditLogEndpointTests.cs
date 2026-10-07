using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// ADM-03 audit log viewer, end to end against a real SQL Server database (doc 06 §4). Every audit row
/// this suite writes carries a unique <c>CorrelationId</c> (<see cref="UniqueMarker"/>) and every filter
/// assertion narrows to it first, because <c>ops.AuditLogs</c> is shared across the whole assembly's test
/// run (rows are never deleted, doc 08 §1.6) and other tests keep writing to it in the same database.
/// </summary>
public sealed class AuditLogEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string AuditLogsUrl = "/api/admin/audit-logs";
    private const string LoginUrl = "/api/admin/auth/login";

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(new Uri(AuditLogsUrl, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_signed_in_staff_user_without_the_permission_is_forbidden()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("no-permission");
        const string password = "Correct-Password-1!";
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        // A role that exists but grants nothing: proves the check is "has AuditLog.View", not "is signed in".
        await CreateStaffUserWithRoleAsync(factory, email, password, permissions: []);
        using var client = CreateClient(factory);

        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var response = await client.GetAsync(new Uri(AuditLogsUrl, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_user_holding_AuditLog_View_can_list_entries_sorted_newest_first()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("auditor");
        const string password = "Correct-Password-1!";
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserWithRoleAsync(factory, email, password, permissions: [Permissions.AuditLog.View]);
        var marker = UniqueMarker();
        await SeedAuditLogsAsync(factory, marker, entries:
        [
            (new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), "Product", "1", "Create", 10, "first@example.com"),
            (new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc), "Product", "2", "Update", 11, "second@example.com"),
            (new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc), "Order", "5", "StatusChange", 10, "first@example.com"),
        ]);
        using var client = CreateClient(factory);

        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var response = await client.GetAsync(
            new Uri(
                $"{AuditLogsUrl}?entityType=&fromUtc=2026-09-01T00:00:00Z&toUtc=2026-09-04T00:00:00Z&pageSize=50",
                UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var ours = ItemsWithMarker(body, marker);

        Assert.Equal(3, ours.Count);
        // Newest first (OccurredAtUtc DESC), matching IX_AuditLogs_Occurred (doc 08 §3.8).
        Assert.Equal("StatusChange", ours[0].GetProperty("action").GetString());
        Assert.Equal("Update", ours[1].GetProperty("action").GetString());
        Assert.Equal("Create", ours[2].GetProperty("action").GetString());
    }

    [Fact]
    public async Task Results_can_be_filtered_by_entity_type_entity_id_user_action_and_date_range()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("filterer");
        const string password = "Correct-Password-1!";
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserWithRoleAsync(factory, email, password, permissions: [Permissions.AuditLog.View]);
        // **The ids are deliberately far outside the range any real row can hold**, and that is a repair
        // rather than a decoration. The marker narrows the *assertions*, but every filter here is read
        // off page one of 50 — so a filter that other tests also match (`userId=20`, `entityId=100`) can
        // crowd the marked rows off that page as `ops.AuditLogs` grows, and rows are never deleted
        // (doc 08 §1.6). W2-11 added twenty-eight tests that write audit rows and tipped it over:
        // `userId=20` began returning fifty newer rows from a real staff user who happened to be given
        // that id. The marker survives, but the page it is on does not.
        //
        // A user id and an entity id nothing else will ever use make every filter below effectively
        // unique to this test, so the page size stops mattering.
        var marker = UniqueMarker();
        await SeedAuditLogsAsync(factory, marker, entries:
        [
            (new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), "Product", "900100", "Update", 900020, "a@example.com"),
            (new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc), "Product", "900101", "Update", 900021, "b@example.com"),
            (new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "Order", "900100", "Approve", 900020, "a@example.com"),
        ]);
        using var client = CreateClient(factory);

        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // entityType=Product narrows to the two Product rows. Narrowed by the unique user id too,
        // because every product test in the assembly writes real `Product` rows and this filter would
        // otherwise be reading page one of all of them.
        var byEntityType = await GetFilteredAsync(client, "entityType=Product&userId=900020");
        Assert.Single(ItemsWithMarker(byEntityType, marker));

        var byEntityTypeOnly = await GetFilteredAsync(client, "entityType=Product&userId=900021");
        Assert.Single(ItemsWithMarker(byEntityTypeOnly, marker));

        // entityId=100 across all entity types narrows to Product/100 and Order/100.
        var byEntityId = await GetFilteredAsync(client, "entityId=900100");
        Assert.Equal(2, ItemsWithMarker(byEntityId, marker).Count);

        // entityType + entityId together narrow to exactly one row.
        var byBoth = await GetFilteredAsync(client, "entityType=Product&entityId=900100");
        var bothItems = ItemsWithMarker(byBoth, marker);
        Assert.Single(bothItems);
        Assert.Equal("Update", bothItems[0].GetProperty("action").GetString());

        // userId=20 narrows to the two rows acted by user 20.
        var byUser = await GetFilteredAsync(client, "userId=900020");
        Assert.Equal(2, ItemsWithMarker(byUser, marker).Count);

        // action=Approve narrows to one row. Narrowed by the unique user id for the same reason.
        var byAction = await GetFilteredAsync(client, "action=Approve&userId=900020");
        var actionItems = ItemsWithMarker(byAction, marker);
        Assert.Single(actionItems);
        Assert.Equal("Order", actionItems[0].GetProperty("entityType").GetString());

        // A date range covering only August excludes the September row. The dates are in the past and no
        // other test writes rows there, so this one needs no narrowing.
        var byRange = await GetFilteredAsync(client, "fromUtc=2026-08-01&toUtc=2026-08-31T23:59:59Z");
        Assert.Equal(2, ItemsWithMarker(byRange, marker).Count);

        async Task<JsonElement> GetFilteredAsync(HttpClient httpClient, string query)
        {
            using var filterResponse = await httpClient.GetAsync(
                new Uri($"{AuditLogsUrl}?{query}&pageSize=50", UriKind.Relative),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, filterResponse.StatusCode);
            return await filterResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Regression test: the admin screen's "To" filter is an HTML <c>&lt;input type="date"&gt;</c>, which
    /// sends a bare date with no time-of-day, e.g. <c>toUtc=2026-08-31</c>. Before <c>GetAuditLogsQuery</c>
    /// rolled a bare upper bound to the end of that day, this literally meant "at or before
    /// 2026-08-31T00:00:00.000Z" and silently dropped every row from later the same day — exactly the
    /// rows a caller who typed that date plainly meant to include.
    /// </summary>
    [Fact]
    public async Task A_bare_to_date_includes_rows_from_later_the_same_day()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("end-of-day");
        const string password = "Correct-Password-1!";
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserWithRoleAsync(factory, email, password, permissions: [Permissions.AuditLog.View]);
        var marker = UniqueMarker();
        await SeedAuditLogsAsync(factory, marker, entries:
        [
            (new DateTime(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc), "Product", "1", "Update", 40, "late@example.com"),
            (new DateTime(2026, 9, 1, 1, 0, 0, DateTimeKind.Utc), "Product", "2", "Update", 40, "late@example.com"),
        ]);
        using var client = CreateClient(factory);

        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var response = await client.GetAsync(
            new Uri($"{AuditLogsUrl}?toUtc=2026-08-31&pageSize=50", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var ours = ItemsWithMarker(body, marker);

        Assert.Single(ours);
        Assert.Equal("1", ours[0].GetProperty("entityId").GetString());
    }

    [Fact]
    public async Task Paging_returns_the_requested_page_and_an_accurate_total_count()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("pager");
        const string password = "Correct-Password-1!";
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserWithRoleAsync(factory, email, password, permissions: [Permissions.AuditLog.View]);
        var marker = UniqueMarker();
        var entries = Enumerable.Range(0, 5)
            .Select(i => (
                new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                "Product",
                i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Update",
                (int?)30,
                (string?)"pager@example.com"))
            .ToList();
        await SeedAuditLogsAsync(factory, marker, entries);
        using var client = CreateClient(factory);

        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var response = await client.GetAsync(
            new Uri(
                // Bounded to the window this test seeded: user ids are assigned by Identity, so a staff
                // account created by another test can legitimately be user 30 and write real audit rows.
                $"{AuditLogsUrl}?userId=30&fromUtc=2026-09-01T00:00:00Z&toUtc=2026-09-02T00:00:00Z&page=2&pageSize=2",
                UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(5, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, body.GetProperty("page").GetInt32());
        Assert.Equal(2, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task An_invalid_query_parameter_is_a_400_naming_the_field()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("bad-query");
        const string password = "Correct-Password-1!";
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserWithRoleAsync(factory, email, password, permissions: [Permissions.AuditLog.View]);
        using var client = CreateClient(factory);

        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var response = await client.GetAsync(
            new Uri($"{AuditLogsUrl}?userId=not-a-number", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("errors").TryGetProperty("userId", out _));
    }

    /// <summary>
    /// DB24: before/after JSON is already redacted at write time. This is not the redaction test (that
    /// belongs with the audit interceptor) — it only proves the endpoint passes the stored JSON through
    /// unchanged, rather than reformatting or dropping it.
    /// </summary>
    [Fact]
    public async Task Before_and_after_JSON_is_returned_exactly_as_stored()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var email = UniqueEmail("json-viewer");
        const string password = "Correct-Password-1!";
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserWithRoleAsync(factory, email, password, permissions: [Permissions.AuditLog.View]);
        var marker = UniqueMarker();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.AuditLogs.Add(new AuditLog
            {
                OccurredAtUtc = DateTime.UtcNow,
                UserId = 99,
                UserEmail = "json@example.com",
                Action = "Update",
                EntityType = "Product",
                EntityId = "7",
                BeforeJson = """{"name":"Old"}""",
                AfterJson = """{"name":"New"}""",
                CorrelationId = marker,
            });
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = CreateClient(factory);
        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var response = await client.GetAsync(
            new Uri($"{AuditLogsUrl}?entityType=Product&entityId=7", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var item = Assert.Single(ItemsWithMarker(body, marker));
        Assert.Equal("""{"name":"Old"}""", item.GetProperty("beforeJson").GetString());
        Assert.Equal("""{"name":"New"}""", item.GetProperty("afterJson").GetString());
    }

    private static List<JsonElement> ItemsWithMarker(JsonElement body, string marker) =>
        body.GetProperty("items")
            .EnumerateArray()
            .Where(item => item.TryGetProperty("correlationId", out var correlation) && correlation.GetString() == marker)
            .ToList();

    private static string UniqueEmail(string label) => $"{label}-{Guid.NewGuid():N}@example.com";

    private static string UniqueMarker() => $"itest-{Guid.NewGuid():N}";

    private static HttpClient CreateClient(ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static async Task SeedAuditLogsAsync(
        ApiFactory factory,
        string marker,
        IReadOnlyList<(DateTime OccurredAtUtc, string EntityType, string EntityId, string Action, int? UserId, string? UserEmail)> entries)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The fixture database is shared and outlives a run, so rows this class seeded earlier are still
        // here and would be counted again. Nothing in production writes an "itest-" correlation id.
        await dbContext.AuditLogs
            .Where(log => log.CorrelationId != null && log.CorrelationId.StartsWith("itest-"))
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        foreach (var entry in entries)
        {
            dbContext.AuditLogs.Add(new AuditLog
            {
                OccurredAtUtc = entry.OccurredAtUtc,
                UserId = entry.UserId,
                UserEmail = entry.UserEmail,
                Action = entry.Action,
                EntityType = entry.EntityType,
                EntityId = entry.EntityId,
                CorrelationId = marker,
            });
        }

        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> CreateStaffUserWithRoleAsync(
        ApiFactory factory,
        string email,
        string password,
        IReadOnlyList<string> permissions)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = "Test",
            LastName = "Auditor",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));

        var roleName = $"role-{Guid.NewGuid():N}";
        var role = new AppRole
        {
            Name = roleName,
            Description = "Test role (W1-09 audit log viewer tests)",
            IsSystem = false,
            GrantsAllPermissions = false,
            CreatedAtUtc = DateTime.UtcNow,
        };
        var roleResult = await roleManager.CreateAsync(role);
        Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));

        foreach (var permission in permissions)
        {
            var claimResult = await roleManager.AddClaimAsync(role, new System.Security.Claims.Claim(RoleClaimConfiguration.PermissionClaimType, permission));
            Assert.True(claimResult.Succeeded, string.Join("; ", claimResult.Errors.Select(e => e.Description)));
        }

        var assignResult = await userManager.AddToRoleAsync(user, roleName);
        Assert.True(assignResult.Succeeded, string.Join("; ", assignResult.Errors.Select(e => e.Description)));

        return user.Id;
    }
}
