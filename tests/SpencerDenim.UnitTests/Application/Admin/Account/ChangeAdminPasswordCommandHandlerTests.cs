using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Account;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.UnitTests.Application.Admin.Account;

/// <summary>Issue #17: the audit trail and result mapping of a staff user's own password change.</summary>
public sealed class ChangeAdminPasswordCommandHandlerTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubPasswordChanger(AdminPasswordChangeOutcome outcome) : IAdminPasswordChanger
    {
        public (int UserId, string Current, string New)? Received { get; private set; }

        public Task<AdminPasswordChangeOutcome> ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
        {
            Received = (userId, currentPassword, newPassword);
            return Task.FromResult(outcome);
        }
    }

    private sealed class RecordingAuditLogWriter : IAuditLogWriter
    {
        public List<AuditLog> Entries { get; } = [];

        public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero));

    private static readonly ChangeAdminPasswordCommand Command =
        new(7, "Old-Password-1!", "New-Password-2!", "203.0.113.9", "test-agent");

    private static (ChangeAdminPasswordCommandHandler Handler, StubPasswordChanger Changer, RecordingAuditLogWriter Audit) Create(AdminPasswordChangeOutcome outcome)
    {
        var changer = new StubPasswordChanger(outcome);
        var audit = new RecordingAuditLogWriter();
        return (new ChangeAdminPasswordCommandHandler(changer, audit, Clock), changer, audit);
    }

    [Fact]
    public async Task Clearing_a_forced_change_returns_the_new_stamp_and_audits_the_flag_diff_only()
    {
        var (handler, changer, audit) = Create(
            AdminPasswordChangeOutcome.Succeeded(7, "admin@example.com", "Ada", "Lovelace", "new-stamp", clearedMustChangePassword: true));

        var result = await handler.HandleAsync(Command, TestContext.Current.CancellationToken);

        var success = Assert.IsType<ChangeAdminPasswordResult.Success>(result);
        Assert.Equal("new-stamp", success.SecurityStamp);
        Assert.Equal(7, success.UserId);
        Assert.Equal((7, "Old-Password-1!", "New-Password-2!"), changer.Received);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.PasswordChanged, entry.Action);
        Assert.Equal(7, entry.UserId);
        Assert.Equal("7", entry.EntityId);
        Assert.Equal("User", entry.EntityType);
        Assert.Equal("admin@example.com", entry.UserEmail);
        Assert.Equal("203.0.113.9", entry.IpAddress);
        Assert.Equal("test-agent", entry.UserAgent);
        Assert.Equal("""{"mustChangePassword":true}""", entry.BeforeJson);
        Assert.Equal("""{"mustChangePassword":false}""", entry.AfterJson);
    }

    [Fact]
    public async Task A_voluntary_change_is_audited_without_a_diff()
    {
        var (handler, _, audit) = Create(
            AdminPasswordChangeOutcome.Succeeded(7, "admin@example.com", "Ada", null, "new-stamp", clearedMustChangePassword: false));

        await handler.HandleAsync(Command, TestContext.Current.CancellationToken);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.PasswordChanged, entry.Action);
        Assert.Null(entry.BeforeJson);
        Assert.Null(entry.AfterJson);
    }

    [Fact]
    public async Task No_audit_row_ever_contains_a_password()
    {
        var (handler, _, audit) = Create(
            AdminPasswordChangeOutcome.Succeeded(7, "admin@example.com", "Ada", null, "new-stamp", clearedMustChangePassword: true));

        await handler.HandleAsync(Command, TestContext.Current.CancellationToken);

        var entry = Assert.Single(audit.Entries);
        var serialised = string.Concat(entry.BeforeJson, entry.AfterJson);
        Assert.DoesNotContain("Old-Password-1!", serialised, StringComparison.Ordinal);
        Assert.DoesNotContain("New-Password-2!", serialised, StringComparison.Ordinal);
        Assert.DoesNotContain("new-stamp", serialised, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_current_password_is_refused_and_audited_as_password_change_failed()
    {
        var (handler, _, audit) = Create(AdminPasswordChangeOutcome.InvalidCurrentPassword(7, "admin@example.com"));

        var result = await handler.HandleAsync(Command, TestContext.Current.CancellationToken);

        Assert.IsType<ChangeAdminPasswordResult.InvalidCurrentPassword>(result);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.PasswordChangeFailed, entry.Action);
        Assert.Equal(7, entry.UserId);
        Assert.Equal("""{"reason":"InvalidCurrentPassword"}""", entry.AfterJson);
    }

    [Fact]
    public async Task The_attempt_that_trips_the_lockout_writes_both_a_failed_and_a_lockout_row()
    {
        var lockoutEnd = new DateTimeOffset(2026, 9, 29, 9, 15, 0, TimeSpan.Zero);
        var (handler, _, audit) = Create(AdminPasswordChangeOutcome.LockedOut(7, "admin@example.com", lockoutEnd, wasNewlyLockedOut: true));

        var result = await handler.HandleAsync(Command, TestContext.Current.CancellationToken);

        var lockedOut = Assert.IsType<ChangeAdminPasswordResult.LockedOut>(result);
        Assert.Equal(lockoutEnd, lockedOut.LockoutEndUtc);
        Assert.Equal(2, audit.Entries.Count);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.PasswordChangeFailed);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.Lockout);
    }

    [Fact]
    public async Task An_attempt_against_an_already_locked_account_writes_only_the_failed_row()
    {
        var lockoutEnd = new DateTimeOffset(2026, 9, 29, 9, 15, 0, TimeSpan.Zero);
        var (handler, _, audit) = Create(AdminPasswordChangeOutcome.LockedOut(7, "admin@example.com", lockoutEnd, wasNewlyLockedOut: false));

        await handler.HandleAsync(Command, TestContext.Current.CancellationToken);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.PasswordChangeFailed, entry.Action);
    }

    [Fact]
    public async Task A_new_password_that_fails_the_policy_is_returned_with_its_errors_and_not_audited()
    {
        var (handler, _, audit) = Create(
            AdminPasswordChangeOutcome.NewPasswordRejected(7, "admin@example.com", ["Passwords must have at least one digit ('0'-'9')."]));

        var result = await handler.HandleAsync(Command, TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<ChangeAdminPasswordResult.NewPasswordRejected>(result);
        Assert.Single(rejected.Errors);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task An_unavailable_account_is_reported_as_such_and_not_audited()
    {
        var (handler, _, audit) = Create(AdminPasswordChangeOutcome.AccountUnavailable(7));

        var result = await handler.HandleAsync(Command, TestContext.Current.CancellationToken);

        Assert.IsType<ChangeAdminPasswordResult.AccountUnavailable>(result);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task HandleAsync_throws_when_command_is_null()
    {
        var (handler, _, _) = Create(AdminPasswordChangeOutcome.AccountUnavailable(7));

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!, CancellationToken.None));
    }
}
