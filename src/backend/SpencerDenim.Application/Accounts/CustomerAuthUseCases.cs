using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Accounts.Abstractions;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Application.Accounts;

/// <summary>Doc 11 P11 step A.1: what the registration form submits (ACC-01).</summary>
/// <param name="IpAddress">
/// For the <c>Lockout</c> audit row only, and it is the server's own view of the connection — never a
/// forwarded header a client can write, which is the hole W1-01 round 3 closed.
/// </param>
public sealed record RegisterCustomerCommand(
    string? FirstName,
    string? LastName,
    string? Email,
    string? Password,
    string? Phone,
    string? IpAddress,
    string? UserAgent);

/// <summary>
/// Registration's one visible answer.
/// </summary>
/// <remarks>
/// <b>There is exactly one success shape and it carries nothing.</b> Doc 08 §3.1 DB10: "Register with an
/// existing email (either type) gives the same generic response." A result type with a field that
/// differed between the three cases would be a leak waiting for a careless endpoint; a result type with
/// no fields cannot leak. What <i>happened</i> is in the log and in <c>ops.EmailOutbox</c>, which is
/// where the integration tests look.
/// </remarks>
public sealed record RegisterCustomerResult
{
    /// <summary>The server has done whatever this address warranted. The endpoint says one sentence.</summary>
    public static RegisterCustomerResult Accepted { get; } = new();
}

/// <summary>Doc 11 P11 steps A.1–A.3.</summary>
/// <remarks>
/// <b>The handler validates and then stops knowing anything.</b> Which of doc 08 §3.1 DB10's three cases
/// the address was is decided in the store and is deliberately discarded here, so there is no variable
/// in this layer that an endpoint could be refactored into reading.
/// </remarks>
public sealed class RegisterCustomerCommandHandler(ICustomerRegistration registration)
    : ICommandHandler<RegisterCustomerCommand, Result<RegisterCustomerResult>>
{
    public async Task<Result<RegisterCustomerResult>> HandleAsync(
        RegisterCustomerCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var input = new CustomerRegistration(
            command.FirstName ?? string.Empty,
            command.LastName,
            command.Email ?? string.Empty,
            command.Password ?? string.Empty,
            command.Phone);

        var errors = CustomerAccountValidator.ValidateRegistration(input);
        if (errors.Count > 0)
        {
            return Result.Failure<RegisterCustomerResult>(errors);
        }

        // The outcome is read and dropped on purpose: see the remarks above.
        _ = await registration.RegisterAsync(input, cancellationToken);

        return Result.Success(RegisterCustomerResult.Accepted);
    }
}

/// <summary>Doc 11 P11 step B.5: the sign-in form (ACC-01).</summary>
public sealed record CustomerSignInCommand(
    string? Email,
    string? Password,
    string? IpAddress,
    string? UserAgent);

/// <summary>
/// Hands a sign-in to <see cref="ICustomerAuthenticator"/> and writes the one audit row a customer
/// sign-in produces.
/// </summary>
/// <remarks>
/// <b>One audit row, and only on a lockout.</b> Doc 08 §3.1's explicit security-event list is about staff
/// (<c>StaffDeactivated</c>, <c>AdminPasswordReset</c>, <c>RoleAssigned</c>…) and gives
/// <c>LastLoginAtUtc</c> as the column for "when did this user last sign in", which the authenticator
/// writes. Two rows this handler deliberately does <b>not</b> write, each for its own reason:
/// <list type="bullet">
/// <item><description>
/// <b><c>Login</c> per customer per visit</b> would be the highest-volume thing in <c>ops.AuditLogs</c>
/// with no screen that reads it — ADM-15's customer detail shows order history, not sign-ins — and doc 08
/// §8.4 keeps audit rows far longer than that volume warrants.
/// </description></item>
/// <item><description>
/// <b><c>LoginFailed</c> on a public form</b> would store an address somebody <i>typed</i>. For an
/// address with no account that row carries <c>UserId = null</c>, so doc 08 §3.1 step 6's anonymisation —
/// which scrubs by <c>EntityType</c> and this user's ids — cannot find it. A table of addresses that do
/// not have accounts, un-scrubbable, is the enumeration these endpoints exist to prevent, written down.
/// </description></item>
/// </list>
/// <b>Both are decisions rather than transcriptions and the task report names them</b>, because an owner
/// may want the volume for fraud work and can have it with a retention rule of its own.
/// <para>
/// <b>The lockout row is written once</b>, by the attempt the authenticator says tripped it — however
/// many requests are in flight (<c>CustomerAccountLockout</c>, and #146's staff equivalent).
/// </para>
/// </remarks>
public sealed class CustomerSignInCommandHandler(
    ICustomerAuthenticator authenticator,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<CustomerSignInCommand, Result<CustomerSignInResult>>
{
    private const string UserEntityType = "User";

    public async Task<Result<CustomerSignInResult>> HandleAsync(
        CustomerSignInCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = CustomerAccountValidator.ValidateSignIn(command.Email, command.Password);
        if (errors.Count > 0)
        {
            return Result.Failure<CustomerSignInResult>(errors);
        }

        var result = await authenticator.SignInAsync(
            command.Email!,
            command.Password!,
            cancellationToken);

        if (result is CustomerSignInResult.LockedOut { WasNewlyLockedOut: true } lockedOut)
        {
            await auditLog.WriteAsync(
                new AuditLog
                {
                    OccurredAtUtc = clock.UtcNow.UtcDateTime,

                    // Deliberately no UserId and no UserEmail on this row, and that needs saying.
                    // The authenticator reports a lockout without reporting *which account* — a result
                    // type that carried the id would make the reply "this address exists and is now
                    // locked", which is the enumeration leak the whole flow is built to avoid. So the
                    // trail records that a customer lockout happened and when; doc 08 §3.1's own
                    // LockoutEnd column on the user row records who. The task report flags the gap: an
                    // owner who wants the id here can have it, because the row is written server-side
                    // and never returned.
                    UserId = null,
                    UserEmail = null,
                    Action = AuditActions.Lockout,
                    EntityType = UserEntityType,
                    EntityId = null,
                    AfterJson = $"{{\"lockoutEndUtc\":\"{lockedOut.LockoutEndUtc.ToString("o", CultureInfo.InvariantCulture)}\"}}",
                    IpAddress = command.IpAddress,
                    UserAgent = command.UserAgent,
                },
                cancellationToken);
        }

        return Result.Success(result);
    }
}

/// <summary>Doc 11 P11 step C.8: the forgot-password form (ACC-01).</summary>
public sealed record RequestCustomerPasswordResetCommand(string? Email);

/// <summary>
/// Asks for a reset link. The answer is the same whatever the address is.
/// </summary>
/// <remarks>
/// <b>Even a validation failure has to be thought about here.</b> A blank field is refused with a field
/// error, which is safe: it says nothing about whether any address has an account. A badly-shaped address
/// is <i>not</i> refused — it is accepted and nothing is sent — because "that is not a valid address"
/// tells an attacker which strings are worth trying, and a real customer's address that passed a looser
/// check at registration must not be refused here.
/// </remarks>
public sealed class RequestCustomerPasswordResetCommandHandler(ICustomerPasswords passwords)
    : ICommandHandler<RequestCustomerPasswordResetCommand, Result<bool>>
{
    public async Task<Result<bool>> HandleAsync(
        RequestCustomerPasswordResetCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = CustomerAccountValidator.ValidateForgotPassword(command.Email);
        if (errors.Count > 0)
        {
            return Result.Failure<bool>(errors);
        }

        await passwords.RequestResetAsync(command.Email!, cancellationToken);

        return Result.Success(true);
    }
}

/// <summary>Doc 11 P11 step C.9: the reset form (ACC-01).</summary>
public sealed record ResetCustomerPasswordCommand(string? Token, string? Password);

/// <summary>Redeems a reset link and sets the password.</summary>
public sealed class ResetCustomerPasswordCommandHandler(ICustomerPasswords passwords)
    : ICommandHandler<ResetCustomerPasswordCommand, Result<CustomerPasswordResetResult>>
{
    public async Task<Result<CustomerPasswordResetResult>> HandleAsync(
        ResetCustomerPasswordCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(command.Token))
        {
            errors.Add(new ValidationError("token", "This link is missing its token."));
        }

        if (string.IsNullOrEmpty(command.Password))
        {
            errors.Add(new ValidationError("password", "Choose a password."));
        }
        else if (command.Password.Length > CustomerAccountValidator.PasswordMaxLength)
        {
            errors.Add(new ValidationError(
                "password",
                $"A password can be at most {CustomerAccountValidator.PasswordMaxLength} characters."));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<CustomerPasswordResetResult>(errors);
        }

        // The length and character rules are checked inside the store, against the user the token names,
        // and come back as `WeakPassword` — not as field errors here. Identity's validators need the user
        // (one of them refuses a password that contains the user name), and this layer does not have one
        // until the token has been verified.
        var result = await passwords.ResetAsync(command.Token!, command.Password!, cancellationToken);

        return Result.Success(result);
    }
}

/// <summary>Doc 11 P11 step 10: a signed-in customer changes their own password (ACC-01).</summary>
/// <param name="UserId">From the session cookie. Never from the request body.</param>
public sealed record ChangeCustomerPasswordCommand(
    int UserId,
    string? CurrentPassword,
    string? NewPassword,
    string? IpAddress,
    string? UserAgent);

/// <summary>Changes a customer's own password.</summary>
public sealed class ChangeCustomerPasswordCommandHandler(
    ICustomerPasswords passwords,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<ChangeCustomerPasswordCommand, Result<CustomerPasswordChangeResult>>
{
    private const string UserEntityType = "User";

    public async Task<Result<CustomerPasswordChangeResult>> HandleAsync(
        ChangeCustomerPasswordCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();

        if (string.IsNullOrEmpty(command.CurrentPassword))
        {
            errors.Add(new ValidationError("currentPassword", "Enter your current password."));
        }

        if (string.IsNullOrEmpty(command.NewPassword))
        {
            errors.Add(new ValidationError("newPassword", "Choose a new password."));
        }
        else if (command.NewPassword.Length > CustomerAccountValidator.PasswordMaxLength)
        {
            errors.Add(new ValidationError(
                "newPassword",
                $"A password can be at most {CustomerAccountValidator.PasswordMaxLength} characters."));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<CustomerPasswordChangeResult>(errors);
        }

        var result = await passwords.ChangeAsync(
            command.UserId,
            command.CurrentPassword!,
            command.NewPassword!,
            cancellationToken);

        if (result is CustomerPasswordChangeResult.LockedOut { WasNewlyLockedOut: true } lockedOut)
        {
            // Here the id *is* recorded, and the difference from the sign-in row is the point: this
            // caller is already holding a valid session for the account, so naming it in the trail
            // reveals nothing a successful request would not.
            await auditLog.WriteAsync(
                new AuditLog
                {
                    OccurredAtUtc = clock.UtcNow.UtcDateTime,
                    UserId = command.UserId,
                    UserEmail = null,
                    Action = AuditActions.Lockout,
                    EntityType = UserEntityType,
                    EntityId = command.UserId.ToString(CultureInfo.InvariantCulture),
                    AfterJson = $"{{\"lockoutEndUtc\":\"{lockedOut.LockoutEndUtc.ToString("o", CultureInfo.InvariantCulture)}\",\"reason\":\"ChangePassword\"}}",
                    IpAddress = command.IpAddress,
                    UserAgent = command.UserAgent,
                },
                cancellationToken);
        }

        return Result.Success(result);
    }
}

/// <summary>Doc 11 P11 step A.4: redeeming a confirmation link (ACC-01).</summary>
/// <param name="SignedInUserId">
/// The customer the confirming browser is signed in as, from the session cookie, or null. Confirming
/// requires one — see <see cref="CustomerEmailConfirmationResult.SignInRequired"/>.
/// </param>
public sealed record ConfirmCustomerEmailCommand(string? Token, int? SignedInUserId);

/// <summary>Confirms an address and links the guest orders behind it (doc 08 §3.1 D5).</summary>
public sealed class ConfirmCustomerEmailCommandHandler(ICustomerEmailConfirmation confirmation)
    : ICommandHandler<ConfirmCustomerEmailCommand, CustomerEmailConfirmationResult>
{
    public async Task<CustomerEmailConfirmationResult> HandleAsync(
        ConfirmCustomerEmailCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The session first, before the token is looked at: a click from a signed-out browser reads,
        // spends and links nothing, so the link survives for the account's real owner.
        if (command.SignedInUserId is not { } signedInUserId)
        {
            return new CustomerEmailConfirmationResult.SignInRequired();
        }

        return string.IsNullOrWhiteSpace(command.Token)
            ? new CustomerEmailConfirmationResult.InvalidToken()
            : await confirmation.ConfirmAsync(command.Token, signedInUserId, cancellationToken);
    }
}

/// <summary>"Send me a new confirmation link" (doc 11 P11; ACC-01).</summary>
/// <param name="UserId">From the session cookie. A customer may only ask for their own.</param>
public sealed record ResendCustomerConfirmationCommand(int UserId);

/// <summary>
/// Mints a fresh confirmation token and queues the message.
/// </summary>
/// <remarks>
/// <b>It answers the same whether or not anything was queued.</b> Nothing is queued when the address is
/// already confirmed or when no storefront origin is configured, and neither is a fact the screen can do
/// anything with: the first means the customer has what they wanted, and the second is a shop
/// configuration gap that an operator sees in the log (<c>CustomerAccountLog</c>) and a shopper cannot
/// act on. What the screen must not do is promise an email — see the endpoint's wording and the two
/// Majors that came from exactly that.
/// </remarks>
public sealed class ResendCustomerConfirmationCommandHandler(ICustomerRegistration registration)
    : ICommandHandler<ResendCustomerConfirmationCommand, bool>
{
    public async Task<bool> HandleAsync(
        ResendCustomerConfirmationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return await registration.ResendConfirmationAsync(command.UserId, cancellationToken);
    }
}
