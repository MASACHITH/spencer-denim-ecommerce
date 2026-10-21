using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Identity.Seeding;
using SpencerDenim.Infrastructure.Ops.Email;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Ops;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The email account panel end to end against SQL Server (ADM-22, 2026-10-21; issue #126's rule that every new route
/// has an endpoint test): who may save and test it, the audit trail with no secret in it, the password never returned,
/// the precedence over <c>Email__Smtp__*</c>, doc 08 §3.8 N2's dual send through a fake SMTP sink that records which
/// account each message went through, and the outbox using the new account.
/// <para>
/// <b>Run in <see cref="OutboxWipingTests"/>, on its own, and every test clears the account again in a
/// <c>finally</c>.</b> A complete account in the shared database would give every other test host something to send
/// with; and each host here has its <c>SendEmails</c> job removed, so a sweep only happens when a test runs one, into
/// a sink that never connects.
/// </para>
/// </summary>
[Collection(nameof(OutboxWipingTests))]
public sealed class AdminSmtpSettingsEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string SettingsUrl = "/api/admin/settings";
    private const string SmtpUrl = "/api/admin/settings/email-smtp";
    private const string TestUrl = "/api/admin/settings/email-smtp/test";
    private const string Password = "Correct-Horse-Battery-Staple-1!";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static object AccountA(string? rowVersion) => new
    {
        host = "smtp.account-a.test",
        port = "587",
        username = "user-a",
        password = "password-a-plaintext",
        fromAddress = "shop-a@shop.test",
        fromName = "Colombo Wardrobe",
        useStartTls = true,
        rowVersion,
    };

    private static object AccountB(string? rowVersion) => new
    {
        host = "smtp.account-b.test",
        port = "2525",
        username = "user-b",
        password = "password-b-plaintext",
        fromAddress = "shop-b@shop.test",
        fromName = "Colombo Wardrobe",
        useStartTls = true,
        rowVersion,
    };

    // ---- who may use it ------------------------------------------------------------------------------------

    [Fact]
    public async Task Both_routes_refuse_an_anonymous_caller()
    {
        await using var world = await StartAsync();
        using var client = world.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var save = await client.PutAsJsonAsync(new Uri(SmtpUrl, UriKind.Relative), AccountA(null), JsonOptions, Token);
        using var test = await client.PostAsJsonAsync(new Uri(TestUrl, UriKind.Relative), AccountA(null), JsonOptions, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, save.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, test.StatusCode);
    }

    /// <summary>
    /// Every default role below Super Admin, and a holder of every other settings permission, is refused at both routes
    /// and does not even see the group (<c>Settings.Security</c> cannot be granted to a role at all).
    /// </summary>
    [Theory]
    [InlineData(DefaultRoleDefinitions.StoreManagerName)]
    [InlineData(DefaultRoleDefinitions.OrderStaffName)]
    [InlineData(DefaultRoleDefinitions.ContentEditorName)]
    [InlineData("Settings.Edit and Settings.Payments")]
    public async Task Every_role_without_settings_security_is_refused_and_does_not_see_the_group(string role)
    {
        await using var world = await StartAsync();

        var permissions = role switch
        {
            DefaultRoleDefinitions.StoreManagerName => DefaultRoleDefinitions.StoreManager.Permissions,
            DefaultRoleDefinitions.OrderStaffName => DefaultRoleDefinitions.OrderStaff.Permissions,
            DefaultRoleDefinitions.ContentEditorName => DefaultRoleDefinitions.ContentEditor.Permissions,
            _ => (IReadOnlySet<string>)new HashSet<string> { Permissions.Settings.Edit, Permissions.Settings.Payments },
        };

        using var client = await MediaTestHost.SignInAsync(world.Factory, [.. permissions]);

        try
        {
            using var save = await client.PutAsJsonAsync(new Uri(SmtpUrl, UriKind.Relative), AccountA(await VersionAsync(world)), JsonOptions, Token);
            using var test = await client.PostAsJsonAsync(new Uri(TestUrl, UriKind.Relative), AccountA(null), JsonOptions, Token);

            Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, test.StatusCode);
            Assert.Null(await ValueAsync(world, SettingKeys.Smtp.Host));
            Assert.Empty(world.Sink.Sent);

            using var read = await client.GetAsync(new Uri(SettingsUrl, UriKind.Relative), Token);

            if (read.StatusCode == HttpStatusCode.OK)
            {
                var body = await read.Content.ReadAsStringAsync(Token);
                Assert.DoesNotContain("Email.Smtp", body, StringComparison.Ordinal);
            }
        }
        finally
        {
            await ClearAccountAsync(world);
        }
    }

    // ---- save, read, audit, alert, dual send -----------------------------------------------------------------

    [Fact]
    public async Task A_super_admin_saves_the_account_the_password_is_encrypted_never_returned_and_audited_as_secret_changed()
    {
        await using var world = await StartAsync();
        var (client, superAdmin) = await SignInSuperAdminAsync(world);

        try
        {
            var since = DateTime.UtcNow.AddSeconds(-5);

            using var save = await client.PutAsJsonAsync(new Uri(SmtpUrl, UriKind.Relative), AccountA(await VersionAsync(world)), JsonOptions, Token);
            Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);

            Assert.Equal("smtp.account-a.test", await ValueAsync(world, SettingKeys.Smtp.Host));
            Assert.Equal("587", await ValueAsync(world, SettingKeys.Smtp.Port));
            Assert.Equal("true", await ValueAsync(world, SettingKeys.Smtp.UseStartTls));

            // Ciphertext at rest, under the settings purpose.
            var stored = await ValueAsync(world, SettingKeys.Smtp.Password);
            Assert.NotNull(stored);
            Assert.DoesNotContain("password-a-plaintext", stored, StringComparison.Ordinal);
            await using (var scope = world.Factory.Services.CreateAsyncScope())
            {
                Assert.True(scope.ServiceProvider.GetRequiredService<ISettingSecretProtector>().TryUnprotect(stored, out var plain));
                Assert.Equal("password-a-plaintext", plain);
            }

            // The read: the group is there for a Super Admin, the password only as "set".
            using var read = await client.GetAsync(new Uri(SettingsUrl, UriKind.Relative), Token);
            var payload = await read.Content.ReadAsStringAsync(Token);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.DoesNotContain("password-a-plaintext", payload, StringComparison.Ordinal);
            Assert.DoesNotContain(stored, payload, StringComparison.Ordinal);
            var password = SettingOf(payload, SettingKeys.Smtp.Password);
            Assert.Equal(JsonValueKind.Null, password.GetProperty("value").ValueKind);
            Assert.True(password.GetProperty("hasValue").GetBoolean());
            Assert.True(password.GetProperty("isSecret").GetBoolean());

            // One audit row per key; the password as "[secret changed]"; no plaintext or ciphertext anywhere.
            await using (var scope = world.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var audits = await db.AuditLogs.AsNoTracking()
                    .Where(row => row.UserId == superAdmin && row.EntityType == SettingsAuditTrail.EntityType && row.OccurredAtUtc >= since)
                    .ToListAsync(Token);

                Assert.Equal(7, audits.Count);
                var passwordAudit = Assert.Single(audits, row => row.EntityId == SettingKeys.Smtp.Password);
                Assert.Contains(SettingsAuditTrail.RedactedSecret, passwordAudit.AfterJson, StringComparison.Ordinal);
                Assert.All(audits, row =>
                {
                    Assert.DoesNotContain("password-a-plaintext", row.AfterJson ?? string.Empty, StringComparison.Ordinal);
                    Assert.DoesNotContain(stored, row.AfterJson ?? string.Empty, StringComparison.Ordinal);
                });

                // The banner: keys only.
                var alert = await db.AdminAlerts.AsNoTracking()
                    .Where(row => row.AlertType == AdminAlertTypes.SensitiveSettingsChanged && row.CreatedAtUtc >= since)
                    .OrderByDescending(row => row.Id)
                    .FirstAsync(Token);
                Assert.Contains(SettingKeys.Smtp.Password, alert.DetailsJson, StringComparison.Ordinal);
                Assert.DoesNotContain("password-a-plaintext", alert.DetailsJson, StringComparison.Ordinal);
                Assert.DoesNotContain("smtp.account-a.test", alert.DetailsJson, StringComparison.Ordinal);
            }

            // No account before this save (configuration blank, rows empty): only the new-account copy.
            var mine = await OutboxForAsync(world, superAdmin);
            var only = Assert.Single(mine);
            Assert.Null(only.TransportOverrideProtected);
            Assert.DoesNotContain("password-a-plaintext", only.BodyText!, StringComparison.Ordinal);
        }
        finally
        {
            await ClearAccountAsync(world);
            client.Dispose();
        }
    }

    /// <summary>
    /// Doc 08 §3.8 N2 end to end: changing A to B queues, for every Super Admin, one copy through B and one carrying A;
    /// a sweep sends the first through B and the second through A — and ordinary mail through B.
    /// </summary>
    [Fact]
    public async Task Changing_the_account_alerts_through_the_new_and_the_old_account_and_the_outbox_uses_the_new_one()
    {
        await using var world = await StartAsync();
        var (client, superAdmin) = await SignInSuperAdminAsync(world);

        try
        {
            using (var first = await client.PutAsJsonAsync(new Uri(SmtpUrl, UriKind.Relative), AccountA(await VersionAsync(world)), JsonOptions, Token))
            {
                Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
            }

            await DeleteOutboxAsync(world);

            using (var second = await client.PutAsJsonAsync(new Uri(SmtpUrl, UriKind.Relative), AccountB(await VersionAsync(world)), JsonOptions, Token))
            {
                Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
            }

            var mine = await OutboxForAsync(world, superAdmin);
            Assert.Equal(2, mine.Count);
            var carried = Assert.Single(mine, row => row.TransportOverrideProtected is not null);
            Assert.DoesNotContain("password-a-plaintext", carried.TransportOverrideProtected!, StringComparison.Ordinal);

            var superAdminEmail = await EmailOfAsync(world, superAdmin);
            await EnqueueOrdinaryAsync(world, "customer@shop.test");

            await SweepAsync(world);

            var toAdmin = world.Sink.Sent.Where(sent => sent.Email.ToEmail == superAdminEmail).ToList();
            Assert.Equal(2, toAdmin.Count);
            Assert.Single(toAdmin, sent => sent.Account.Host == "smtp.account-b.test" && sent.Account.Password == "password-b-plaintext");
            Assert.Single(toAdmin, sent => sent.Account.Host == "smtp.account-a.test" && sent.Account.Password == "password-a-plaintext");

            var customer = Assert.Single(world.Sink.Sent, sent => sent.Email.ToEmail == "customer@shop.test");
            Assert.Equal("smtp.account-b.test", customer.Account.Host);
            Assert.Equal(2525, customer.Account.Port);

            // R5: the carried ciphertext does not outlive the message.
            Assert.All(await OutboxForAsync(world, superAdmin), row =>
            {
                Assert.Equal(EmailOutboxStatus.Sent, row.Status);
                Assert.Null(row.TransportOverrideProtected);
            });
        }
        finally
        {
            await ClearAccountAsync(world);
            client.Dispose();
        }
    }

    /// <summary>
    /// A hijacker who <i>clears</i> the account leaves nothing to send through; the copy carrying the previous account
    /// still goes out through it, and ordinary mail waits untouched.
    /// </summary>
    [Fact]
    public async Task Clearing_the_account_still_sends_the_warning_through_the_previous_one_while_ordinary_mail_waits()
    {
        await using var world = await StartAsync();
        var (client, superAdmin) = await SignInSuperAdminAsync(world);

        try
        {
            using (var first = await client.PutAsJsonAsync(new Uri(SmtpUrl, UriKind.Relative), AccountA(await VersionAsync(world)), JsonOptions, Token))
            {
                Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
            }

            await DeleteOutboxAsync(world);

            using (var cleared = await client.PutAsJsonAsync(
                new Uri(SmtpUrl, UriKind.Relative),
                new { host = (string?)null, rowVersion = await VersionAsync(world) },
                JsonOptions,
                Token))
            {
                Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
            }

            await EnqueueOrdinaryAsync(world, "waiting@shop.test");
            await SweepAsync(world);

            var sent = Assert.Single(world.Sink.Sent);
            Assert.Equal(await EmailOfAsync(world, superAdmin), sent.Email.ToEmail);
            Assert.Equal("smtp.account-a.test", sent.Account.Host);

            await using var scope = world.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var waiting = await db.EmailOutbox.AsNoTracking().SingleAsync(row => row.ToEmail == "waiting@shop.test", Token);
            Assert.Equal(EmailOutboxStatus.Pending, waiting.Status);
            Assert.Equal(0, waiting.AttemptCount);
        }
        finally
        {
            await ClearAccountAsync(world);
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_blank_password_keeps_the_saved_one_and_a_bad_body_is_refused_by_field()
    {
        await using var world = await StartAsync();
        var (client, _) = await SignInSuperAdminAsync(world);

        try
        {
            using (var first = await client.PutAsJsonAsync(new Uri(SmtpUrl, UriKind.Relative), AccountA(await VersionAsync(world)), JsonOptions, Token))
            {
                Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
            }

            var stored = await ValueAsync(world, SettingKeys.Smtp.Password);

            using (var keep = await client.PutAsJsonAsync(
                new Uri(SmtpUrl, UriKind.Relative),
                new { password = "", fromName = "Renamed", rowVersion = await VersionAsync(world) },
                JsonOptions,
                Token))
            {
                Assert.Equal(HttpStatusCode.NoContent, keep.StatusCode);
            }

            Assert.Equal(stored, await ValueAsync(world, SettingKeys.Smtp.Password));
            Assert.Equal("Renamed", await ValueAsync(world, SettingKeys.Smtp.FromName));

            var version = await VersionAsync(world);
            await AssertFieldRefusedAsync(client, new { host = "smtp.test:587", rowVersion = version }, "host");
            await AssertFieldRefusedAsync(client, new { port = "0", rowVersion = version }, "port");
            await AssertFieldRefusedAsync(client, new { port = 587, rowVersion = version }, "port");
            await AssertFieldRefusedAsync(client, new { fromAddress = "Shop <a@b.test>", rowVersion = version }, "fromAddress");
            await AssertFieldRefusedAsync(client, new { useStartTls = false, rowVersion = version }, "useStartTls");
            await AssertFieldRefusedAsync(client, new { useStartTls = "yes", rowVersion = version }, "useStartTls");

            using var stale = await client.PutAsJsonAsync(new Uri(SmtpUrl, UriKind.Relative), AccountB("AAAAAAAAAAA="), JsonOptions, Token);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal("smtp.account-a.test", await ValueAsync(world, SettingKeys.Smtp.Host));
        }
        finally
        {
            await ClearAccountAsync(world);
            client.Dispose();
        }
    }

    // ---- precedence ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Configuration_is_the_fallback_and_a_complete_saved_account_wins()
    {
        await using var world = await StartAsync(new Dictionary<string, string?>
        {
            ["Email:Smtp:Host"] = "smtp.configured.test",
            ["Email:Smtp:FromAddress"] = "configured@shop.test",
            ["Email:Smtp:Port"] = "465",
        });

        try
        {
            Assert.Equal(SmtpSettingsSource.Configuration, (await EffectiveAsync(world)).Source);
            Assert.Equal("smtp.configured.test", (await EffectiveAsync(world)).Settings!.Host);

            await SetRowsAsync(world, ("Email.Smtp.Host", "smtp.rows.test"), ("Email.Smtp.Port", "587"), ("Email.Smtp.FromAddress", "rows@shop.test"));
            var rows = await EffectiveAsync(world);
            Assert.Equal(SmtpSettingsSource.Settings, rows.Source);
            Assert.Equal("smtp.rows.test", rows.Settings!.Host);

            // A row the form would refuse (written by hand) is passed over, with a reason, for configuration.
            await SetRowsAsync(world, ("Email.Smtp.Host", "https://smtp.rows.test"));
            var invalid = await EffectiveAsync(world);
            Assert.Equal(SmtpSettingsSource.Configuration, invalid.Source);
            Assert.NotNull(invalid.IgnoredStoredReason);

            // A password that is not ciphertext is never used as one.
            await SetRowsAsync(world, ("Email.Smtp.Host", "smtp.rows.test"), ("Email.Smtp.Username", "u"), ("Email.Smtp.Password", "plaintext-in-the-column"));
            Assert.Equal(SmtpSettingsSource.Configuration, (await EffectiveAsync(world)).Source);
        }
        finally
        {
            await ClearAccountAsync(world);
        }
    }

    // ---- the test send ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_test_goes_to_the_signed_in_super_admin_through_the_typed_account_and_saves_nothing()
    {
        await using var world = await StartAsync();
        var (client, superAdmin) = await SignInSuperAdminAsync(world);

        try
        {
            using var test = await client.PostAsJsonAsync(new Uri(TestUrl, UriKind.Relative), AccountB(null), JsonOptions, Token);
            Assert.Equal(HttpStatusCode.OK, test.StatusCode);

            var email = await EmailOfAsync(world, superAdmin);
            Assert.Equal(email, (await test.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("sentTo").GetString());

            var sent = Assert.Single(world.Sink.Sent);
            Assert.Equal(email, sent.Email.ToEmail);
            Assert.Equal("smtp.account-b.test", sent.Account.Host);
            Assert.Equal("password-b-plaintext", sent.Account.Password);

            Assert.Null(await ValueAsync(world, SettingKeys.Smtp.Host));
            Assert.Empty(await OutboxForAsync(world, superAdmin));

            // A refusal comes back as a fixed sentence, never the server's own words.
            world.Sink.Throw = new SmtpException((SmtpStatusCode)535, "5.7.8 BadCredentials for user-b password-b-plaintext");
            using var refused = await client.PostAsJsonAsync(new Uri(TestUrl, UriKind.Relative), AccountB(null), JsonOptions, Token);
            var body = await refused.Content.ReadAsStringAsync(Token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("smtp_test_failed", body, StringComparison.Ordinal);
            Assert.Contains("user name or password", body, StringComparison.Ordinal);
            Assert.DoesNotContain("BadCredentials", body, StringComparison.Ordinal);
            Assert.DoesNotContain("password-b-plaintext", body, StringComparison.Ordinal);

            using var invalid = await client.PostAsJsonAsync(new Uri(TestUrl, UriKind.Relative), new { port = "587" }, JsonOptions, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        finally
        {
            await ClearAccountAsync(world);
            client.Dispose();
        }
    }

    // ---- seeding ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_seeder_adds_the_seven_keys_once_with_no_values_and_never_overwrites_a_saved_one()
    {
        await using var world = await StartAsync();

        try
        {
            await using (var scope = world.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Settings.Where(row => row.GroupKey == SettingKeys.Smtp.Group).ExecuteDeleteAsync(Token);
            }

            await SeedAsync(world);
            await SeedAsync(world);

            await using (var scope = world.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var rows = await db.Settings.AsNoTracking().Where(row => row.GroupKey == SettingKeys.Smtp.Group).ToListAsync(Token);

                Assert.Equal(7, rows.Count);
                Assert.All(rows, row =>
                {
                    Assert.Null(row.Value);
                    Assert.Equal(Permissions.Settings.Security, row.RequiredPermission);
                });
                Assert.True(Assert.Single(rows, row => row.Key == SettingKeys.Smtp.Password).IsSecret);
            }

            await SetRowsAsync(world, ("Email.Smtp.Host", "smtp.kept.test"));
            await SeedAsync(world);
            Assert.Equal("smtp.kept.test", await ValueAsync(world, SettingKeys.Smtp.Host));
        }
        finally
        {
            await ClearAccountAsync(world);
        }
    }

    // ---- the world -------------------------------------------------------------------------------------------

    private sealed record SinkMessage(OutboundEmail Email, SmtpSettings Account);

    /// <summary>A fake SMTP server: records every message with the account it was sent through, never connects.</summary>
    private sealed class SmtpSink : IEmailTransport
    {
        private readonly Lock _gate = new();

        public List<SinkMessage> Sent { get; } = [];

        public Exception? Throw { get; set; }

        public Task SendAsync(OutboundEmail email, SmtpSettings account, CancellationToken cancellationToken)
        {
            if (Throw is { } failure)
            {
                throw failure;
            }

            lock (_gate)
            {
                Sent.Add(new SinkMessage(email, account));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class World(ApiFactory factory, SmtpSink sink) : IAsyncDisposable
    {
        public ApiFactory Factory { get; } = factory;

        public SmtpSink Sink { get; } = sink;

        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }

    private async Task<World> StartAsync(IReadOnlyDictionary<string, string?>? configuration = null)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        var sink = new SmtpSink();

        var factory = new ApiFactory(
            sqlServer.ConnectionString,
            additionalConfiguration: configuration,
            configureServices: services =>
            {
                services.RemoveAll<IEmailTransport>();
                services.AddSingleton<IEmailTransport>(sink);

                // No background sweep: a test runs one when it means to (see the class remarks).
                var sweeper = services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SendEmailsService)).ToList();
                foreach (var descriptor in sweeper)
                {
                    services.Remove(descriptor);
                }
            });

        var world = new World(factory, sink);
        await ClearAccountAsync(world);
        return world;
    }

    private static async Task SweepAsync(World world)
    {
        var sweeper = ActivatorUtilities.CreateInstance<SendEmailsService>(world.Factory.Services);

        for (var pass = 0; pass < 50; pass++)
        {
            var before = world.Sink.Sent.Count;
            await sweeper.SweepAsync(Token);

            if (world.Sink.Sent.Count == before)
            {
                break;
            }
        }
    }

    private static async Task<EffectiveSmtpSettings> EffectiveAsync(World world)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISmtpSettingsReader>().ReadEffectiveAsync(Token);
    }

    private static async Task SeedAsync(World world)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        await new SettingsSeeder(scope.ServiceProvider.GetRequiredService<AppDbContext>()).SeedAsync(Token);
    }

    /// <summary>Every Email.Smtp row back to empty, and the outbox emptied (this class runs alone).</summary>
    private static async Task ClearAccountAsync(World world)
    {
        await SeedAsync(world);

        await using var scope = world.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Settings.Where(row => row.GroupKey == SettingKeys.Smtp.Group)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Value, (string?)null), Token);
        await db.EmailOutbox.ExecuteDeleteAsync(Token);

        // The Super Admins this class made are taken out of the GrantsAll role again, so the database is left with
        // the holders it had (other classes count them).
        var made = db.Users.Where(user => user.Email!.StartsWith("smtp-sa-")).Select(user => user.Id);
        await db.UserRoles.Where(ur => made.Contains(ur.UserId)).ExecuteDeleteAsync(Token);
        await db.Users.Where(user => user.Email!.StartsWith("smtp-sa-")).ExecuteUpdateAsync(set => set.SetProperty(user => user.IsActive, false), Token);

        world.Sink.Sent.Clear();
        world.Sink.Throw = null;
    }

    private static async Task DeleteOutboxAsync(World world)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailOutbox.ExecuteDeleteAsync(Token);
    }

    private static async Task SetRowsAsync(World world, params (string Key, string? Value)[] values)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var (key, value) in values)
        {
            await db.Settings.Where(row => row.Key == key).ExecuteUpdateAsync(set => set.SetProperty(row => row.Value, value), Token);
        }
    }

    private static async Task<string?> ValueAsync(World world, string key)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Settings.AsNoTracking().Where(row => row.Key == key).Select(row => row.Value).SingleAsync(Token);
    }

    private static async Task<string> VersionAsync(World world)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keys = SettingCatalog.KeysInSaveUnitFor(SettingKeys.Smtp.Group);
        return SettingsGroupVersion.For(await db.Settings.AsNoTracking().Where(row => keys.Contains(row.Key)).ToListAsync(Token));
    }

    private static async Task<string> EmailOfAsync(World world, int userId)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking().Where(user => user.Id == userId).Select(user => user.Email!).SingleAsync(Token);
    }

    private static async Task<List<EmailOutboxMessage>> OutboxForAsync(World world, int userId)
    {
        var email = await EmailOfAsync(world, userId);
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.EmailOutbox.AsNoTracking()
            .Where(row => row.ToEmail == email && row.TemplateCode == EmailTemplateCodes.SensitiveSettingsChanged)
            .ToListAsync(Token);
    }

    private static async Task EnqueueOrdinaryAsync(World world, string to)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow.AddMinutes(-1);
        db.EmailOutbox.Add(new EmailOutboxMessage
        {
            TemplateCode = EmailTemplateCodes.OrderPlaced,
            ToEmail = to,
            Subject = "We have your order",
            BodyHtml = "<p>Hello</p>",
            BodyText = "Hello",
            Status = EmailOutboxStatus.Pending,
            AttemptCount = 0,
            MaxAttempts = EmailBackoff.MaxAttempts,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
        });
        await db.SaveChangesAsync(Token);
    }

    private static JsonElement SettingOf(string payload, string key)
    {
        using var document = JsonDocument.Parse(payload);

        foreach (var group in document.RootElement.GetProperty("groups").EnumerateArray())
        {
            foreach (var setting in group.GetProperty("settings").EnumerateArray())
            {
                if (setting.GetProperty("key").GetString() == key)
                {
                    return setting.Clone();
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"{key} is not in the settings payload.");
    }

    private static async Task AssertFieldRefusedAsync(HttpClient client, object body, string field)
    {
        using var response = await client.PutAsJsonAsync(new Uri(SmtpUrl, UriKind.Relative), body, JsonOptions, Token);
        var payload = await response.Content.ReadAsStringAsync(Token);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{field}: {(int)response.StatusCode} {payload}");
        Assert.True(
            JsonDocument.Parse(payload).RootElement.GetProperty("errors").TryGetProperty(field, out _),
            $"{field} not named in {payload}");
    }

    /// <summary>
    /// A real Super Admin session: a staff user in the GrantsAll role who signs in and enrols two-factor (which the
    /// GrantsAll gate requires before any admin route), with a fresh antiforgery token.
    /// </summary>
    private static async Task<(HttpClient Client, int UserId)> SignInSuperAdminAsync(World world)
    {
        var email = $"smtp-sa-{Guid.NewGuid():N}@shop.test";
        int userId;

        await using (var scope = world.Factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();

            if (await roleManager.FindByNameAsync(AdminSeedHostedService.SuperAdminRoleName) is null)
            {
                Assert.True((await roleManager.CreateAsync(new AppRole
                {
                    Name = AdminSeedHostedService.SuperAdminRoleName,
                    IsSystem = true,
                    GrantsAllPermissions = true,
                    CreatedAtUtc = DateTime.UtcNow,
                })).Succeeded);
            }

            var user = new AppUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                UserType = UserType.Staff,
                FirstName = "Smtp",
                LastName = "Owner",
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
            };

            Assert.True((await userManager.CreateAsync(user, Password)).Succeeded);
            Assert.True((await userManager.AddToRoleAsync(user, AdminSeedHostedService.SuperAdminRoleName)).Succeeded);
            userId = user.Id;
        }

        var client = world.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using (var login = await client.PostAsJsonAsync("/api/admin/auth/login", new { email, password = Password }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        using (var setup = await PostWithTokenAsync(client, "/api/admin/auth/two-factor/setup", null))
        {
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            var key = TotpTestCodes.SecretFromUri(
                (await setup.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("authenticatorUri").GetString()!);

            using var enable = await PostWithTokenAsync(client, "/api/admin/auth/two-factor/enable", new { code = TotpTestCodes.Current(key) });
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        }

        client.DefaultRequestHeaders.Add(MediaTestHost.AntiforgeryHeader, await AntiforgeryTokenAsync(client));

        return (client, userId);
    }

    private static async Task<HttpResponseMessage> PostWithTokenAsync(HttpClient client, string url, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Add(MediaTestHost.AntiforgeryHeader, await AntiforgeryTokenAsync(client));
        return await client.SendAsync(request, Token);
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/api/admin/auth/antiforgery-token", UriKind.Relative), Token);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("token").GetString()!;
    }
}
