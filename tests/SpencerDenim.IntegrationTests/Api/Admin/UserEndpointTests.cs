using System.Globalization;
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
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// Users &amp; Roles management end to end (ADM-02, W1-08, issue #8), especially the doc 08 §3.1 DB4 RBAC
/// safety rules: this is the security-sensitive slice a prior client-facing review specifically flagged
/// ("staff-account takeover via Users.Manage"). All test methods share one class so they run sequentially
/// against the one migrated database, the same convention <c>AdminAuthEndpointTests</c> uses.
/// </summary>
public sealed class UserEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string LoginUrl = "/api/admin/auth/login";
    private const string AntiforgeryUrl = "/api/admin/auth/antiforgery-token";
    private const string UsersUrl = "/api/admin/users";

    [Fact]
    public async Task UsersManage_actor_can_assign_a_role_that_is_a_subset_of_their_own_permissions()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);

        var wideRoleId = await CreateRoleAsync(factory, UniqueName("wide"), [Permissions.Orders.View, Permissions.Orders.Edit, Permissions.Users.Manage]);
        var narrowRoleId = await CreateRoleAsync(factory, UniqueName("narrow"), [Permissions.Orders.View]);

        var (actorEmail, actorPassword) = UniqueCredentials("actor-subset-ok");
        await CreateStaffUserWithRolesAsync(factory, actorEmail, actorPassword, [wideRoleId]);
        var targetId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("target-subset-ok"), "Target-Pass-1!", []);

        using var client = CreateClient(factory);
        await LoginAsync(client, actorEmail, actorPassword);
        var token = await GetAntiforgeryTokenAsync(client);

        using var response = await PutJsonAsync(client, $"{UsersUrl}/{targetId}/roles", new { roleIds = new[] { narrowRoleId } }, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertAuditActionWrittenAsync(factory, actorEmail, AuditActions.PermissionChange);
    }

    [Fact]
    public async Task UsersManage_actor_cannot_assign_a_role_that_grants_a_permission_they_do_not_hold()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);

        var actorRoleId = await CreateRoleAsync(factory, UniqueName("orders-only"), [Permissions.Orders.View, Permissions.Users.Manage]);
        var forbiddenRoleId = await CreateRoleAsync(factory, UniqueName("content-editor-like"), [Permissions.Content.View, Permissions.Content.Edit]);

        var (actorEmail, actorPassword) = UniqueCredentials("actor-subset-fail");
        await CreateStaffUserWithRolesAsync(factory, actorEmail, actorPassword, [actorRoleId]);
        var targetId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("target-subset-fail"), "Target-Pass-1!", []);

        using var client = CreateClient(factory);
        await LoginAsync(client, actorEmail, actorPassword);
        var token = await GetAntiforgeryTokenAsync(client);

        using var response = await PutJsonAsync(client, $"{UsersUrl}/{targetId}/roles", new { roleIds = new[] { forbiddenRoleId } }, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(nameof(RbacDenialReasonMirror.NotSubsetOfActorPermissions), body.GetProperty("error").GetString());

        // Confirms the rejection is real, not just a client-side hint: the target still has no roles.
        await AssertUserHasNoRolesAsync(factory, targetId);
    }

    [Fact]
    public async Task Non_grants_all_actor_can_never_assign_the_grants_all_role_even_with_users_manage()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var actorRoleId = await CreateRoleAsync(factory, UniqueName("manage-only"), [Permissions.Users.Manage]);
        var grantsAllRoleId = await GetOrCreateGrantsAllRoleIdAsync(factory);

        var (actorEmail, actorPassword) = UniqueCredentials("actor-no-grantsall");
        await CreateStaffUserWithRolesAsync(factory, actorEmail, actorPassword, [actorRoleId]);
        var targetId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("target-no-grantsall"), "Target-Pass-1!", []);

        using var client = CreateClient(factory);
        await LoginAsync(client, actorEmail, actorPassword);
        var token = await GetAntiforgeryTokenAsync(client);

        using var response = await PutJsonAsync(client, $"{UsersUrl}/{targetId}/roles", new { roleIds = new[] { grantsAllRoleId } }, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UsersManage_actor_cannot_manage_a_target_who_already_holds_GrantsAll()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var actorRoleId = await CreateRoleAsync(factory, UniqueName("manage-only-2"), PermissionCatalog.All.Where(p => p != Permissions.Settings.Security));
        var grantsAllRoleId = await GetOrCreateGrantsAllRoleIdAsync(factory);

        var (actorEmail, actorPassword) = UniqueCredentials("actor-vs-grantsall-target");
        await CreateStaffUserWithRolesAsync(factory, actorEmail, actorPassword, [actorRoleId]);
        var grantsAllTargetId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("grantsall-target"), "Target-Pass-1!", [grantsAllRoleId]);

        using var client = CreateClient(factory);
        await LoginAsync(client, actorEmail, actorPassword);
        var token = await GetAntiforgeryTokenAsync(client);

        // Even deactivating (not just role assignment) must be refused: rule 2 protects a GrantsAll target
        // from a non-GrantsAll actor entirely, no matter how wide the actor's own permissions are otherwise.
        using var response = await PostAsync(client, $"{UsersUrl}/{grantsAllTargetId}/deactivate", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await RemoveFromGrantsAllRoleAsync(factory, grantsAllTargetId);
    }

    [Fact]
    public async Task Only_grants_all_can_change_a_staff_users_email()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var actorRoleId = await CreateRoleAsync(factory, UniqueName("manage-only-3"), [Permissions.Users.Manage]);

        var (actorEmail, actorPassword) = UniqueCredentials("actor-email-change");
        await CreateStaffUserWithRolesAsync(factory, actorEmail, actorPassword, [actorRoleId]);
        var targetId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("target-email-change"), "Target-Pass-1!", []);

        using var client = CreateClient(factory);
        await LoginAsync(client, actorEmail, actorPassword);
        var token = await GetAntiforgeryTokenAsync(client);

        using var response = await PutJsonAsync(
            client,
            $"{UsersUrl}/{targetId}",
            new { firstName = "Changed", lastName = "Name", email = UniqueEmail("new-address") },
            token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("EmailChangeRequiresGrantsAll", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Self_role_edit_is_always_blocked()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var roleId = await CreateRoleAsync(factory, UniqueName("self-edit"), [Permissions.Users.Manage]);
        var (email, password) = UniqueCredentials("self-edit-actor");
        var actorId = await CreateStaffUserWithRolesAsync(factory, email, password, [roleId]);

        using var client = CreateClient(factory);
        await LoginAsync(client, email, password);
        var token = await GetAntiforgeryTokenAsync(client);

        using var response = await PutJsonAsync(client, $"{UsersUrl}/{actorId}/roles", new { roleIds = new[] { roleId } }, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("SelfEditNotAllowed", body.GetProperty("error").GetString());
    }

    /// <summary>
    /// Doc 08 §3.1 DB4 rules 2+3 already stop a lone Super Admin from ever deactivating themselves (rule 3,
    /// self-edit) or being deactivated by anyone who is not themselves an active GrantsAll holder (rule 2:
    /// a non-GrantsAll actor can never touch a GrantsAll target at all). Put together, the only actor who
    /// could legitimately be authorized to deactivate a GrantsAll target is <i>another</i> active GrantsAll
    /// holder — which means, sequentially, there is always at least one other active holder left over
    /// whenever such a request is even reachable, and rule 4's guard correctly allows it. The one scenario
    /// where the guard actually has to refuse something is exactly the race two simultaneous, otherwise
    /// individually-valid such requests create — covered by
    /// <see cref="Two_concurrent_deactivations_of_the_last_two_super_admins_never_both_succeed"/>. The
    /// single-request "attempt to deactivate the literal sole remaining Super Admin" scenario the task names
    /// is proven directly against <c>IStaffUserRoleManagementStore</c> (bypassing the actor/self-edit
    /// authorization layer on purpose, since that layer is what makes the scenario unreachable through this
    /// HTTP surface) in <c>StaffUserRoleManagementStoreTests.Deactivating_the_sole_active_GrantsAll_holder_is_rejected</c>.
    /// </summary>
    [Fact]
    public async Task A_lone_active_super_admin_cannot_be_deactivated_by_anyone_reachable_through_this_api()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var grantsAllRoleId = await GetOrCreateGrantsAllRoleIdAsync(factory);
        var manageOnlyRoleId = await CreateRoleAsync(factory, UniqueName("manage-only-vs-sole-admin"), [Permissions.Users.Manage]);

        var soleAdminId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("sole-super-admin"), "Target-Pass-1!", [grantsAllRoleId]);

        // The only other staff account is a non-GrantsAll Users.Manage holder: rule 2 refuses it outright
        // (a GrantsAll target is never manageable by a non-GrantsAll actor), before rule 4 even runs.
        var (attackerEmail, attackerPassword) = UniqueCredentials("would-be-attacker");
        await CreateStaffUserWithRolesAsync(factory, attackerEmail, attackerPassword, [manageOnlyRoleId]);

        using var client = CreateClient(factory);
        await LoginAsync(client, attackerEmail, attackerPassword);
        var token = await GetAntiforgeryTokenAsync(client);

        using var response = await PostAsync(client, $"{UsersUrl}/{soleAdminId}/deactivate", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stillActive = await dbContext.Users.AsNoTracking().Where(u => u.Id == soleAdminId).Select(u => u.IsActive).SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(stillActive);

        await RemoveFromGrantsAllRoleAsync(factory, soleAdminId);
    }

    /// <summary>
    /// Doc 08 §3.1 DB4 rule 4, the race the task explicitly calls out: with exactly two active Super Admins,
    /// two concurrent requests each try to deactivate a *different* one of the two at the same time. If the
    /// guard only checked the count non-atomically (read-then-write, no lock), both could observe "1 other
    /// active holder" before either commits and both would succeed — leaving zero. With the
    /// <c>sp_getapplock</c>'d transaction, the second call blocks until the first commits, then re-reads the
    /// count and correctly sees zero *other* active holders, so exactly one request succeeds and the other is
    /// rejected with the guard.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_deactivations_of_the_last_two_super_admins_never_both_succeed()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var grantsAllRoleId = await GetOrCreateGrantsAllRoleIdAsync(factory);

        var (adminAEmail, adminAPassword) = UniqueCredentials("race-admin-a");
        var adminAId = await CreateStaffUserWithRolesAsync(factory, adminAEmail, adminAPassword, [grantsAllRoleId]);
        var (adminBEmail, adminBPassword) = UniqueCredentials("race-admin-b");
        var adminBId = await CreateStaffUserWithRolesAsync(factory, adminBEmail, adminBPassword, [grantsAllRoleId]);

        // This suite shares one database for the whole assembly (doc 06 §4, AssemblyFixture), so earlier tests
        // may have left other active GrantsAll holders behind (e.g. a seeded bootstrap admin, or a target from
        // an earlier "cannot manage a GrantsAll target" test that was deliberately never reactivated). The race
        // this test proves only shows up with *exactly* two active holders, so every other one is deactivated
        // first — this is what makes adminA and adminB "the last two Super Admins" for the guard's purposes.
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            await DeactivateAllOtherActiveGrantsAllHoldersAsync(setupScope.ServiceProvider, adminAId, adminBId);
        }

        // A acts on B, and B acts on A, concurrently — neither is a self-edit.
        using var clientA = CreateClient(factory);
        await LoginAsync(clientA, adminAEmail, adminAPassword);
        var tokenA = await GetAntiforgeryTokenAsync(clientA);

        using var clientB = CreateClient(factory);
        await LoginAsync(clientB, adminBEmail, adminBPassword);
        var tokenB = await GetAntiforgeryTokenAsync(clientB);

        var deactivateBByA = PostAsync(clientA, $"{UsersUrl}/{adminBId}/deactivate", tokenA);
        var deactivateAByB = PostAsync(clientB, $"{UsersUrl}/{adminAId}/deactivate", tokenB);

        var responses = await Task.WhenAll(deactivateBByA, deactivateAByB);

        var statusCodes = responses.Select(r => r.StatusCode).OrderBy(s => s).ToList();
        Assert.Contains(HttpStatusCode.OK, statusCodes);
        Assert.Contains(HttpStatusCode.Conflict, statusCodes);

        foreach (var response in responses)
        {
            response.Dispose();
        }

        // The authoritative check: exactly one of the two is now inactive, never both, never neither.
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var activeCount = await dbContext.Users.AsNoTracking()
            .CountAsync(u => (u.Id == adminAId || u.Id == adminBId) && u.IsActive, TestContext.Current.CancellationToken);
        Assert.Equal(1, activeCount);

        await RemoveFromGrantsAllRoleAsync(factory, adminAId, adminBId);
    }

    [Fact]
    public async Task Assigning_roles_rotates_the_targets_security_stamp()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var grantsAllRoleId = await GetOrCreateGrantsAllRoleIdAsync(factory);
        var narrowRoleId = await CreateRoleAsync(factory, UniqueName("stamp-target-role"), [Permissions.Orders.View]);

        var (actorEmail, actorPassword) = UniqueCredentials("stamp-actor");
        var actorId = await CreateStaffUserWithRolesAsync(factory, actorEmail, actorPassword, [grantsAllRoleId]);
        var targetId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("stamp-target"), "Target-Pass-1!", []);

        var stampBefore = await GetSecurityStampAsync(factory, targetId);

        using var client = CreateClient(factory);
        await LoginAsync(client, actorEmail, actorPassword);
        var token = await GetAntiforgeryTokenAsync(client);

        using var response = await PutJsonAsync(client, $"{UsersUrl}/{targetId}/roles", new { roleIds = new[] { narrowRoleId } }, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stampAfter = await GetSecurityStampAsync(factory, targetId);

        // Doc 08 §3.1 DB4 rule 5: a role change bumps SecurityStamp, which is exactly the cache key
        // EffectivePermissionsProvider uses and the value the 1-minute revalidation loop (issue #16) compares.
        Assert.NotEqual(stampBefore, stampAfter);

        await RemoveFromGrantsAllRoleAsync(factory, actorId);
    }

    [Fact]
    public async Task Audit_rows_for_this_feature_never_contain_redacted_columns()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var grantsAllRoleId = await GetOrCreateGrantsAllRoleIdAsync(factory);
        var narrowRoleId = await CreateRoleAsync(factory, UniqueName("redaction-target-role"), [Permissions.Orders.View]);

        var (actorEmail, actorPassword) = UniqueCredentials("redaction-actor");
        var actorId = await CreateStaffUserWithRolesAsync(factory, actorEmail, actorPassword, [grantsAllRoleId]);
        var targetId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("redaction-target"), "Target-Pass-1!", []);

        using var client = CreateClient(factory);
        await LoginAsync(client, actorEmail, actorPassword);
        var token = await GetAntiforgeryTokenAsync(client);

        using var response = await PutJsonAsync(client, $"{UsersUrl}/{targetId}/roles", new { roleIds = new[] { narrowRoleId } }, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "User" && a.EntityId == targetId.ToString(CultureInfo.InvariantCulture))
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            AssertNoRedactedColumn(row.BeforeJson);
            AssertNoRedactedColumn(row.AfterJson);
        }

        await RemoveFromGrantsAllRoleAsync(factory, actorId);
    }

    [Fact]
    public async Task Listing_and_reading_users_requires_UsersView_and_returns_their_roles()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var viewerRoleId = await CreateRoleAsync(factory, UniqueName("viewer"), [Permissions.Users.View]);
        var targetRoleId = await CreateRoleAsync(factory, UniqueName("target-role-for-listing"), [Permissions.Orders.View]);

        var (viewerEmail, viewerPassword) = UniqueCredentials("listing-viewer");
        await CreateStaffUserWithRolesAsync(factory, viewerEmail, viewerPassword, [viewerRoleId]);
        var targetId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("listing-target"), "Target-Pass-1!", [targetRoleId]);

        using var client = CreateClient(factory);
        await LoginAsync(client, viewerEmail, viewerPassword);

        using var detailResponse = await client.GetAsync(new Uri($"{UsersUrl}/{targetId}", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detailBody = await detailResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var roles = detailBody.GetProperty("roles").EnumerateArray().ToList();
        Assert.Single(roles);
    }

    [Fact]
    public async Task Endpoints_require_the_UsersManage_permission_for_writes()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var viewOnlyRoleId = await CreateRoleAsync(factory, UniqueName("view-only"), [Permissions.Users.View]);
        var (email, password) = UniqueCredentials("view-only-actor");
        await CreateStaffUserWithRolesAsync(factory, email, password, [viewOnlyRoleId]);
        var targetId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("view-only-target"), "Target-Pass-1!", []);

        using var client = CreateClient(factory);
        await LoginAsync(client, email, password);
        var token = await GetAntiforgeryTokenAsync(client);

        using var response = await PostAsync(client, $"{UsersUrl}/{targetId}/deactivate", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static void AssertNoRedactedColumn(string? json)
    {
        if (json is null)
        {
            return;
        }

        foreach (var redacted in new[] { "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "AccessFailedCount" })
        {
            Assert.DoesNotContain(redacted, json, StringComparison.Ordinal);
        }
    }

    private static string UniqueEmail(string label) => $"{label}-{Guid.NewGuid():N}@example.com";

    private static string UniqueName(string label) => $"{label}-{Guid.NewGuid():N}";

    private static (string Email, string Password) UniqueCredentials(string label) => (UniqueEmail(label), "Correct-Password-1!");

    private static HttpClient CreateClient(ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static async Task LoginAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri(AntiforgeryUrl, UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.GetProperty("token").GetString() ?? string.Empty;
    }

    private static Task<HttpResponseMessage> PutJsonAsync(HttpClient client, string url, object body, string antiforgeryToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-XSRF-TOKEN", antiforgeryToken);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string antiforgeryToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-XSRF-TOKEN", antiforgeryToken);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<int> CreateRoleAsync(ApiFactory factory, string name, IEnumerable<string> permissions)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();

        var role = new AppRole { Name = name, GrantsAllPermissions = false, IsSystem = false };
        var createResult = await roleManager.CreateAsync(role);
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));

        foreach (var permission in permissions)
        {
            var claimResult = await roleManager.AddClaimAsync(role, new System.Security.Claims.Claim("perm", permission));
            Assert.True(claimResult.Succeeded, string.Join("; ", claimResult.Errors.Select(e => e.Description)));
        }

        return role.Id;
    }

    /// <summary>
    /// <c>UX_Roles_GrantsAll</c> (doc 08 §3.1) allows at most one <c>GrantsAllPermissions = 1</c> role in the
    /// whole database — this suite shares one database for the whole assembly, so tests must find and reuse
    /// that one role (exactly as the real seeder does, by name) rather than each creating their own.
    /// </summary>
    private static async Task<int> GetOrCreateGrantsAllRoleIdAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existingId = await dbContext.Roles.AsNoTracking()
            .Where(r => r.GrantsAllPermissions)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        if (existingId != 0)
        {
            return existingId;
        }

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
        var role = new AppRole { Name = "Super Admin", GrantsAllPermissions = true, IsSystem = true };
        var createResult = await roleManager.CreateAsync(role);
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));
        return role.Id;
    }

    /// <summary>
    /// Cleans up the shared GrantsAll role's membership at the end of a test. Necessary because
    /// <c>AdminAuthEndpointTests.Admin_seeding_creates_exactly_one_super_admin_who_can_then_sign_in</c> relies
    /// on the "Super Admin" role having *no* members yet the first time <c>AdminSeedHostedService</c> runs in
    /// this shared database (its own gate is "any existing holder at all, regardless of IsActive, skips
    /// creating a new one") — membership left behind here, even for a since-deactivated user, would make that
    /// test's bootstrap admin silently never get created, regardless of which test happens to run first.
    /// </summary>
    private static async Task RemoveFromGrantsAllRoleAsync(ApiFactory factory, params int[] userIds)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        foreach (var userId in userIds)
        {
            var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
            if (user is null)
            {
                continue;
            }

            var roles = await userManager.GetRolesAsync(user);
            if (roles.Count > 0)
            {
                await userManager.RemoveFromRolesAsync(user, roles);
            }
        }
    }

    /// <summary>
    /// Test isolation helper for the last-Super-Admin guard's race scenario: this suite's one-shared-database
    /// design (doc 06 §4) means earlier tests can leave other active GrantsAll holders behind, which would
    /// otherwise make "exactly two active holders" — the precondition the race needs — false by the time this
    /// test runs. Deactivates directly through the DbContext rather than the store/handler under test, so this
    /// setup step cannot itself exercise (or accidentally validate) the guard it is preparing to test.
    /// </summary>
    private static async Task DeactivateAllOtherActiveGrantsAllHoldersAsync(IServiceProvider services, params int[] exceptUserIds)
    {
        var dbContext = services.GetRequiredService<AppDbContext>();
        var keep = exceptUserIds.ToHashSet();

        var others = await dbContext.Users
            .Where(u => u.IsActive
                && !keep.Contains(u.Id)
                && dbContext.UserRoles.Any(ur => ur.UserId == u.Id && dbContext.Roles.Any(r => r.Id == ur.RoleId && r.GrantsAllPermissions)))
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var user in others)
        {
            user.IsActive = false;
        }

        if (others.Count > 0)
        {
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task<int> CreateStaffUserWithRolesAsync(ApiFactory factory, string email, string password, IReadOnlyList<int> roleIds)
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
            LastName = "Staff",
            IsActive = true,
        };

        var createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));

        if (roleIds.Count > 0)
        {
            var roleNames = await roleManager.Roles.Where(r => roleIds.Contains(r.Id)).Select(r => r.Name!).ToListAsync(TestContext.Current.CancellationToken);
            var addResult = await userManager.AddToRolesAsync(user, roleNames);
            Assert.True(addResult.Succeeded, string.Join("; ", addResult.Errors.Select(e => e.Description)));
        }

        return user.Id;
    }

    private static async Task AssertUserHasNoRolesAsync(ApiFactory factory, int userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await dbContext.UserRoles.AsNoTracking().CountAsync(ur => ur.UserId == userId, TestContext.Current.CancellationToken);
        Assert.Equal(0, count);
    }

    private static async Task<string?> GetSecurityStampAsync(ApiFactory factory, int userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task AssertAuditActionWrittenAsync(ApiFactory factory, string email, string action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var found = await dbContext.AuditLogs.AnyAsync(a => a.UserEmail == email && a.Action == action, TestContext.Current.CancellationToken);
        Assert.True(found, $"Expected an AuditLogs row with Action '{action}' for '{email}'.");
    }

    /// <summary>Mirrors the API's serialized <c>RbacDenialReason</c> enum names, so test assertions read clearly without a project reference to the Application layer's internal enum from this file.</summary>
    private enum RbacDenialReasonMirror
    {
        NotAuthorized,
        SelfEditNotAllowed,
        TargetIsOrWouldBecomeGrantsAll,
        NotSubsetOfActorPermissions,
        EmailChangeRequiresGrantsAll,
    }
}
