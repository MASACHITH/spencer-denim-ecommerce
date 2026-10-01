using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Account;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.UnitTests.Application.Admin.Account;

/// <summary>
/// W1-10: the 2FA management handlers — what each outcome becomes, what is audited, and the rule that a GrantsAll
/// holder can never turn 2FA off (checked before the manager is even asked).
/// </summary>
public sealed class AdminTwoFactorCommandHandlerTests
{
    private const string Password = "Current-Password-1!";

    private static readonly string[] RecoveryCodes = ["AAAAA-BBBBB", "CCCCC-DDDDD"];

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubManager(AdminTwoFactorOutcome outcome, AdminTwoFactorState? state = null) : IAdminTwoFactorManager
    {
        public List<string> Calls { get; } = [];

        public Task<AdminTwoFactorState?> GetStateAsync(int userId, CancellationToken cancellationToken) => Task.FromResult(state);

        public Task<AdminTwoFactorOutcome> BeginSetupAsync(int userId, CancellationToken cancellationToken) => Record(nameof(BeginSetupAsync));

        public Task<AdminTwoFactorOutcome> EnableAsync(int userId, string code, CancellationToken cancellationToken) => Record(nameof(EnableAsync));

        public Task<AdminTwoFactorOutcome> RegenerateRecoveryCodesAsync(int userId, string currentPassword, CancellationToken cancellationToken) =>
            Record(nameof(RegenerateRecoveryCodesAsync));

        public Task<AdminTwoFactorOutcome> DisableAsync(int userId, string currentPassword, CancellationToken cancellationToken) =>
            Record(nameof(DisableAsync));

        private Task<AdminTwoFactorOutcome> Record(string call)
        {
            Calls.Add(call);
            return Task.FromResult(outcome);
        }
    }

    private sealed class StubPermissionsProvider(bool grantsAll) : IEffectivePermissionsProvider
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken) =>
            Task.FromResult(new EffectivePermissions(grantsAll, []));
    }

    private sealed class RecordingAuditLogWriter : IAuditLogWriter
    {
        public List<AuditLog> Entries { get; } = [];

        public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        /// <summary>
        /// W1-12 added this to the port for use cases that have their own save to commit (issue #59). These
        /// sign-in and account use cases have nothing else to write, so they still call
        /// <see cref="WriteAsync"/>; recording into the same list keeps that fact visible rather than
        /// hiding an accidental switch behind a second collection nobody asserts on.
        /// </summary>
        public void Enlist(AuditLog entry) => Entries.Add(entry);
    }

    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    private static readonly DateTimeOffset LockoutEnd = new(2026, 9, 29, 12, 15, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AdminTwoFactorOutcome Succeeded(IReadOnlyList<string>? codes = null) =>
        AdminTwoFactorOutcome.Succeeded(7, "a@example.com", "Ada", null, "stamp-new", "abcd efgh", "otpauth://totp/x", codes);

    private static AdminTwoFactorOutcome Failed(AdminTwoFactorStatus status) => AdminTwoFactorOutcome.Failed(status, 7, "a@example.com");

    [Fact]
    public async Task Setup_returns_the_key_and_the_session_to_reissue_and_audits_nothing()
    {
        var handler = new BeginAdminTwoFactorSetupCommandHandler(new StubManager(Succeeded()));

        var result = await handler.HandleAsync(new BeginAdminTwoFactorSetupCommand(7), Ct);

        var setup = Assert.IsType<AdminTwoFactorResult.SetupStarted>(result);
        Assert.Equal("abcd efgh", setup.SharedKey);
        Assert.Equal("otpauth://totp/x", setup.AuthenticatorUri);
        Assert.Equal("stamp-new", setup.Session.SecurityStamp);
    }

    [Theory]
    [InlineData(AdminTwoFactorStatus.AlreadyEnabled, typeof(AdminTwoFactorResult.AlreadyEnabled))]
    [InlineData(AdminTwoFactorStatus.AccountUnavailable, typeof(AdminTwoFactorResult.AccountUnavailable))]
    public async Task Setup_maps_refusals(AdminTwoFactorStatus status, Type expected)
    {
        var handler = new BeginAdminTwoFactorSetupCommandHandler(new StubManager(Failed(status)));

        Assert.IsType(expected, await handler.HandleAsync(new BeginAdminTwoFactorSetupCommand(7), Ct));
    }

    [Fact]
    public async Task Enabling_returns_the_codes_once_and_audits_only_the_flag()
    {
        var audit = new RecordingAuditLogWriter();
        var handler = new EnableAdminTwoFactorCommandHandler(new StubManager(Succeeded(RecoveryCodes)), audit, Clock);

        var result = await handler.HandleAsync(new EnableAdminTwoFactorCommand(7, "123456", "203.0.113.9", "agent"), Ct);

        var enabled = Assert.IsType<AdminTwoFactorResult.Enabled>(result);
        Assert.Equal(RecoveryCodes, enabled.RecoveryCodes);
        Assert.Equal("stamp-new", enabled.Session.SecurityStamp);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.TwoFactorEnabled, entry.Action);
        Assert.Equal("{\"twoFactorEnabled\":false}", entry.BeforeJson);
        Assert.Equal("{\"twoFactorEnabled\":true}", entry.AfterJson);
        Assert.Equal("203.0.113.9", entry.IpAddress);
        foreach (var code in RecoveryCodes)
        {
            Assert.DoesNotContain(code, entry.AfterJson!, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(AdminTwoFactorStatus.InvalidCode, typeof(AdminTwoFactorResult.InvalidCode))]
    [InlineData(AdminTwoFactorStatus.AlreadyEnabled, typeof(AdminTwoFactorResult.AlreadyEnabled))]
    [InlineData(AdminTwoFactorStatus.NotSetUp, typeof(AdminTwoFactorResult.NotSetUp))]
    [InlineData(AdminTwoFactorStatus.AccountUnavailable, typeof(AdminTwoFactorResult.AccountUnavailable))]
    public async Task Enabling_maps_refusals_without_auditing_them(AdminTwoFactorStatus status, Type expected)
    {
        var audit = new RecordingAuditLogWriter();
        var handler = new EnableAdminTwoFactorCommandHandler(new StubManager(Failed(status)), audit, Clock);

        Assert.IsType(expected, await handler.HandleAsync(new EnableAdminTwoFactorCommand(7, "000000", null, null), Ct));
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Regenerating_returns_the_new_codes_and_audits_it()
    {
        var audit = new RecordingAuditLogWriter();
        var handler = new RegenerateAdminRecoveryCodesCommandHandler(new StubManager(Succeeded(RecoveryCodes)), audit, Clock);

        var result = await handler.HandleAsync(new RegenerateAdminRecoveryCodesCommand(7, Password, null, null), Ct);

        Assert.Equal(RecoveryCodes, Assert.IsType<AdminTwoFactorResult.RecoveryCodesRegenerated>(result).RecoveryCodes);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.RecoveryCodesRegenerated, entry.Action);
        Assert.Null(entry.AfterJson);
    }

    [Fact]
    public async Task A_wrong_password_on_regenerate_writes_two_factor_failed_with_the_step()
    {
        var audit = new RecordingAuditLogWriter();
        var handler = new RegenerateAdminRecoveryCodesCommandHandler(new StubManager(Failed(AdminTwoFactorStatus.InvalidCurrentPassword)), audit, Clock);

        var result = await handler.HandleAsync(new RegenerateAdminRecoveryCodesCommand(7, "wrong", null, null), Ct);

        Assert.IsType<AdminTwoFactorResult.InvalidCurrentPassword>(result);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.TwoFactorFailed, entry.Action);
        Assert.Contains("RegenerateRecoveryCodes", entry.AfterJson, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong", entry.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_wrong_password_that_trips_the_limit_writes_two_factor_failed_and_lockout()
    {
        var audit = new RecordingAuditLogWriter();
        var outcome = AdminTwoFactorOutcome.LockedOut(7, "a@example.com", LockoutEnd, wasNewlyLockedOut: true);
        var handler = new RegenerateAdminRecoveryCodesCommandHandler(new StubManager(outcome), audit, Clock);

        var result = await handler.HandleAsync(new RegenerateAdminRecoveryCodesCommand(7, "wrong", null, null), Ct);

        Assert.Equal(LockoutEnd, Assert.IsType<AdminTwoFactorResult.LockedOut>(result).LockoutEndUtc);
        Assert.Equal([AuditActions.TwoFactorFailed, AuditActions.Lockout], audit.Entries.Select(e => e.Action));
    }

    [Fact]
    public async Task Disabling_is_refused_for_a_grants_all_holder_before_the_manager_is_asked()
    {
        var manager = new StubManager(Succeeded());
        var audit = new RecordingAuditLogWriter();
        var handler = new DisableAdminTwoFactorCommandHandler(manager, new StubPermissionsProvider(grantsAll: true), audit, Clock);

        var result = await handler.HandleAsync(new DisableAdminTwoFactorCommand(7, Password, null, null), Ct);

        Assert.IsType<AdminTwoFactorResult.RequiredForRole>(result);
        Assert.Empty(manager.Calls);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Disabling_for_other_staff_reissues_the_session_and_audits_the_flag()
    {
        var audit = new RecordingAuditLogWriter();
        var handler = new DisableAdminTwoFactorCommandHandler(new StubManager(Succeeded()), new StubPermissionsProvider(grantsAll: false), audit, Clock);

        var result = await handler.HandleAsync(new DisableAdminTwoFactorCommand(7, Password, null, null), Ct);

        Assert.Equal("stamp-new", Assert.IsType<AdminTwoFactorResult.Disabled>(result).Session.SecurityStamp);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.TwoFactorDisabled, entry.Action);
        Assert.Equal("{\"twoFactorEnabled\":true}", entry.BeforeJson);
        Assert.Equal("{\"twoFactorEnabled\":false}", entry.AfterJson);
    }

    [Fact]
    public async Task Disabling_when_it_is_off_is_a_conflict()
    {
        var handler = new DisableAdminTwoFactorCommandHandler(
            new StubManager(Failed(AdminTwoFactorStatus.NotEnabled)), new StubPermissionsProvider(grantsAll: false), new RecordingAuditLogWriter(), Clock);

        Assert.IsType<AdminTwoFactorResult.NotEnabled>(await handler.HandleAsync(new DisableAdminTwoFactorCommand(7, Password, null, null), Ct));
    }

    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, false)]
    public async Task Status_reports_whether_two_factor_is_required_and_can_be_turned_off(
        bool grantsAll, bool enabled, bool expectedRequired, bool expectedCanDisable)
    {
        var state = new AdminTwoFactorState(7, "a@example.com", enabled, enabled ? 8 : 0);
        var handler = new GetAdminTwoFactorStatusQueryHandler(new StubManager(Succeeded(), state), new StubPermissionsProvider(grantsAll));

        var status = await handler.HandleAsync(new GetAdminTwoFactorStatusQuery(7), Ct);

        Assert.NotNull(status);
        Assert.Equal(enabled, status.Enabled);
        Assert.Equal(expectedRequired, status.Required);
        Assert.Equal(expectedCanDisable, status.CanDisable);
        Assert.Equal(enabled ? 8 : 0, status.RecoveryCodesLeft);
    }

    [Fact]
    public async Task Status_is_null_for_an_unavailable_account()
    {
        var handler = new GetAdminTwoFactorStatusQueryHandler(new StubManager(Succeeded(), state: null), new StubPermissionsProvider(false));

        Assert.Null(await handler.HandleAsync(new GetAdminTwoFactorStatusQuery(7), Ct));
    }
}
