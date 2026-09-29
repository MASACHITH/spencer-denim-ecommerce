using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Authentication;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.UnitTests.Application.Admin.Authentication;

public sealed class AdminLoginCommandHandlerTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubAuthenticator(AdminAuthenticationOutcome outcome) : IAdminAuthenticator
    {
        public string? ReceivedEmail { get; private set; }

        public Task<AdminAuthenticationOutcome> AuthenticateAsync(string email, string password, CancellationToken cancellationToken)
        {
            ReceivedEmail = email;
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

    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Successful_login_returns_success_result_and_writes_one_login_audit_row()
    {
        var outcome = AdminAuthenticationOutcome.Succeeded(1, "admin@example.com", "Ada", "Lovelace", true, [], "stamp-1", mustChangePassword: false);
        var authenticator = new StubAuthenticator(outcome);
        var auditWriter = new RecordingAuditLogWriter();
        var handler = new AdminLoginCommandHandler(authenticator, auditWriter, Clock);

        var result = await handler.HandleAsync(
            new AdminLoginCommand("admin@example.com", "correct-password", "203.0.113.1", "test-agent"),
            TestContext.Current.CancellationToken);

        var success = Assert.IsType<AdminLoginResult.Success>(result);
        Assert.Equal(1, success.UserId);
        Assert.True(success.GrantsAllPermissions);

        var entry = Assert.Single(auditWriter.Entries);
        Assert.Equal(AuditActions.Login, entry.Action);
        Assert.Equal(1, entry.UserId);
        Assert.Equal("admin@example.com", entry.UserEmail);
        Assert.Equal("203.0.113.1", entry.IpAddress);
        Assert.Equal("test-agent", entry.UserAgent);
    }

    [Fact]
    public async Task Wrong_password_returns_invalid_credentials_and_writes_one_login_failed_row()
    {
        var outcome = AdminAuthenticationOutcome.InvalidCredentials(userId: 2, "admin@example.com");
        var authenticator = new StubAuthenticator(outcome);
        var auditWriter = new RecordingAuditLogWriter();
        var handler = new AdminLoginCommandHandler(authenticator, auditWriter, Clock);

        var result = await handler.HandleAsync(
            new AdminLoginCommand("admin@example.com", "wrong-password", null, null),
            TestContext.Current.CancellationToken);

        Assert.IsType<AdminLoginResult.InvalidCredentials>(result);
        var entry = Assert.Single(auditWriter.Entries);
        Assert.Equal(AuditActions.LoginFailed, entry.Action);
        Assert.Equal(2, entry.UserId);
    }

    [Fact]
    public async Task Unknown_email_writes_a_login_failed_row_with_no_user_id()
    {
        var outcome = AdminAuthenticationOutcome.InvalidCredentials(userId: null, "nobody@example.com");
        var authenticator = new StubAuthenticator(outcome);
        var auditWriter = new RecordingAuditLogWriter();
        var handler = new AdminLoginCommandHandler(authenticator, auditWriter, Clock);

        var result = await handler.HandleAsync(
            new AdminLoginCommand("nobody@example.com", "whatever", null, null),
            TestContext.Current.CancellationToken);

        Assert.IsType<AdminLoginResult.InvalidCredentials>(result);
        var entry = Assert.Single(auditWriter.Entries);
        Assert.Null(entry.UserId);
        Assert.Equal("nobody@example.com", entry.UserEmail);
    }

    [Fact]
    public async Task Newly_locked_out_attempt_writes_both_a_login_failed_and_a_lockout_row()
    {
        var lockoutEnd = new DateTimeOffset(2026, 9, 21, 12, 15, 0, TimeSpan.Zero);
        var outcome = AdminAuthenticationOutcome.LockedOut(3, "admin@example.com", lockoutEnd, wasNewlyLockedOut: true);
        var authenticator = new StubAuthenticator(outcome);
        var auditWriter = new RecordingAuditLogWriter();
        var handler = new AdminLoginCommandHandler(authenticator, auditWriter, Clock);

        var result = await handler.HandleAsync(
            new AdminLoginCommand("admin@example.com", "wrong-password", null, null),
            TestContext.Current.CancellationToken);

        var lockedOut = Assert.IsType<AdminLoginResult.LockedOut>(result);
        Assert.Equal(lockoutEnd, lockedOut.LockoutEndUtc);
        Assert.Equal(2, auditWriter.Entries.Count);
        Assert.Contains(auditWriter.Entries, e => e.Action == AuditActions.LoginFailed);
        Assert.Contains(auditWriter.Entries, e => e.Action == AuditActions.Lockout);
    }

    [Fact]
    public async Task Attempt_against_an_already_locked_account_writes_only_a_login_failed_row()
    {
        var lockoutEnd = new DateTimeOffset(2026, 9, 21, 12, 15, 0, TimeSpan.Zero);
        var outcome = AdminAuthenticationOutcome.LockedOut(3, "admin@example.com", lockoutEnd, wasNewlyLockedOut: false);
        var authenticator = new StubAuthenticator(outcome);
        var auditWriter = new RecordingAuditLogWriter();
        var handler = new AdminLoginCommandHandler(authenticator, auditWriter, Clock);

        await handler.HandleAsync(new AdminLoginCommand("admin@example.com", "wrong-password", null, null), TestContext.Current.CancellationToken);

        var entry = Assert.Single(auditWriter.Entries);
        Assert.Equal(AuditActions.LoginFailed, entry.Action);
    }

    [Fact]
    public async Task Successful_login_passes_a_pending_forced_password_change_through_to_the_result()
    {
        // Issue #17: sign-in itself still succeeds, but the endpoint has to tell the client to change the password.
        var outcome = AdminAuthenticationOutcome.Succeeded(1, "admin@example.com", "Ada", null, true, [], "stamp-1", mustChangePassword: true);
        var handler = new AdminLoginCommandHandler(new StubAuthenticator(outcome), new RecordingAuditLogWriter(), Clock);

        var result = await handler.HandleAsync(
            new AdminLoginCommand("admin@example.com", "bootstrap-password", null, null),
            TestContext.Current.CancellationToken);

        var success = Assert.IsType<AdminLoginResult.Success>(result);
        Assert.True(success.MustChangePassword);
    }

    [Fact]
    public async Task HandleAsync_throws_when_command_is_null()
    {
        var handler = new AdminLoginCommandHandler(
            new StubAuthenticator(AdminAuthenticationOutcome.InvalidCredentials(null, null)),
            new RecordingAuditLogWriter(),
            Clock);

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!, CancellationToken.None));
    }
}
