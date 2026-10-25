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
    private const string TwoFactorSetupUrl = "/api/admin/auth/two-factor/setup";
    private const string TwoFactorEnableUrl = "/api/admin/auth/two-factor/enable";

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

    /// <summary>
    /// Tester-round-1 fix (W1-08, #8, defect 1): before this fix, a non-GrantsAll <c>Users.Manage</c> actor
    /// got a distinct, machine-readable reason ("TargetIsOrWouldBecomeGrantsAll") for a target that holds
    /// Super Admin, versus the generic "NotSubsetOfActorPermissions" for a target that merely has one extra
    /// ordinary permission — an identity oracle letting that actor fish for who is a Super Admin. This proves
    /// the two 403 bodies are now identical in every field that reaches the actor, not just "both 403".
    /// </summary>
    [Fact]
    public async Task GrantsAll_target_and_merely_over_permissioned_target_produce_byte_identical_denial_bodies()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var actorRoleId = await CreateRoleAsync(factory, UniqueName("manage-only-oracle"), [Permissions.Users.Manage, Permissions.Orders.View]);
        var overPermissionedRoleId = await CreateRoleAsync(factory, UniqueName("over-permissioned"), [Permissions.Content.View]);
        var grantsAllRoleId = await GetOrCreateGrantsAllRoleIdAsync(factory);

        var (actorEmail, actorPassword) = UniqueCredentials("actor-oracle-check");
        await CreateStaffUserWithRolesAsync(factory, actorEmail, actorPassword, [actorRoleId]);
        var overPermissionedTargetId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("over-permissioned-target"), "Target-Pass-1!", [overPermissionedRoleId]);
        var grantsAllTargetId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("grantsall-target-oracle"), "Target-Pass-1!", [grantsAllRoleId]);

        using var client = CreateClient(factory);
        await LoginAsync(client, actorEmail, actorPassword);

        var tokenForOverPermissioned = await GetAntiforgeryTokenAsync(client);
        using var overPermissionedResponse = await PostAsync(client, $"{UsersUrl}/{overPermissionedTargetId}/deactivate", tokenForOverPermissioned);
        var tokenForGrantsAll = await GetAntiforgeryTokenAsync(client);
        using var grantsAllResponse = await PostAsync(client, $"{UsersUrl}/{grantsAllTargetId}/deactivate", tokenForGrantsAll);

        Assert.Equal(HttpStatusCode.Forbidden, overPermissionedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, grantsAllResponse.StatusCode);

        var overPermissionedBody = await overPermissionedResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var grantsAllBody = await grantsAllResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(nameof(RbacDenialReasonMirror.NotSubsetOfActorPermissions), overPermissionedBody.GetProperty("error").GetString());
        Assert.Equal(overPermissionedBody.GetProperty("error").GetString(), grantsAllBody.GetProperty("error").GetString());
        Assert.Equal(overPermissionedBody.GetProperty("title").GetString(), grantsAllBody.GetProperty("title").GetString());
        Assert.Equal(overPermissionedBody.GetProperty("detail").GetString(), grantsAllBody.GetProperty("detail").GetString());
        Assert.Equal(overPermissionedBody.GetProperty("status").GetInt32(), grantsAllBody.GetProperty("status").GetInt32());

        // No extra property on either side that could itself become a new oracle (e.g. a richer field only
        // present for the GrantsAll case).
        var overPermissionedNames = overPermissionedBody.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var grantsAllNames = grantsAllBody.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(overPermissionedNames, grantsAllNames);

        await RemoveFromGrantsAllRoleAsync(factory, grantsAllTargetId);
    }

    /// <summary>
    /// Tester-round-1 fix (defect 1): the tester specifically flagged that combining a real 404 for "doesn't
    /// exist" with the distinguishable 403 reason worsened the oracle. A non-GrantsAll actor must now get the
    /// exact same 403 body for a target that does not exist at all as for one that exists but is protected.
    /// </summary>
    [Fact]
    public async Task Non_grants_all_actor_gets_the_generic_denial_not_a_404_for_a_target_that_does_not_exist()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var actorRoleId = await CreateRoleAsync(factory, UniqueName("manage-only-404"), [Permissions.Users.Manage]);
        var (actorEmail, actorPassword) = UniqueCredentials("actor-404-check");
        await CreateStaffUserWithRolesAsync(factory, actorEmail, actorPassword, [actorRoleId]);

        using var client = CreateClient(factory);
        await LoginAsync(client, actorEmail, actorPassword);
        var token = await GetAntiforgeryTokenAsync(client);

        // Some very large id that certainly does not exist in this test's shared database.
        using var response = await PostAsync(client, $"{UsersUrl}/2000000000/deactivate", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(nameof(RbacDenialReasonMirror.NotSubsetOfActorPermissions), body.GetProperty("error").GetString());
    }

    /// <summary>A Super Admin is allowed to know a target genuinely doesn't exist — unaffected by defect 1.</summary>
    [Fact]
    public async Task GrantsAll_actor_still_gets_a_real_404_for_a_target_that_does_not_exist()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var grantsAllRoleId = await GetOrCreateGrantsAllRoleIdAsync(factory);
        var (actorEmail, actorPassword) = UniqueCredentials("actor-404-grantsall");
        var actorId = await CreateStaffUserWithRolesAsync(factory, actorEmail, actorPassword, [grantsAllRoleId]);

        using var client = CreateClient(factory);
        await LoginAsync(client, actorEmail, actorPassword);
        var token = await GetAntiforgeryTokenAsync(client);

        using var response = await PostAsync(client, $"{UsersUrl}/2000000001/deactivate", token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await RemoveFromGrantsAllRoleAsync(factory, actorId);
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

    /// <summary>
    /// The other half of "only grants-all can change an email": when one does, the new address is the one the staff user
    /// signs in with and the old one stops working. Login resolves by the <i>normalised</i> email, so this fails if the
    /// save (issue #250's changed-columns-only user store) writes the typed address and leaves the normalised column
    /// behind: the user would be locked out of their own account by an administrator's edit.
    /// </summary>
    [Fact]
    public async Task A_staff_users_new_email_address_signs_in_and_the_old_one_no_longer_does()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var grantsAllRoleId = await GetOrCreateGrantsAllRoleIdAsync(factory);
        var (actorEmail, actorPassword) = UniqueCredentials("actor-email-signs-in");
        var actorId = await CreateStaffUserWithRolesAsync(factory, actorEmail, actorPassword, [grantsAllRoleId]);

        const string targetPassword = "Target-Pass-1!";
        var oldEmail = UniqueEmail("old-address");
        var newEmail = UniqueEmail("new-address");
        var targetId = await CreateStaffUserWithRolesAsync(factory, oldEmail, targetPassword, []);

        try
        {
            using var client = CreateClient(factory);
            await LoginAsync(client, actorEmail, actorPassword);
            var token = await GetAntiforgeryTokenAsync(client);

            using var response = await PutJsonAsync(
                client,
                $"{UsersUrl}/{targetId}",
                new { firstName = "Moved", lastName = "Address", email = newEmail },
                token);
            Assert.True(
                response.IsSuccessStatusCode,
                $"The email change was refused: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var stored = await dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == targetId, TestContext.Current.CancellationToken);
                Assert.Equal(newEmail, stored.Email);
                Assert.True(
                    string.Equals(stored.NormalizedEmail, userManager.NormalizeEmail(newEmail), StringComparison.Ordinal),
                    $"NormalizedEmail was not written with the new address. It is '{stored.NormalizedEmail}'.");
            }

            using var newAddress = CreateClient(factory);
            using var signedIn = await newAddress.PostAsJsonAsync(
                LoginUrl, new { email = newEmail, password = targetPassword }, TestContext.Current.CancellationToken);
            Assert.True(
                signedIn.StatusCode == HttpStatusCode.OK,
                $"The new address does not sign in: {(int)signedIn.StatusCode} {await signedIn.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");

            using var oldAddress = CreateClient(factory);
            using var refused = await oldAddress.PostAsJsonAsync(
                LoginUrl, new { email = oldEmail, password = targetPassword }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        finally
        {
            await RemoveFromGrantsAllRoleAsync(factory, actorId);
        }
    }

    /// <summary>
    /// Review round 4, item 4. <c>UserName</c> mirrors <c>Email</c> and nothing checks the address's shape before
    /// Identity does, so an address a real staff name produces — a space in it, an accent in it — is refused by
    /// Identity's <c>InvalidUserName</c> rule. Every Identity failure used to come back as
    /// <c>PasswordRejected</c>: a 400 keyed on <c>initialPassword</c>, titled "The initial password does not meet
    /// the password rules.", carrying Identity's message about the <i>address</i>, which the form then renders
    /// under the password box. The admin is told to fix the password and shown the email.
    /// <para>
    /// Asserted through HTTP rather than at the handler because the field key is the whole point: it is what the
    /// form reads to decide which input to put the message under.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("space")]
    [InlineData("accent")]
    public async Task An_email_Identity_refuses_is_reported_against_the_email_field_not_the_password(string shape)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var actorRoleId = await CreateRoleAsync(factory, UniqueName($"creator-{shape}"), [Permissions.Users.Manage, Permissions.Orders.View]);
        var newUserRoleId = await CreateRoleAsync(factory, UniqueName($"created-{shape}"), [Permissions.Orders.View]);

        var (actorEmail, actorPassword) = UniqueCredentials($"actor-create-{shape}");
        await CreateStaffUserWithRolesAsync(factory, actorEmail, actorPassword, [actorRoleId]);

        using var client = CreateClient(factory);
        await LoginAsync(client, actorEmail, actorPassword);
        var token = await GetAntiforgeryTokenAsync(client);

        // Both are what a Sri Lankan or European staff name actually produces when someone types it into an
        // address. The accented one passes every structural email check there is — exactly one @, no whitespace,
        // a real domain — so no amount of format validation on this side would catch it; only Identity's
        // allowed-characters rule refuses it, and that refusal has to name the right field.
        var email = shape == "space"
            ? $"nimal perera-{Guid.NewGuid():N}@example.com"
            : $"josé-{Guid.NewGuid():N}@example.com";

        using var response = await PostJsonAsync(
            client,
            UsersUrl,
            new { email, firstName = "Nimal", lastName = "Perera", initialPassword = "Correct-Password-1!", roleIds = new[] { newUserRoleId } },
            token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("email", out var emailErrors), $"Expected the error keyed on 'email'; got {errors.GetRawText()}");
        Assert.NotEmpty(emailErrors.EnumerateArray());
        Assert.False(errors.TryGetProperty("initialPassword", out _), "The password was valid; nothing may be reported against it.");
        Assert.DoesNotContain("password", body.GetProperty("title").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other half of item 4: a genuine password failure still gets the password-specific response, so the
    /// fix above cannot have been "key everything on the email instead".
    /// </summary>
    [Fact]
    public async Task A_password_the_policy_refuses_is_still_reported_against_the_password_field()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var actorRoleId = await CreateRoleAsync(factory, UniqueName("creator-weak-pw"), [Permissions.Users.Manage, Permissions.Orders.View]);
        var newUserRoleId = await CreateRoleAsync(factory, UniqueName("created-weak-pw"), [Permissions.Orders.View]);

        var (actorEmail, actorPassword) = UniqueCredentials("actor-create-weak-pw");
        await CreateStaffUserWithRolesAsync(factory, actorEmail, actorPassword, [actorRoleId]);

        using var client = CreateClient(factory);
        await LoginAsync(client, actorEmail, actorPassword);
        var token = await GetAntiforgeryTokenAsync(client);

        using var response = await PostJsonAsync(
            client,
            UsersUrl,
            new { email = UniqueEmail("weak-password-target"), firstName = "Weak", lastName = "Password", initialPassword = "abc", roleIds = new[] { newUserRoleId } },
            token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("errors").TryGetProperty("initialPassword", out var passwordErrors));
        Assert.NotEmpty(passwordErrors.EnumerateArray());
        Assert.Equal("The initial password does not meet the password rules.", body.GetProperty("title").GetString());
    }

    /// <summary>
    /// Issue #200. The initial password of a new staff user must reach the same minimum a customer's must
    /// (ten, W3-01): nine is a 400 keyed on <c>initialPassword</c> that states the rule and leaves no user
    /// behind, ten is a 201, and six, the old Identity default, is refused.
    /// </summary>
    /// <remarks>
    /// <b>The lengths are written out rather than read from the policy</b>, so this test is an oracle that
    /// stays independent of the constant it is checking: if the constant were lowered, a test built from it
    /// would follow it down and pass. Raising the minimum on purpose means updating these rows with it.
    /// Every candidate carries all four character classes, so length is the only thing that can decide the
    /// outcome.
    /// </remarks>
    [Theory]
    [InlineData(6, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    public async Task A_new_staff_users_initial_password_must_reach_the_customer_minimum(int length, bool accepted)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var actorRoleId = await CreateRoleAsync(factory, UniqueName("creator-min-pw"), [Permissions.Users.Manage, Permissions.Orders.View]);
        var newUserRoleId = await CreateRoleAsync(factory, UniqueName("created-min-pw"), [Permissions.Orders.View]);

        var (actorEmail, actorPassword) = UniqueCredentials("actor-create-min-pw");
        await CreateStaffUserWithRolesAsync(factory, actorEmail, actorPassword, [actorRoleId]);

        using var client = CreateClient(factory);
        await LoginAsync(client, actorEmail, actorPassword);
        var token = await GetAntiforgeryTokenAsync(client);

        var targetEmail = UniqueEmail("min-pw-target");
        using var response = await PostJsonAsync(
            client,
            UsersUrl,
            new
            {
                email = targetEmail,
                firstName = "Min",
                lastName = "Password",
                initialPassword = "Aa1!" + new string('x', length - 4),
                roleIds = new[] { newUserRoleId },
            },
            token);

        await using var scope = factory.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(targetEmail);

        if (accepted)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotNull(created);
            Assert.True(created.MustChangePassword);
            return;
        }

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("The initial password does not meet the password rules.", body.GetProperty("title").GetString());

        var messages = body.GetProperty("errors").GetProperty("initialPassword").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(PasswordPolicy.Requirement, messages);

        // The refusal rolled back: there is no half-made account to find.
        Assert.Null(created);
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

    /// <summary>
    /// Tester round 2, defect B. Closing the write-side oracle was moot while the read endpoints listed every
    /// Super Admin in plain JSON, and any real <c>Users.Manage</c> holder needs <c>Users.View</c> to pick a
    /// target at all. A viewer who is not a Super Admin must not see one: not in the page, not in the total.
    /// The same search is run by a Super Admin, who sees both, so the filter is shown to be doing the work
    /// rather than the search simply matching nothing.
    /// </summary>
    [Fact]
    public async Task A_viewer_without_GrantsAll_never_sees_a_Super_Admin_in_the_user_list_or_its_total()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var token = $"vis{Guid.NewGuid():N}";
        var viewerRoleId = await CreateRoleAsync(factory, UniqueName("viewer-list"), [Permissions.Users.View, Permissions.Users.Manage]);
        var ordinaryRoleId = await CreateRoleAsync(factory, UniqueName("ordinary-list"), [Permissions.Orders.View]);
        var grantsAllRoleId = await GetOrCreateGrantsAllRoleIdAsync(factory);

        var (viewerEmail, viewerPassword) = UniqueCredentials("viewer-list");
        await CreateStaffUserWithRolesAsync(factory, viewerEmail, viewerPassword, [viewerRoleId]);
        var ordinaryEmail = UniqueEmail($"{token}-ordinary");
        var superAdminEmail = UniqueEmail($"{token}-superadmin");
        await CreateStaffUserWithRolesAsync(factory, ordinaryEmail, "Target-Pass-1!", [ordinaryRoleId]);
        var superAdminId = await CreateStaffUserWithRolesAsync(factory, superAdminEmail, "Target-Pass-1!", [grantsAllRoleId]);

        var (adminEmail, adminPassword) = UniqueCredentials("admin-list");
        var adminId = await CreateStaffUserWithRolesAsync(factory, adminEmail, adminPassword, [grantsAllRoleId]);

        try
        {
            using var viewerClient = CreateClient(factory);
            await LoginAsync(viewerClient, viewerEmail, viewerPassword);
            var (viewerEmails, viewerTotal) = await ListEmailsAsync(viewerClient, token);

            Assert.Equal([ordinaryEmail], viewerEmails);
            Assert.Equal(1, viewerTotal);

            using var adminClient = CreateClient(factory);
            await LoginAsync(adminClient, adminEmail, adminPassword);
            var (adminEmails, adminTotal) = await ListEmailsAsync(adminClient, token);

            Assert.Equal(new[] { ordinaryEmail, superAdminEmail }.Order(StringComparer.Ordinal), adminEmails.Order(StringComparer.Ordinal));
            Assert.Equal(2, adminTotal);

            // A filter on the Super Admin role must not be a way round it either.
            using var byRole = await viewerClient.GetAsync(new Uri($"{UsersUrl}?search={token}&roleId={grantsAllRoleId}", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, byRole.StatusCode);
            var byRoleBody = await byRole.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal(0, byRoleBody.GetProperty("totalCount").GetInt32());
            Assert.Empty(byRoleBody.GetProperty("items").EnumerateArray());
        }
        finally
        {
            await RemoveFromGrantsAllRoleAsync(factory, superAdminId, adminId);
        }
    }

    /// <summary>
    /// The by-id half of defect B, and the one that matters most: a protected account and an id that does not
    /// exist must be the same answer. Compared on the wire (status and body), not just on the status code.
    /// </summary>
    [Fact]
    public async Task A_viewer_without_GrantsAll_gets_the_same_404_for_a_Super_Admin_as_for_an_id_that_does_not_exist()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var viewerRoleId = await CreateRoleAsync(factory, UniqueName("viewer-detail"), [Permissions.Users.View, Permissions.Users.Manage]);
        var grantsAllRoleId = await GetOrCreateGrantsAllRoleIdAsync(factory);

        var (viewerEmail, viewerPassword) = UniqueCredentials("viewer-detail");
        await CreateStaffUserWithRolesAsync(factory, viewerEmail, viewerPassword, [viewerRoleId]);
        var superAdminId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("superadmin-detail"), "Target-Pass-1!", [grantsAllRoleId]);

        var (adminEmail, adminPassword) = UniqueCredentials("admin-detail");
        var adminId = await CreateStaffUserWithRolesAsync(factory, adminEmail, adminPassword, [grantsAllRoleId]);

        try
        {
            using var viewerClient = CreateClient(factory);
            await LoginAsync(viewerClient, viewerEmail, viewerPassword);

            using var hidden = await viewerClient.GetAsync(new Uri($"{UsersUrl}/{superAdminId}", UriKind.Relative), TestContext.Current.CancellationToken);
            using var missing = await viewerClient.GetAsync(new Uri($"{UsersUrl}/2000000002", UriKind.Relative), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(missing.StatusCode, hidden.StatusCode);
            Assert.Equal(await BodyWithoutTraceIdAsync(missing), await BodyWithoutTraceIdAsync(hidden));
            Assert.Equal(missing.Content.Headers.ContentType?.ToString(), hidden.Content.Headers.ContentType?.ToString());

            // The same Super Admin is fully visible to a Super Admin, with the flag the viewer was never shown.
            using var adminClient = CreateClient(factory);
            await LoginAsync(adminClient, adminEmail, adminPassword);
            using var visible = await adminClient.GetAsync(new Uri($"{UsersUrl}/{superAdminId}", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
            var visibleBody = await visible.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.True(visibleBody.GetProperty("grantsAllPermissions").GetBoolean());
        }
        finally
        {
            await RemoveFromGrantsAllRoleAsync(factory, superAdminId, adminId);
        }
    }

    /// <summary>
    /// The role views: the GrantsAll role is not offered to a viewer who could never assign it, its detail is the
    /// same 404 as an unknown role, and a role's headcount does not include a Super Admin who also holds it
    /// (otherwise the count would reveal one).
    /// </summary>
    [Fact]
    public async Task A_viewer_without_GrantsAll_is_not_shown_the_GrantsAll_role_or_a_Super_Admin_in_a_role_count()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var viewerRoleId = await CreateRoleAsync(factory, UniqueName("viewer-roles"), [Permissions.Users.View, Permissions.Users.Manage]);
        var sharedRoleId = await CreateRoleAsync(factory, UniqueName("shared-role"), [Permissions.Orders.View]);
        var grantsAllRoleId = await GetOrCreateGrantsAllRoleIdAsync(factory);

        var (viewerEmail, viewerPassword) = UniqueCredentials("viewer-roles");
        await CreateStaffUserWithRolesAsync(factory, viewerEmail, viewerPassword, [viewerRoleId]);

        // One ordinary holder of the shared role, and one Super Admin who ALSO holds it.
        await CreateStaffUserWithRolesAsync(factory, UniqueEmail("ordinary-holder"), "Target-Pass-1!", [sharedRoleId]);
        var superAdminId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("superadmin-holder"), "Target-Pass-1!", [grantsAllRoleId, sharedRoleId]);

        var (adminEmail, adminPassword) = UniqueCredentials("admin-roles");
        var adminId = await CreateStaffUserWithRolesAsync(factory, adminEmail, adminPassword, [grantsAllRoleId]);

        try
        {
            using var viewerClient = CreateClient(factory);
            await LoginAsync(viewerClient, viewerEmail, viewerPassword);

            var viewerRoles = await GetRolesAsync(viewerClient);
            Assert.DoesNotContain(viewerRoles, role => role.GetProperty("grantsAllPermissions").GetBoolean());
            Assert.DoesNotContain(viewerRoles, role => role.GetProperty("id").GetInt32() == grantsAllRoleId);
            Assert.Equal(1, viewerRoles.Single(role => role.GetProperty("id").GetInt32() == sharedRoleId).GetProperty("userCount").GetInt32());

            using var hiddenRole = await viewerClient.GetAsync(new Uri($"/api/admin/roles/{grantsAllRoleId}", UriKind.Relative), TestContext.Current.CancellationToken);
            using var missingRole = await viewerClient.GetAsync(new Uri("/api/admin/roles/2000000003", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, missingRole.StatusCode);
            Assert.Equal(missingRole.StatusCode, hiddenRole.StatusCode);
            Assert.Equal(await BodyWithoutTraceIdAsync(missingRole), await BodyWithoutTraceIdAsync(hiddenRole));

            using var viewerSharedDetail = await viewerClient.GetAsync(new Uri($"/api/admin/roles/{sharedRoleId}", UriKind.Relative), TestContext.Current.CancellationToken);
            var viewerSharedBody = await viewerSharedDetail.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal(1, viewerSharedBody.GetProperty("userCount").GetInt32());

            // A Super Admin sees all of it.
            using var adminClient = CreateClient(factory);
            await LoginAsync(adminClient, adminEmail, adminPassword);
            var adminRoles = await GetRolesAsync(adminClient);
            Assert.Contains(adminRoles, role => role.GetProperty("id").GetInt32() == grantsAllRoleId);
            Assert.Equal(2, adminRoles.Single(role => role.GetProperty("id").GetInt32() == sharedRoleId).GetProperty("userCount").GetInt32());
        }
        finally
        {
            await RemoveFromGrantsAllRoleAsync(factory, superAdminId, adminId);
        }
    }

    /// <summary>
    /// The hiding in defect B must not become the leak defect A is (#101). Because a GrantsAll holder is excluded
    /// by the <c>WHERE</c> clause rather than fetched and then dropped, a by-id read of a protected account and one
    /// of an id that does not exist run the <b>same number of database commands</b> — so the two are
    /// indistinguishable by work done, not only by the bytes returned. Counted rather than timed: a count is what
    /// actually differs when this is got wrong, and it does not flake on a loaded machine.
    /// </summary>
    [Fact]
    public async Task A_hidden_Super_Admin_and_a_missing_id_cost_the_same_queries_so_hiding_adds_no_timing_oracle()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        var commands = new CommandCountingInterceptor();
        await using var factory = new ApiFactory(sqlServer.ConnectionString, commandInterceptor: commands);

        var viewerRoleId = await CreateRoleAsync(factory, UniqueName("viewer-plan"), [Permissions.Users.View, Permissions.Users.Manage]);
        var grantsAllRoleId = await GetOrCreateGrantsAllRoleIdAsync(factory);
        var (viewerEmail, viewerPassword) = UniqueCredentials("viewer-plan");
        await CreateStaffUserWithRolesAsync(factory, viewerEmail, viewerPassword, [viewerRoleId]);
        var superAdminId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("superadmin-plan"), "Target-Pass-1!", [grantsAllRoleId]);

        try
        {
            using var client = CreateClient(factory);
            await LoginAsync(client, viewerEmail, viewerPassword);

            // Warm up, so neither count includes the model build or the first session read.
            using (var warmup = await client.GetAsync(new Uri($"{UsersUrl}/2000000004", UriKind.Relative), TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NotFound, warmup.StatusCode);
            }

            commands.Reset();
            using (var hidden = await client.GetAsync(new Uri($"{UsersUrl}/{superAdminId}", UriKind.Relative), TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            }

            var hiddenCommands = commands.Count;

            commands.Reset();
            using (var missing = await client.GetAsync(new Uri($"{UsersUrl}/2000000005", UriKind.Relative), TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }

            var missingCommands = commands.Count;

            Assert.Equal(missingCommands, hiddenCommands);
        }
        finally
        {
            await RemoveFromGrantsAllRoleAsync(factory, superAdminId);
        }
    }

    /// <summary>
    /// Tester round 3, Major 2 — the tester's exact exploit, as a test. Hiding Super Admins from
    /// <c>GET /users</c> is worth nothing while the audit log names them, and the actor who can do this holds
    /// <b>only <c>AuditLog.View</c></b>: the seeded Store Manager's shape (doc 08 §9), so no role CRUD and no
    /// custom role is needed. The same request made by a Super Admin returns the row, which is what shows the
    /// filter is doing the work rather than the query simply matching nothing.
    /// </summary>
    [Fact]
    public async Task The_audit_log_never_names_a_Super_Admin_to_a_viewer_who_only_holds_AuditLogView()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, TestContext.Current.CancellationToken);

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var grantsAllRoleId = await GetOrCreateGrantsAllRoleIdAsync(factory);
        var ordinaryRoleId = await CreateRoleAsync(factory, UniqueName("audit-ordinary"), [Permissions.Orders.View]);

        // The acting Super Admin, who will promote someone through the real API so the row is a real one.
        var (adminEmail, adminPassword) = UniqueCredentials("audit-admin");
        var adminId = await CreateStaffUserWithRolesAsync(factory, adminEmail, adminPassword, [grantsAllRoleId]);

        // The target, who ends up holding GrantsAll, and a control target who never does.
        var promotedId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("audit-promoted"), "Target-Pass-1!", [ordinaryRoleId]);
        var controlId = await CreateStaffUserWithRolesAsync(factory, UniqueEmail("audit-control"), "Target-Pass-1!", [ordinaryRoleId]);

        // The viewer: AuditLog.View and nothing else, exactly like the seeded Store Manager.
        var auditViewerRoleId = await CreateRoleAsync(factory, UniqueName("audit-viewer"), [Permissions.AuditLog.View]);
        var (viewerEmail, viewerPassword) = UniqueCredentials("audit-viewer");
        await CreateStaffUserWithRolesAsync(factory, viewerEmail, viewerPassword, [auditViewerRoleId]);

        try
        {
            using var adminClient = CreateClient(factory);
            await LoginAsync(adminClient, adminEmail, adminPassword);

            // A real promotion and a real ordinary edit, so there is one row of each kind to find.
            using (var promote = await PutJsonAsync(adminClient, $"{UsersUrl}/{promotedId}/roles", new { roleIds = new[] { grantsAllRoleId } }, await GetAntiforgeryTokenAsync(adminClient)))
            {
                Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
            }

            using (var edit = await PutJsonAsync(adminClient, $"{UsersUrl}/{controlId}/roles", new { roleIds = new[] { ordinaryRoleId } }, await GetAntiforgeryTokenAsync(adminClient)))
            {
                Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            }

            using var viewerClient = CreateClient(factory);
            await LoginAsync(viewerClient, viewerEmail, viewerPassword);

            // The viewer cannot read the promoted user directly...
            using (var denied = await viewerClient.GetAsync(new Uri($"{UsersUrl}/{promotedId}", UriKind.Relative), TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }

            // ...and must not be able to read the same fact out of the audit log instead.
            var promotedRows = await AuditRowsAsync(viewerClient, promotedId);
            Assert.Empty(promotedRows);

            // Nor by naming the acting Super Admin, nor by asking for the action unfiltered.
            Assert.Empty(await AuditRowsAsync(viewerClient, adminId));
            var everything = await AuditPageAsync(viewerClient, $"/api/admin/audit-logs?action={AuditActions.PermissionChange}&pageSize=100");
            Assert.DoesNotContain(
                everything.GetProperty("items").EnumerateArray(),
                row => (row.GetProperty("afterJson").GetString() ?? string.Empty).Contains("Super Admin", StringComparison.Ordinal));
            Assert.DoesNotContain(
                everything.GetProperty("items").EnumerateArray(),
                row => row.GetProperty("userEmail").GetString() == adminEmail);

            // The control row — an ordinary user edited by the same Super Admin — is hidden too, because its
            // actor is a holder. That is the cost of the rule and is asserted so it is a decision, not a surprise.
            Assert.Empty(await AuditRowsAsync(viewerClient, controlId));

            // A Super Admin sees all of it, which is what proves the rows exist at all.
            var adminSees = await AuditRowsAsync(adminClient, promotedId);
            Assert.Contains(adminSees, row => (row.GetProperty("afterJson").GetString() ?? string.Empty).Contains("Super Admin", StringComparison.Ordinal));
        }
        finally
        {
            await RemoveFromGrantsAllRoleAsync(factory, adminId, promotedId);
        }
    }

    private static async Task<List<JsonElement>> AuditRowsAsync(HttpClient client, int subjectUserId)
    {
        var body = await AuditPageAsync(client, $"/api/admin/audit-logs?entityType=User&entityId={subjectUserId}&pageSize=100");
        var items = body.GetProperty("items").EnumerateArray().ToList();

        // The total must agree with the page, or the count itself becomes the oracle.
        Assert.Equal(items.Count, body.GetProperty("totalCount").GetInt32());
        return items;
    }

    private static async Task<JsonElement> AuditPageAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The response body with its per-request <c>traceId</c> removed, as sorted <c>name=value</c> pairs, so two
    /// responses compare equal exactly when everything the caller could use to tell them apart is equal.
    /// </summary>
    private static async Task<string> BodyWithoutTraceIdAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var body = JsonSerializer.Deserialize<JsonElement>(text);
        return string.Join(
            ";",
            body.EnumerateObject()
                .Where(property => !string.Equals(property.Name, "traceId", StringComparison.Ordinal))
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => $"{property.Name}={property.Value.GetRawText()}"));
    }

    private static async Task<(List<string> Emails, int Total)> ListEmailsAsync(HttpClient client, string search)
    {
        using var response = await client.GetAsync(new Uri($"{UsersUrl}?search={search}&pageSize=100", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var emails = body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("email").GetString()!).ToList();
        return (emails, body.GetProperty("totalCount").GetInt32());
    }

    private static async Task<List<JsonElement>> GetRolesAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/api/admin/roles", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.EnumerateArray().ToList();
    }

    private static HttpClient CreateClient(ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static async Task LoginAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync(LoginUrl, new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // W1-10: a Super Admin without a second factor is refused on every endpoint but the enrolment ones, so a
        // test that needs a Super Admin actor has to do what the product makes them do. Enrolling through the same
        // two endpoints the enrolment page calls keeps these tests running behind the real gate, not around it.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        if (body.TryGetProperty("twoFactorEnrolmentRequired", out var enrolmentRequired) && enrolmentRequired.GetBoolean())
        {
            await EnrolTwoFactorAsync(client);
        }
    }

    private static async Task EnrolTwoFactorAsync(HttpClient client)
    {
        using var setup = await PostAsync(client, TwoFactorSetupUrl, await GetAntiforgeryTokenAsync(client));
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var setupBody = await setup.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var key = TotpTestCodes.SecretFromUri(setupBody.GetProperty("authenticatorUri").GetString()!);

        using var enableRequest = new HttpRequestMessage(HttpMethod.Post, TwoFactorEnableUrl)
        {
            Content = JsonContent.Create(new { code = TotpTestCodes.Current(key) }),
        };
        enableRequest.Headers.Add("X-XSRF-TOKEN", await GetAntiforgeryTokenAsync(client));
        using var enable = await client.SendAsync(enableRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
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

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, object body, string antiforgeryToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
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

    /// <summary>
    /// Mirrors the API's serialized <c>RbacDenialReason</c> enum names, so test assertions read clearly without
    /// a project reference to the Application layer's internal enum from this file. Tester-round-1 fix (W1-08,
    /// #8, defect 1) removed <c>TargetIsOrWouldBecomeGrantsAll</c> from the real enum entirely — do not add it
    /// back here; see the tests below that specifically prove a non-GrantsAll actor can no longer tell that
    /// case apart from <see cref="NotSubsetOfActorPermissions"/>.
    /// </summary>
    private enum RbacDenialReasonMirror
    {
        NotAuthorized,
        SelfEditNotAllowed,
        NotSubsetOfActorPermissions,
        EmailChangeRequiresGrantsAll,
    }
}
