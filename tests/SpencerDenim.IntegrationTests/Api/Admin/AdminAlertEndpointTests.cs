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
using SpencerDenim.Domain.Ops;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// <c>/api/admin/alerts</c> end to end against a real SQL Server (doc 08 §3.8 N2, R10; doc 11 §7 G-37).
/// <para>
/// <b>The case this file exists for is a negative one.</b> G-37 says the dead-email alert is for Super
/// Admins and that "the visibility rule in 08 §3.8 R10 gives no other role a path to it" — so the test
/// that matters is an <c>Orders.Approve</c> holder, who <i>does</i> have a path to the payment alerts,
/// being shown the payment alert and <b>not</b> the dead email, and being unable to acknowledge it
/// either. A visibility rule is only proved by the thing it refuses.
/// </para>
/// <para>
/// <b>It also pins the payload</b> (issue #126). Playwright never starts this API: the browser tests run
/// against a Node stub, so a field dropped from this DTO cannot fail a screen test. The field names here
/// and in <c>AdminAlertShapeTests</c> are the pair that catches it.
/// </para>
/// <para>
/// <b>There is no GrantsAll case here, deliberately.</b> A GrantsAll holder cannot reach any admin
/// endpoint until they have enrolled in two-factor authentication (W1-10, doc 08 §3.1 N14), so signing
/// one in means driving the whole enrolment flow — which is <c>AdminTwoFactorEndpointTests</c>' subject,
/// not this one's. What a Super Admin sees is proved where the rule lives
/// (<c>AdminAlertVisibilityTests</c>), where it is applied (<c>AdminAlertHandlerTests</c>) and against
/// the real database (<c>AdminAlertsBehaviourTests</c>).
/// </para>
/// </summary>
public sealed class AdminAlertEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string AlertsUrl = "/api/admin/alerts";
    private const string LoginUrl = "/api/admin/auth/login";
    private const string AntiforgeryUrl = "/api/admin/auth/antiforgery-token";
    private const string Password = "Correct-Password-1!";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unauthenticated_request_is_refused()
    {
        await MigrateAsync();

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(new Uri(AlertsUrl, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A signed-in staff member R10 gives no audience to cannot reach the route at all.
    /// </summary>
    /// <remarks>
    /// The route guard, not the read model: a user with no alert audience would get an empty list from
    /// the use case anyway, and the guard is what stops them asking in the first place. Both halves are
    /// deliberate — the guard is coarse (any audience) and the read model is per type (doc 08 §3.8 R10),
    /// which is the same arrangement as <c>GET /api/admin/settings</c>.
    /// </remarks>
    [Fact]
    public async Task A_staff_member_with_no_alert_audience_is_forbidden()
    {
        await MigrateAsync();

        var email = UniqueEmail("no-alerts");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, [Permissions.Orders.View, Permissions.Settings.Edit]);
        using var client = CreateClient(factory);

        await SignInAsync(client, email);

        using var response = await client.GetAsync(new Uri(AlertsUrl, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// <b>An <c>Orders.Approve</c> holder sees the payment alert and never the dead email</b>
    /// (doc 08 §3.8 R10, doc 11 §7 G-37).
    /// </summary>
    [Fact]
    public async Task An_order_staff_member_sees_the_payment_alert_and_not_the_dead_email()
    {
        await MigrateAsync();

        var email = UniqueEmail("order-staff");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, [Permissions.Orders.Approve]);

        await ResetAlertsAsync(factory);
        var payment = await InsertAlertAsync(factory, AdminAlertTypes.PaymentException, "A payment exception");
        var dead = await InsertAlertAsync(factory, AdminAlertTypes.EmailDeliveryFailed, "A dead email");
        await InsertAlertAsync(factory, AdminAlertTypes.BreakGlassUsed, "The recovery CLI was used");

        using var client = CreateClient(factory);
        await SignInAsync(client, email);

        using var response = await client.GetAsync(new Uri(AlertsUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var alerts = body.GetProperty("alerts").EnumerateArray().ToList();

        var only = Assert.Single(alerts);
        Assert.Equal(payment, only.GetProperty("id").GetInt32());
        Assert.Equal(AdminAlertTypes.PaymentException, only.GetProperty("alertType").GetString());

        Assert.DoesNotContain(dead, alerts.Select(entry => entry.GetProperty("id").GetInt32()));
        Assert.False(body.GetProperty("moreThanShown").GetBoolean());

        // A staff work list naming orders and payment problems, and the answer differs per user, so it
        // must never be stored by a browser or an intermediary. **Asserted flag by flag rather than as a
        // string**: `HttpResponseHeaders.CacheControl` is a parsed value and its `ToString` does not
        // round-trip the directives in the order they were written.
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Contains("Cookie", response.Headers.Vary);
    }

    /// <summary>
    /// The payload carries exactly the fields the admin app's own interface declares.
    /// </summary>
    /// <remarks>
    /// Issue #126's pair: <c>AdminAlertShapeTests</c> reads the TypeScript and the Playwright stub,
    /// and this reads the wire. Camel-cased by the API's JSON options, which is what the screen indexes
    /// by.
    /// </remarks>
    [Fact]
    public async Task The_payload_carries_the_fields_the_admin_app_declares()
    {
        await MigrateAsync();

        var email = UniqueEmail("payload");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, [Permissions.Orders.Refund]);

        await ResetAlertsAsync(factory);
        await InsertAlertAsync(
            factory,
            AdminAlertTypes.PaymentException,
            "A payment exception",
            detailsJson: "{\"templateCode\":\"OrderPlaced\","
                + "\"orderPublicId\":\"44444444-4444-4444-8444-444444444444\","
                + "\"orderNumber\":\"SD-2026-000123\"}");

        using var client = CreateClient(factory);
        await SignInAsync(client, email);

        using var response = await client.GetAsync(new Uri(AlertsUrl, UriKind.Relative), Token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);

        Assert.Equal(
            ["alerts", "moreThanShown"],
            body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        var alert = body.GetProperty("alerts").EnumerateArray().Single();

        Assert.Equal(
            [
                "alertType",
                "createdAtUtc",
                "id",
                "orderNumber",
                "orderPublicId",
                "relatedEntityId",
                "relatedEntityType",
                "severity",
                "templateCode",
                "title",
            ],
            alert.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        // The order link and the template code really are lifted out of DetailsJson, which is the one
        // transformation between the row and the screen. The template code is what the banner's advice
        // branches on (doc 11 §4 gives a dead StaffInvite a different remedy from a dead PasswordReset).
        Assert.Equal("44444444-4444-4444-8444-444444444444", alert.GetProperty("orderPublicId").GetString());
        Assert.Equal("SD-2026-000123", alert.GetProperty("orderNumber").GetString());
        Assert.Equal("OrderPlaced", alert.GetProperty("templateCode").GetString());

        // And DetailsJson itself is not on the wire: six of the seven alert types are later tasks' and a
        // screen cannot render a shape it has never met.
        Assert.False(alert.TryGetProperty("detailsJson", out _));
    }

    /// <summary>
    /// <b>An <c>Orders.Approve</c> holder cannot acknowledge a dead-email alert, and gets the same answer
    /// as for an id that does not exist.</b>
    /// </summary>
    /// <remarks>
    /// Two answers would make the route a way to count the shop's security alerts: a 403 on a real id and
    /// a 404 on a made-up one is a yes/no oracle over the whole table.
    /// </remarks>
    [Fact]
    public async Task An_order_staff_member_cannot_acknowledge_a_dead_email_alert()
    {
        await MigrateAsync();

        var email = UniqueEmail("refuser");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, [Permissions.Orders.Approve]);

        await ResetAlertsAsync(factory);
        var dead = await InsertAlertAsync(factory, AdminAlertTypes.EmailDeliveryFailed, "A dead email");

        using var client = CreateClient(factory);
        await SignInAsync(client, email);

        using var refused = await AcknowledgeAsync(client, dead);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        using var missing = await AcknowledgeAsync(client, int.MaxValue);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Null(
            (await dbContext.AdminAlerts.AsNoTracking().SingleAsync(alert => alert.Id == dead, Token))
                .AcknowledgedAtUtc);
    }

    /// <summary>
    /// Acknowledging an alert they may see stamps the row, writes the audit trail, and answers the time.
    /// </summary>
    [Fact]
    public async Task Acknowledging_an_alert_they_may_see_records_it_with_its_audit_row()
    {
        await MigrateAsync();

        var email = UniqueEmail("acknowledger");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        var userId = await CreateStaffUserAsync(factory, email, [Permissions.Orders.Approve]);

        await ResetAlertsAsync(factory);
        var payment = await InsertAlertAsync(factory, AdminAlertTypes.PaymentException, "A payment exception");

        using var client = CreateClient(factory);
        await SignInAsync(client, email);

        using var response = await AcknowledgeAsync(client, payment);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);

        Assert.Equal(
            ["acknowledgedAtUtc", "alertId", "alertType", "alreadyAcknowledged"],
            body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        Assert.Equal(payment, body.GetProperty("alertId").GetInt32());
        Assert.False(body.GetProperty("alreadyAcknowledged").GetBoolean());

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await dbContext.AdminAlerts.AsNoTracking().SingleAsync(alert => alert.Id == payment, Token);
        Assert.Equal(userId, row.AcknowledgedByUserId);

        // Nothing writes the note (owner's decision, 2026-10-09) — see `AdminAlertAcknowledger`.
        Assert.Null(row.AcknowledgementNote);

        var trail = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(entry => entry.Action == AuditActions.AlertAcknowledged
                && entry.EntityId == payment.ToString(CultureInfo.InvariantCulture))
            .ToListAsync(Token);

        var entry = Assert.Single(trail);
        Assert.Equal(userId, entry.UserId);
        Assert.Equal(email, entry.UserEmail);

        // The banner is gone for this user on the next load, which is what "until one of them
        // acknowledges it" means.
        using var reload = await client.GetAsync(new Uri(AlertsUrl, UriKind.Relative), Token);
        var banner = await reload.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Empty(banner.GetProperty("alerts").EnumerateArray());
    }

    /// <summary>
    /// A second acknowledgement is a 200 saying somebody else got there first, not a refusal.
    /// </summary>
    [Fact]
    public async Task A_second_acknowledgement_says_somebody_else_got_there_first()
    {
        await MigrateAsync();

        var email = UniqueEmail("second");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, [Permissions.Orders.Approve]);

        await ResetAlertsAsync(factory);
        var payment = await InsertAlertAsync(factory, AdminAlertTypes.PaymentException, "A payment exception");

        using var client = CreateClient(factory);
        await SignInAsync(client, email);

        using var first = await AcknowledgeAsync(client, payment);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await AcknowledgeAsync(client, payment);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.True(body.GetProperty("alreadyAcknowledged").GetBoolean());
    }

    /// <summary>
    /// <b>An acknowledgement note is refused by name, and the alert is not acknowledged either.</b>
    /// </summary>
    /// <remarks>
    /// Owner's decision, 2026-10-09: doc 08 §3.8 defines <c>AcknowledgementNote</c> and the migration
    /// creates it, and nothing in this build writes it, because <c>ops.AdminAlerts</c> is not in doc 08
    /// §3.1 step 2's anonymisation deletion list and doc 11 §4 says these rows are never scrubbed.
    /// <para>
    /// <b>Refused rather than accepted and dropped, and that is what this test is for.</b> A route that
    /// took the field and ignored it would be the shape of promise this project has already shipped
    /// twice — the caller believes their sentence was kept. Both halves are asserted: the 400 names the
    /// field, and the row is still open afterwards, so a refused request cannot half-apply.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_acknowledgement_note_is_refused_by_name_and_nothing_is_acknowledged()
    {
        await MigrateAsync();

        var email = UniqueEmail("with-note");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, [Permissions.Orders.Approve]);

        await ResetAlertsAsync(factory);
        var payment = await InsertAlertAsync(factory, AdminAlertTypes.PaymentException, "A payment exception");

        using var client = CreateClient(factory);
        await SignInAsync(client, email);

        using var response = await AcknowledgeAsync(client, payment, note: "Rang them on 0771234567");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var errors = body.GetProperty("errors");
        Assert.Contains("note", errors.EnumerateObject().Select(field => field.Name));
        Assert.Contains(
            "does not store an acknowledgement note",
            errors.GetProperty("note")[0].GetString()!,
            StringComparison.Ordinal);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await dbContext.AdminAlerts.AsNoTracking().SingleAsync(alert => alert.Id == payment, Token);

        Assert.Null(row.AcknowledgedAtUtc);
        Assert.Null(row.AcknowledgementNote);
    }

    /// <summary>An explicit <c>null</c> note is accepted: that is what "no note" looks like on the wire.</summary>
    [Fact]
    public async Task An_explicit_null_note_is_accepted()
    {
        await MigrateAsync();

        var email = UniqueEmail("null-note");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, [Permissions.Orders.Approve]);

        await ResetAlertsAsync(factory);
        var payment = await InsertAlertAsync(factory, AdminAlertTypes.PaymentException, "A payment exception");

        using var client = CreateClient(factory);
        await SignInAsync(client, email);

        using var response = await AcknowledgeAsync(client, payment, note: null, sendNoteField: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The acknowledge route validates the antiforgery token, like every other mutating admin route.
    /// </summary>
    /// <remarks>
    /// Without this the screen could stop sending the header and every test above would still pass —
    /// which is the hole W1-12's stub review found in the browser suite and the reason the stub enforces
    /// it too.
    /// </remarks>
    [Fact]
    public async Task Acknowledging_without_the_antiforgery_token_is_refused()
    {
        await MigrateAsync();

        var email = UniqueEmail("no-token");
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        await CreateStaffUserAsync(factory, email, [Permissions.Orders.Approve]);

        await ResetAlertsAsync(factory);
        var payment = await InsertAlertAsync(factory, AdminAlertTypes.PaymentException, "A payment exception");

        using var client = CreateClient(factory);
        await SignInAsync(client, email);

        using var content = JsonContent.Create(new { note = (string?)null });
        using var response = await client.PostAsync(
            new Uri($"{AlertsUrl}/{payment}/acknowledge", UriKind.Relative),
            content,
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Null(
            (await dbContext.AdminAlerts.AsNoTracking().SingleAsync(alert => alert.Id == payment, Token))
                .AcknowledgedAtUtc);
    }

    // ------------------------------------------------------------------- helpers

    private async Task MigrateAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);
    }

    private static HttpClient CreateClient(ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

    private static string UniqueEmail(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}@example.invalid";

    private static async Task SignInAsync(HttpClient client, string email)
    {
        using var login = await client.PostAsJsonAsync(LoginUrl, new { email, password = Password }, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    /// <summary>
    /// Acknowledges through the route, with an empty body unless a test is probing the note refusal.
    /// </summary>
    /// <param name="note">The note to send. Only sent when <paramref name="sendNoteField"/> is true.</param>
    /// <param name="sendNoteField">
    /// Whether to include the field at all. A route that refuses a field has to be tested both ways: with
    /// it absent (the normal case) and with it present and null (which must still be accepted).
    /// </param>
    private static async Task<HttpResponseMessage> AcknowledgeAsync(
        HttpClient client,
        int alertId,
        string? note = null,
        bool sendNoteField = false)
    {
        using var tokenResponse = await client.GetAsync(new Uri(AntiforgeryUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);

        var antiforgery = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("token")
            .GetString();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{AlertsUrl}/{alertId}/acknowledge", UriKind.Relative))
        {
            Content = note is null && !sendNoteField
                ? JsonContent.Create(new { })
                : JsonContent.Create(new { note }),
        };

        request.Headers.Add("X-XSRF-TOKEN", antiforgery);

        return await client.SendAsync(request, Token);
    }

    /// <summary>
    /// Empties <c>ops.AdminAlerts</c>, so an assertion about what one user sees is exact.
    /// </summary>
    /// <remarks>
    /// The assembly shares one throwaway database and other classes seed this table
    /// (<c>AdminAlertsPlanTests</c> fills it with twenty thousand rows), so narrowing by a marker is not
    /// enough: the cap is ten and a neighbour's rows would be the ten. Doc 08 §1.6's never-deleted list
    /// does not include this table, and §8.4 gives it a purge.
    /// </remarks>
    private static async Task ResetAlertsAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM ops.AdminAlerts;", Token);
    }

    private static async Task<int> InsertAlertAsync(
        ApiFactory factory,
        string alertType,
        string title,
        string? detailsJson = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var alert = new AdminAlert
        {
            AlertType = alertType,
            Severity = AdminAlertSeverity.Warning,
            Title = title,
            DetailsJson = detailsJson,
            RelatedEntityType = AdminAlert.EmailOutboxEntityType,
            RelatedEntityId = "1",
            CreatedAtUtc = DateTime.UtcNow,
        };

        dbContext.AdminAlerts.Add(alert);
        await dbContext.SaveChangesAsync(Token);

        return alert.Id;
    }

    /// <summary>
    /// A staff user in a role granting exactly <paramref name="permissions"/> and nothing else.
    /// </summary>
    /// <remarks>
    /// The shape <c>AuditLogEndpointTests</c> established: a role that exists and grants only what the
    /// test names, so a passing assertion is about the permission and not about being signed in.
    /// </remarks>
    private static async Task<int> CreateStaffUserAsync(
        ApiFactory factory,
        string email,
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
            FirstName = "Alert",
            LastName = "Reader",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var created = await userManager.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));

        var roleName = $"role-{Guid.NewGuid():N}";
        var role = new AppRole
        {
            Name = roleName,
            Description = "Test role (W4-02a admin alert endpoint tests)",
            IsSystem = false,
            GrantsAllPermissions = false,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var roleCreated = await roleManager.CreateAsync(role);
        Assert.True(
            roleCreated.Succeeded,
            string.Join("; ", roleCreated.Errors.Select(error => error.Description)));

        foreach (var permission in permissions)
        {
            var claim = await roleManager.AddClaimAsync(
                role,
                new System.Security.Claims.Claim(RoleClaimConfiguration.PermissionClaimType, permission));

            Assert.True(claim.Succeeded, string.Join("; ", claim.Errors.Select(error => error.Description)));
        }

        var assigned = await userManager.AddToRoleAsync(user, roleName);
        Assert.True(assigned.Succeeded, string.Join("; ", assigned.Errors.Select(error => error.Description)));

        return user.Id;
    }
}
