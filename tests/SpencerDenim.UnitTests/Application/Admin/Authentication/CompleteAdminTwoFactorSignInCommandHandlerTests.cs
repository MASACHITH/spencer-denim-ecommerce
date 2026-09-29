using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Authentication;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.UnitTests.Application.Admin.Authentication;

/// <summary>W1-10: the second sign-in step's results and audit rows. Codes never reach an audit row.</summary>
public sealed class CompleteAdminTwoFactorSignInCommandHandlerTests
{
    private const string Code = "481516";

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubAuthenticator(AdminAuthenticationOutcome outcome) : IAdminAuthenticator
    {
        public (int UserId, string Stamp, string Code, bool IsRecovery)? Received { get; private set; }

        public Task<AdminAuthenticationOutcome> AuthenticateAsync(string email, string password, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdminAuthenticationOutcome> CompleteTwoFactorSignInAsync(
            int userId,
            string pendingSecurityStamp,
            string code,
            bool isRecoveryCode,
            CancellationToken cancellationToken)
        {
            Received = (userId, pendingSecurityStamp, code, isRecoveryCode);
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

    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    private static CompleteAdminTwoFactorSignInCommand Command(bool isRecoveryCode = false) =>
        new(7, "stamp-7", Code, isRecoveryCode, "203.0.113.9", "test-agent");

    private static async Task<(AdminLoginResult Result, RecordingAuditLogWriter Audit, StubAuthenticator Authenticator)> RunAsync(
        AdminAuthenticationOutcome outcome, bool isRecoveryCode = false)
    {
        var authenticator = new StubAuthenticator(outcome);
        var audit = new RecordingAuditLogWriter();
        var handler = new CompleteAdminTwoFactorSignInCommandHandler(authenticator, audit, Clock);
        var result = await handler.HandleAsync(Command(isRecoveryCode), TestContext.Current.CancellationToken);
        return (result, audit, authenticator);
    }

    [Fact]
    public async Task A_right_authenticator_code_signs_in_and_writes_one_login_row()
    {
        var outcome = AdminAuthenticationOutcome.Succeeded(7, "a@example.com", "Ada", null, true, [], "stamp-7", false, twoFactorEnabled: true);

        var (result, audit, authenticator) = await RunAsync(outcome);

        var success = Assert.IsType<AdminLoginResult.Success>(result);
        Assert.True(success.TwoFactorEnabled);
        Assert.False(success.TwoFactorEnrolmentRequired);
        Assert.Equal((7, "stamp-7", Code, false), authenticator.Received);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.Login, entry.Action);
        Assert.Equal("203.0.113.9", entry.IpAddress);
        Assert.Contains("Authenticator", entry.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recovery_code_sign_in_also_writes_recovery_code_used_with_the_count_left()
    {
        var outcome = AdminAuthenticationOutcome.Succeeded(
            7, "a@example.com", "Ada", null, false, [], "stamp-7", false, twoFactorEnabled: true, usedRecoveryCode: true, recoveryCodesLeft: 4);

        var (result, audit, _) = await RunAsync(outcome, isRecoveryCode: true);

        Assert.IsType<AdminLoginResult.Success>(result);
        Assert.Equal([AuditActions.Login, AuditActions.RecoveryCodeUsed], audit.Entries.Select(e => e.Action));
        Assert.Contains("\"recoveryCodesLeft\":4", audit.Entries[1].AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_code_writes_two_factor_failed_without_the_code()
    {
        var (result, audit, _) = await RunAsync(AdminAuthenticationOutcome.InvalidTwoFactorCode(7, "a@example.com", usedRecoveryCode: false));

        Assert.IsType<AdminLoginResult.InvalidTwoFactorCode>(result);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.TwoFactorFailed, entry.Action);
        Assert.Contains("InvalidCode", entry.AfterJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Code, entry.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_code_that_trips_the_limit_writes_two_factor_failed_and_lockout()
    {
        var lockoutEnd = new DateTimeOffset(2026, 9, 29, 12, 15, 0, TimeSpan.Zero);

        var (result, audit, _) = await RunAsync(AdminAuthenticationOutcome.LockedOut(7, "a@example.com", lockoutEnd, wasNewlyLockedOut: true));

        Assert.Equal(lockoutEnd, Assert.IsType<AdminLoginResult.LockedOut>(result).LockoutEndUtc);
        Assert.Equal([AuditActions.TwoFactorFailed, AuditActions.Lockout], audit.Entries.Select(e => e.Action));
    }

    [Fact]
    public async Task A_code_against_an_already_locked_account_writes_only_two_factor_failed()
    {
        var lockoutEnd = new DateTimeOffset(2026, 9, 29, 12, 15, 0, TimeSpan.Zero);

        var (_, audit, _) = await RunAsync(AdminAuthenticationOutcome.LockedOut(7, "a@example.com", lockoutEnd, wasNewlyLockedOut: false));

        Assert.Equal(AuditActions.TwoFactorFailed, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task A_stale_pending_sign_in_is_expired_and_not_audited()
    {
        var (result, audit, _) = await RunAsync(AdminAuthenticationOutcome.TwoFactorSessionExpired(7, "a@example.com"));

        Assert.IsType<AdminLoginResult.TwoFactorSessionExpired>(result);
        Assert.Empty(audit.Entries);
    }
}
