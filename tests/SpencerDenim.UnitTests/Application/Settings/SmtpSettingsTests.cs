using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Authentication;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Application.Settings;

/// <summary>
/// The email account (ADM-22, 2026-10-21): the precedence rule, the field rules, the password's redaction, the save
/// handler (permission, audit, write-only secret, cross-field rules) and doc 08 §3.8 N2's dual send, and the test
/// send. The database paths are covered by <c>AdminSmtpSettingsEndpointTests</c>; these are what the mutation runs
/// exercise.
/// </summary>
public sealed class SmtpSettingsTests
{
    private static readonly AdminActor Actor = new(7, "owner@shop.test", "203.0.113.9", null, null);

    private static readonly SmtpOptions ConfiguredOptions = new()
    {
        Host = "smtp.config.test",
        Port = 2525,
        Username = "config-user",
        Password = "config-password",
        FromAddress = "config@shop.test",
        UseStartTls = true,
    };

    private static readonly string[] SecretValues =
        ["new-password", "old-password", "smtp.new.test", "smtp.previous.test", "new-user", "old-user"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- precedence -----------------------------------------------------------------------------------------

    private static StoredSmtpValues CompleteRows(
        string? host = "smtp.rows.test",
        string? port = "587",
        string? username = "rows-user",
        string? password = "rows-password",
        bool unreadable = false,
        string? from = "rows@shop.test",
        string? fromName = "Shop",
        string? startTls = "true") =>
        new(host, port, username, password, unreadable, from, fromName, startTls);

    [Fact]
    public void A_complete_valid_settings_account_wins_over_configuration()
    {
        var effective = SmtpSettingsResolver.Resolve(CompleteRows(), ConfiguredOptions);

        Assert.Equal(SmtpSettingsSource.Settings, effective.Source);
        Assert.Equal("smtp.rows.test", effective.Settings!.Host);
        Assert.Equal(587, effective.Settings.Port);
        Assert.Equal("rows-user", effective.Settings.Username);
        Assert.Equal("rows-password", effective.Settings.Password);
        Assert.Equal("rows@shop.test", effective.Settings.FromAddress);
        Assert.Equal("Shop", effective.Settings.FromName);
        Assert.Null(effective.IgnoredStoredReason);
    }

    [Fact]
    public void No_saved_server_falls_back_to_configuration_without_a_warning()
    {
        var effective = SmtpSettingsResolver.Resolve(CompleteRows(host: null), ConfiguredOptions);

        Assert.Equal(SmtpSettingsSource.Configuration, effective.Source);
        Assert.Equal("smtp.config.test", effective.Settings!.Host);
        Assert.Equal("config-password", effective.Settings.Password);
        Assert.Null(effective.IgnoredStoredReason);
    }

    [Fact]
    public void Nothing_saved_and_nothing_configured_is_none()
    {
        var effective = SmtpSettingsResolver.Resolve(StoredSmtpValues.Empty, new SmtpOptions());

        Assert.Equal(SmtpSettingsSource.None, effective.Source);
        Assert.False(effective.IsConfigured);
    }

    public static TheoryData<string, StoredSmtpValues> IncompleteRows => new()
    {
        { "bad host", CompleteRows(host: "https://smtp.rows.test") },
        { "host with port", CompleteRows(host: "smtp.rows.test:587") },
        { "no port", CompleteRows(port: null) },
        { "port zero", CompleteRows(port: "0") },
        { "port too high", CompleteRows(port: "65536") },
        { "port text", CompleteRows(port: "five") },
        { "no from", CompleteRows(from: null) },
        { "bad from", CompleteRows(from: "Shop <rows@shop.test>") },
        { "from name line break", CompleteRows(fromName: "Shop\r\nBcc: x@y.test") },
        { "username line break", CompleteRows(username: "u\nser") },
        { "unreadable password", CompleteRows(password: null, unreadable: true) },
        { "switch not boolean", CompleteRows(startTls: "maybe") },
        { "user name without encryption", CompleteRows(startTls: "false") },
    };

    [Theory]
    [MemberData(nameof(IncompleteRows))]
    public void An_invalid_saved_account_is_passed_over_for_configuration_with_a_reason_and_no_mixture(
        string because,
        StoredSmtpValues rows)
    {
        var effective = SmtpSettingsResolver.Resolve(rows, ConfiguredOptions);

        Assert.True(effective.Source == SmtpSettingsSource.Configuration, because);

        // Every value from configuration: never the saved server with the configured password, or the reverse.
        Assert.Equal("smtp.config.test", effective.Settings!.Host);
        Assert.Equal("config-user", effective.Settings.Username);
        Assert.Equal("config-password", effective.Settings.Password);
        Assert.Equal("config@shop.test", effective.Settings.FromAddress);
        Assert.False(string.IsNullOrWhiteSpace(effective.IgnoredStoredReason), because);
        Assert.DoesNotContain("rows", effective.IgnoredStoredReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unsaved_switch_counts_as_on_and_a_relay_without_credentials_may_switch_it_off()
    {
        var unsaved = SmtpSettingsResolver.Resolve(CompleteRows(startTls: null), new SmtpOptions());
        Assert.True(unsaved.Settings!.UseStartTls);

        var relay = SmtpSettingsResolver.Resolve(
            CompleteRows(username: null, password: null, startTls: "false"),
            new SmtpOptions());
        Assert.Equal(SmtpSettingsSource.Settings, relay.Source);
        Assert.False(relay.Settings!.UseStartTls);
    }

    [Fact]
    public void Configuration_with_a_bad_port_is_not_an_account()
    {
        Assert.Null(SmtpSettings.FromOptions(new SmtpOptions { Host = "h.test", FromAddress = "a@b.test", Port = 0 }));
    }

    // ---- rules ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("smtp.gmail.com", true)]
    [InlineData("mail", true)]
    [InlineData("192.0.2.10", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("smtp.example.com:587", false)]
    [InlineData("https://smtp.example.com", false)]
    [InlineData("smtp example.com", false)]
    [InlineData("smtp.example.com/x", false)]
    [InlineData("", false)]
    public void A_host_is_a_name_or_an_address_only(string host, bool valid)
    {
        Assert.Equal(valid, SmtpSettingRules.IsValidHost(host));
    }

    [Theory]
    [InlineData("shop@example.lk", true)]
    [InlineData("Shop <shop@example.lk>", false)]
    [InlineData("Shop<shop@example.lk>", false)]
    [InlineData("\"Shop\"<shop@example.lk>", false)]
    [InlineData("shop", false)]
    [InlineData("shop@example.lk\r\nBcc: x@y.z", false)]
    [InlineData("a b@example.lk", false)]
    public void A_send_from_address_is_one_bare_address(string address, bool valid)
    {
        Assert.Equal(valid, SmtpSettingRules.IsValidAddress(address));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    [InlineData(" 587 ", 587)]
    [InlineData("0", null)]
    [InlineData("65536", null)]
    [InlineData("-1", null)]
    [InlineData("5 87", null)]
    public void A_port_is_whole_digits_in_range(string text, int? expected)
    {
        Assert.Equal(expected, SmtpSettingRules.ParsePort(text));
    }

    [Fact]
    public void The_account_never_prints_its_password()
    {
        var account = new SmtpSettings("h.test", 587, "user", "hunter2-secret", "a@b.test", null, true);

        Assert.DoesNotContain("hunter2-secret", account.ToString(), StringComparison.Ordinal);
        Assert.Contains(SmtpSettings.RedactedPassword, account.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2-secret", $"{account}", StringComparison.Ordinal);
    }

    // ---- the save --------------------------------------------------------------------------------------------

    private static readonly (string Field, string? Value)[] AFullForm =
    [
        ("host", "smtp.new.test"),
        ("port", "587"),
        ("username", "new-user"),
        ("password", "new-password"),
        ("fromAddress", "new@shop.test"),
        ("fromName", "Colombo Wardrobe"),
    ];

    [Fact]
    public async Task A_full_save_stores_every_key_encrypts_the_password_and_audits_it_as_secret_changed()
    {
        var world = new World();

        var result = await world.Save(AFullForm, useStartTls: true);

        var succeeded = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal(
            [
                SettingKeys.Smtp.Host,
                SettingKeys.Smtp.Port,
                SettingKeys.Smtp.Username,
                SettingKeys.Smtp.Password,
                SettingKeys.Smtp.FromAddress,
                SettingKeys.Smtp.FromName,
                SettingKeys.Smtp.UseStartTls,
            ],
            succeeded.ChangedKeys);

        Assert.Equal("smtp.new.test", world.Store.Rows[SettingKeys.Smtp.Host].Value);
        Assert.Equal("protected:new-password", world.Store.Rows[SettingKeys.Smtp.Password].Value);
        Assert.Equal(1, world.Store.Saves);

        Assert.Equal(7, world.Audit.Rows.Count);
        var passwordAudit = Assert.Single(world.Audit.Rows, row => row.EntityId == SettingKeys.Smtp.Password);
        Assert.Contains(SettingsAuditTrail.RedactedSecret, passwordAudit.AfterJson, StringComparison.Ordinal);
        Assert.All(world.Audit.Rows, row =>
        {
            Assert.DoesNotContain("new-password", row.AfterJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("new-password", row.BeforeJson ?? string.Empty, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_blank_password_keeps_the_saved_one_and_the_same_password_is_not_a_change()
    {
        var world = new World();
        world.Store.Rows[SettingKeys.Smtp.Password].Value = "protected:kept";

        var blank = await world.Save([("password", "   ")]);
        Assert.IsType<SettingsWriteResult.Unchanged>(blank);

        var same = await world.Save([("password", "kept")]);
        Assert.IsType<SettingsWriteResult.Unchanged>(same);

        Assert.Equal("protected:kept", world.Store.Rows[SettingKeys.Smtp.Password].Value);
        Assert.Empty(world.Audit.Rows);
        Assert.Empty(world.Outbox.Rows);
        Assert.Empty(world.Alerts.Rows);
    }

    [Fact]
    public async Task A_password_is_not_trimmed()
    {
        var world = new World();

        await world.Save([("password", " spaced ")]);

        Assert.Equal("protected: spaced ", world.Store.Rows[SettingKeys.Smtp.Password].Value);
    }

    [Fact]
    public async Task Only_settings_security_may_save_and_a_store_manager_is_refused_naming_the_keys()
    {
        var world = new World(new EffectivePermissions(false, [Permissions.Settings.Edit, Permissions.Settings.Payments]));

        var result = await world.Save(AFullForm, useStartTls: true);

        var forbidden = Assert.IsType<SettingsWriteResult.Forbidden>(result);
        Assert.Contains(SettingKeys.Smtp.Password, forbidden.Keys);
        Assert.Equal(0, world.Store.Saves);
        Assert.Empty(world.Outbox.Rows);
        Assert.Empty(world.Alerts.Rows);
    }

    [Fact]
    public async Task A_holder_of_settings_security_alone_may_save()
    {
        var world = new World(new EffectivePermissions(false, [Permissions.Settings.Security]));

        Assert.IsType<SettingsWriteResult.Succeeded>(await world.Save(AFullForm, useStartTls: true));
    }

    [Fact]
    public async Task A_stale_token_is_a_conflict_and_writes_nothing()
    {
        var world = new World();

        var result = await world.Save(AFullForm, useStartTls: true, rowVersion: "AAAAAAAAAAA=");

        Assert.IsType<SettingsWriteResult.Conflict>(result);
        Assert.Equal(0, world.Store.Saves);
    }

    [Theory]
    [InlineData("host", "https://smtp.test")]
    [InlineData("port", "70000")]
    [InlineData("fromAddress", "not an address")]
    [InlineData("fromName", "Shop\nBcc: x@y.test")]
    [InlineData("username", "a\rb")]
    [InlineData("password", "pass\nword")]
    public async Task A_bad_field_is_refused_on_that_field(string field, string value)
    {
        var world = new World();

        var result = await world.Save([(field, value)]);

        var invalid = Assert.IsType<SettingsWriteResult.Invalid>(result);
        Assert.Contains(invalid.Errors, error => error.Field == field);
        Assert.Equal(0, world.Store.Saves);
    }

    [Fact]
    public async Task A_server_needs_a_port_and_a_send_from_address()
    {
        var world = new World();

        var result = await world.Save([("host", "smtp.new.test")]);

        var invalid = Assert.IsType<SettingsWriteResult.Invalid>(result);
        Assert.Contains(invalid.Errors, error => error.Field == "port");
        Assert.Contains(invalid.Errors, error => error.Field == "fromAddress");
    }

    [Fact]
    public async Task A_user_name_with_encryption_off_is_refused_on_the_switch()
    {
        var world = new World();

        var result = await world.Save(AFullForm, useStartTls: false);

        var invalid = Assert.IsType<SettingsWriteResult.Invalid>(result);
        Assert.Equal("useStartTls", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task Clearing_the_server_is_allowed_and_hands_sending_back_to_configuration()
    {
        var world = new World();
        world.Store.Rows[SettingKeys.Smtp.Host].Value = "smtp.old.test";

        var result = await world.Save([("host", null)]);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Null(world.Store.Rows[SettingKeys.Smtp.Host].Value);
    }

    [Fact]
    public async Task A_port_is_stored_normalised()
    {
        var world = new World();

        await world.Save([("port", "0587")]);

        Assert.Equal("587", world.Store.Rows[SettingKeys.Smtp.Port].Value);
    }

    // ---- N2: the banner and the dual send --------------------------------------------------------------------

    [Fact]
    public async Task A_change_raises_one_banner_and_emails_every_super_admin_through_the_new_and_the_previous_account()
    {
        var previous = new SmtpSettings("smtp.previous.test", 25, "old-user", "old-password", "old@shop.test", null, true);
        var world = new World(previous: new EffectiveSmtpSettings(previous, SmtpSettingsSource.Configuration));

        await world.Save(AFullForm, useStartTls: true);

        var banner = Assert.Single(world.Alerts.Rows);
        Assert.Equal(AdminAlertTypes.SensitiveSettingsChanged, banner.AlertType);
        Assert.DoesNotContain("new-password", banner.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp.new.test", banner.DetailsJson, StringComparison.Ordinal);

        // Two Super Admins (one listed twice, under a different case), two copies each.
        Assert.Equal(4, world.Outbox.Rows.Count);
        Assert.All(world.Outbox.Rows, row => Assert.Equal(EmailTemplateCodes.SensitiveSettingsChanged, row.TemplateCode));

        foreach (var address in new[] { "first@shop.test", "second@shop.test" })
        {
            var mine = world.Outbox.Rows.Where(row => row.ToEmail == address).ToList();
            Assert.Equal(2, mine.Count);
            Assert.Single(mine, row => row.TransportOverrideProtected is null);

            var carried = Assert.Single(mine, row => row.TransportOverrideProtected is not null);
            Assert.Equal("override:smtp.previous.test", carried.TransportOverrideProtected);
            Assert.Contains("previous mail account", carried.Subject, StringComparison.Ordinal);
        }

        // Keys, actor and IP — never a value, old or new.
        Assert.All(world.Outbox.Rows, row =>
        {
            foreach (var value in SecretValues)
            {
                Assert.DoesNotContain(value, row.BodyHtml!, StringComparison.Ordinal);
                Assert.DoesNotContain(value, row.BodyText!, StringComparison.Ordinal);
                Assert.DoesNotContain(value, row.Subject, StringComparison.Ordinal);
            }

            Assert.Contains("Email.Smtp.Password", row.BodyText!, StringComparison.Ordinal);
            Assert.Contains("owner@shop.test", row.BodyText!, StringComparison.Ordinal);
            Assert.Contains("203.0.113.9", row.BodyText!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task With_no_previous_account_only_the_new_account_copy_is_queued()
    {
        var world = new World(previous: EffectiveSmtpSettings.None);

        await world.Save(AFullForm, useStartTls: true);

        Assert.Equal(2, world.Outbox.Rows.Count);
        Assert.All(world.Outbox.Rows, row => Assert.Null(row.TransportOverrideProtected));
        Assert.Single(world.Alerts.Rows);
    }

    [Fact]
    public async Task The_previous_account_is_read_before_any_row_changes()
    {
        var world = new World();
        world.Store.Rows[SettingKeys.Smtp.Host].Value = "smtp.before.test";

        await world.Save([("host", "smtp.after.test"), ("port", "587"), ("fromAddress", "a@shop.test")]);

        Assert.Equal("smtp.before.test", world.Reader.HostSeenAtRead);
    }

    // ---- the test send ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_test_goes_to_the_signed_in_admin_through_the_typed_account_and_saves_nothing()
    {
        var world = new World();

        var result = await world.Test(AFullForm, useStartTls: true);

        var sent = Assert.IsType<SmtpTestResult.Sent>(result);
        Assert.Equal("me@shop.test", sent.ToEmail);

        var (email, account) = Assert.Single(world.Transport.Sent);
        Assert.Equal("me@shop.test", email.ToEmail);
        Assert.Equal("smtp.new.test", account.Host);
        Assert.Equal("new-password", account.Password);
        Assert.DoesNotContain("new-password", email.BodyText!, StringComparison.Ordinal);
        Assert.Equal(0, world.Store.Saves);
        Assert.Empty(world.Audit.Rows);
        Assert.Empty(world.Outbox.Rows);
    }

    [Fact]
    public async Task A_blank_password_tests_with_the_saved_one()
    {
        var world = new World();
        world.Reader.StoredPassword = "saved-password";

        await world.Test(AFullForm.Where(field => field.Field != "password").ToArray(), useStartTls: true);

        Assert.Equal("saved-password", Assert.Single(world.Transport.Sent).Account.Password);
    }

    [Fact]
    public async Task Only_settings_security_may_test()
    {
        var world = new World(new EffectivePermissions(false, [Permissions.Settings.Edit, Permissions.Settings.Payments]));

        Assert.IsType<SmtpTestResult.Forbidden>(await world.Test(AFullForm, useStartTls: true));
        Assert.Empty(world.Transport.Sent);
    }

    [Fact]
    public async Task A_test_without_a_server_is_refused_before_anything_is_sent()
    {
        var world = new World();

        var result = await world.Test([("port", "587"), ("fromAddress", "a@shop.test")], useStartTls: true);

        var invalid = Assert.IsType<SmtpTestResult.Invalid>(result);
        Assert.Contains(invalid.Errors, error => error.Field == "host");
        Assert.Empty(world.Transport.Sent);
    }

    public static TheoryData<Exception, string> Failures => new()
    {
        { new SmtpException((SmtpStatusCode)535, "5.7.8 Username and Password not accepted for new-user secret-detail"), "user name or password" },
        { new SmtpException(SmtpStatusCode.MustIssueStartTlsFirst, "secret-detail"), "encrypted connection" },
        { new SmtpException(SmtpStatusCode.MailboxUnavailable, "secret-detail"), "send from this address" },
        { new SmtpException("secret-detail", new SocketException(11001)), "Could not connect" },
        { new SmtpException("secret-detail", new AuthenticationException("secret-detail")), "secure connection" },
        { new TimeoutException("secret-detail"), "did not answer" },
        { new SmtpException(SmtpStatusCode.ServiceNotAvailable, "421 secret-detail"), "refused the message (code 421)" },
        { new InvalidOperationException("secret-detail"), "could not be sent" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_failure_is_a_fixed_safe_sentence_and_never_the_servers_words(Exception failure, string expected)
    {
        var world = new World();
        world.Transport.Throw = failure;

        var result = await world.Test(AFullForm, useStartTls: true);

        var failed = Assert.IsType<SmtpTestResult.Failed>(result);
        Assert.Contains(expected, failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-detail", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("new-password", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("new-user", failed.Message, StringComparison.Ordinal);
    }

    // ---- the world --------------------------------------------------------------------------------------------

    private sealed class World
    {
        public World(EffectivePermissions? permissions = null, EffectiveSmtpSettings? previous = null)
        {
            Permissions = permissions ?? new EffectivePermissions(true, []);
            Reader = new FakeReader(Store, previous ?? EffectiveSmtpSettings.None);
        }

        public FakeSettingsStore Store { get; } = new();

        public RecordingAuditLog Audit { get; } = new();

        public RecordingAlerts Alerts { get; } = new();

        public RecordingOutbox Outbox { get; } = new();

        public FakeReader Reader { get; }

        public FakeTransport Transport { get; } = new();

        public EffectivePermissions Permissions { get; }

        public Task<SettingsWriteResult> Save(
            (string Field, string? Value)[] fields,
            bool? useStartTls = null,
            string? rowVersion = null)
        {
            var notifier = new SensitiveSettingsChangeNotifier(Alerts, Outbox, new FakeRecipients(), new FakeOverrides());

            var handler = new UpdateSmtpSettingsCommandHandler(
                Store,
                new FakeSecrets(),
                Reader,
                notifier,
                Audit,
                new FakePermissions(Permissions),
                new FixedClock());

            return handler.HandleAsync(
                new UpdateSmtpSettingsCommand(
                    fields.ToDictionary(field => field.Field, field => field.Value, StringComparer.Ordinal),
                    useStartTls,
                    rowVersion ?? SettingsGroupVersion.For(
                        SettingCatalog.KeysInSaveUnitFor(SettingKeys.Smtp.Group).Select(key => Store.Rows[key])),
                    Actor),
                Token);
        }

        public Task<SmtpTestResult> Test((string Field, string? Value)[] fields, bool? useStartTls = null)
        {
            var handler = new SendSmtpTestEmailCommandHandler(
                Reader,
                new FakeRecipients(),
                Transport,
                new FakePermissions(Permissions),
                new FixedClock());

            return handler.HandleAsync(
                new SendSmtpTestEmailCommand(
                    fields.ToDictionary(field => field.Field, field => field.Value, StringComparer.Ordinal),
                    useStartTls,
                    Actor),
                Token);
        }
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public FakeSettingsStore()
        {
            var version = (byte)1;

            foreach (var definition in SettingCatalog.All)
            {
                Rows[definition.Key] = new Setting
                {
                    Key = definition.Key,
                    GroupKey = definition.GroupKey,
                    ValueType = definition.ValueType,
                    IsSecret = definition.IsSecret,
                    RequiredPermission = definition.RequiredPermission,
                    Description = definition.Description,
                    Value = definition.SeedValue,
                    RowVersion = [0, 0, 0, 0, 0, 0, 0, version++],
                };
            }
        }

        public Dictionary<string, Setting> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Saves { get; private set; }

        public Task<IReadOnlyList<Setting>> ListAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Setting>>(keys.Where(Rows.ContainsKey).Select(key => Rows[key]).ToList());

        public Task<SettingImageAsset?> FindUsableImageAsync(Guid publicId, CancellationToken cancellationToken) =>
            Task.FromResult<SettingImageAsset?>(null);

        public Task<SettingImageAsset?> FindUsableImageByIdAsync(int assetId, CancellationToken cancellationToken) =>
            Task.FromResult<SettingImageAsset?>(null);

        public Task ReplaceMediaUsageAsync(string settingKey, string usageRole, int? newAssetId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<SettingsSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken)
        {
            Saves++;
            return Task.FromResult(SettingsSaveOutcome.Saved);
        }
    }

    /// <summary>"protected:" + plaintext, so a test can see what was protected and what was not.</summary>
    private sealed class FakeSecrets : ISettingSecretProtector
    {
        public string Protect(string plaintext) => "protected:" + plaintext;

        public bool TryUnprotect(string? protectedValue, out string plaintext)
        {
            plaintext = string.Empty;

            if (protectedValue is null || !protectedValue.StartsWith("protected:", StringComparison.Ordinal))
            {
                return false;
            }

            plaintext = protectedValue["protected:".Length..];
            return true;
        }
    }

    private sealed class FakeReader(FakeSettingsStore store, EffectiveSmtpSettings previous) : ISmtpSettingsReader
    {
        public string? HostSeenAtRead { get; private set; }

        public string? StoredPassword { get; set; }

        public Task<EffectiveSmtpSettings> ReadEffectiveAsync(CancellationToken cancellationToken)
        {
            HostSeenAtRead = store.Rows[SettingKeys.Smtp.Host].Value;
            return Task.FromResult(previous);
        }

        public Task<string?> ReadStoredPasswordAsync(CancellationToken cancellationToken) =>
            Task.FromResult(StoredPassword);
    }

    private sealed class FakeOverrides : ISmtpOverrideProtector
    {
        public string Protect(SmtpSettings settings) => "override:" + settings.Host;

        public bool TryUnprotect(string? protectedValue, out SmtpSettings? settings)
        {
            settings = null;
            return false;
        }
    }

    private sealed class FakeRecipients : ISecurityAlertRecipients
    {
        public Task<IReadOnlyList<StaffAddressee>> ListActiveSuperAdminsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StaffAddressee>>(
            [
                new StaffAddressee(1, "first@shop.test", "First"),
                new StaffAddressee(2, "second@shop.test", "Second"),
                new StaffAddressee(3, "FIRST@shop.test", "Duplicate"),
            ]);

        public Task<StaffAddressee?> FindActiveStaffAsync(int userId, CancellationToken cancellationToken) =>
            Task.FromResult<StaffAddressee?>(userId == Actor.UserId ? new StaffAddressee(userId, "me@shop.test", "Me") : null);
    }

    private sealed class FakeTransport : IEmailTransport
    {
        public Exception? Throw { get; set; }

        public List<(OutboundEmail Email, SmtpSettings Account)> Sent { get; } = [];

        public Task SendAsync(OutboundEmail email, SmtpSettings account, CancellationToken cancellationToken)
        {
            if (Throw is { } failure)
            {
                throw failure;
            }

            Sent.Add((email, account));
            return Task.CompletedTask;
        }
    }

    private sealed class FakePermissions(EffectivePermissions permissions) : IEffectivePermissionsProvider
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken) =>
            Task.FromResult(permissions);
    }

    private sealed class RecordingAuditLog : IAuditLogWriter
    {
        public List<AuditLog> Rows { get; } = [];

        public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken)
        {
            Rows.Add(entry);
            return Task.CompletedTask;
        }

        public void Enlist(AuditLog entry) => Rows.Add(entry);
    }

    private sealed class RecordingAlerts : IAdminAlertWriter
    {
        public List<AdminAlert> Rows { get; } = [];

        public Task RaiseAsync(AdminAlert alert, CancellationToken cancellationToken)
        {
            Rows.Add(alert);
            return Task.CompletedTask;
        }

        public void Enlist(AdminAlert alert) => Rows.Add(alert);
    }

    private sealed class RecordingOutbox : IEmailOutboxWriter
    {
        public List<EmailOutboxMessage> Rows { get; } = [];

        public void Enlist(EmailOutboxMessage message) => Rows.Add(message);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    }
}
