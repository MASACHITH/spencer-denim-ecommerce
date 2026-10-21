using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Authentication;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Settings.Admin;

/// <summary>
/// "Send test email" on the email account form: sends one message to the signed-in Super Admin through the account
/// <b>as typed on the form</b>, before or without saving it.
/// </summary>
/// <param name="Fields">The form's text fields, by body name. Absent and blank are the same here: nothing is saved.</param>
/// <param name="UseStartTls">The switch; absent means on.</param>
/// <param name="Actor">The signed-in staff user — the only address a test can go to.</param>
public sealed record SendSmtpTestEmailCommand(
    IReadOnlyDictionary<string, string?> Fields,
    bool? UseStartTls,
    AdminActor Actor);

/// <summary>What the test send came to.</summary>
public abstract record SmtpTestResult
{
    private SmtpTestResult()
    {
    }

    /// <summary>The server accepted the message for <paramref name="ToEmail"/>.</summary>
    public sealed record Sent(string ToEmail) : SmtpTestResult;

    /// <summary>A field is wrong; nothing was attempted. 400.</summary>
    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : SmtpTestResult;

    /// <summary>The caller may not use this form. 403.</summary>
    public sealed record Forbidden : SmtpTestResult;

    /// <summary>The attempt failed; <paramref name="Message"/> is a safe sentence, never the server's own words. 422.</summary>
    /// <param name="Message">What the Super Admin is shown.</param>
    /// <param name="FailureType">The exception's type name, for the endpoint's log line, or null.</param>
    /// <param name="SmtpStatus">The numeric SMTP status where the server gave one, for the log line.</param>
    public sealed record Failed(string Message, string? FailureType = null, int? SmtpStatus = null) : SmtpTestResult;
}

/// <summary>
/// Sends the test email (ADM-22, 2026-10-21).
/// <para>
/// <b>Straight to the transport, not through the outbox.</b> The point is an answer while the Super Admin is looking at
/// the form — "it worked" or "the server refused the password" — and a queued row would answer thirty seconds later,
/// on a different screen. It is outside any transaction (doc 08 §10.3: SMTP is never called inside one) and it
/// changes nothing: no row, no audit entry, no alert. It goes only to the caller's own address, read from the user
/// table rather than the cookie, so the form cannot be used to mail a stranger.
/// </para>
/// <para>
/// <b>The password box keeps its "blank keeps the saved one" meaning here too</b>: blank tests with the stored password,
/// so a Super Admin can test a saved account without retyping it. The password is never logged and never returned.
/// </para>
/// <para>
/// <b>A failure is a fixed sentence chosen from the kind of failure</b>, never the exception's message: an SMTP server's
/// reply text and a socket error both describe the server's network, and neither belongs on a web page or in a log
/// line. The endpoint's log line carries the exception's type and the numeric SMTP status only.
/// </para>
/// </summary>
public sealed class SendSmtpTestEmailCommandHandler(
    ISmtpSettingsReader smtpReader,
    ISecurityAlertRecipients recipients,
    IEmailTransport transport,
    IEffectivePermissionsProvider permissionsProvider,
    IClock clock)
    : ICommandHandler<SendSmtpTestEmailCommand, SmtpTestResult>
{
    /// <summary>How long a test may take before it is called a timeout.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<SmtpTestResult> HandleAsync(SendSmtpTestEmailCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Actor.UserId is not { } userId)
        {
            return new SmtpTestResult.Forbidden();
        }

        var permissions = await permissionsProvider.GetEffectivePermissionsAsync(userId, cancellationToken);

        if (!permissions.Has(Permissions.Settings.Security))
        {
            return new SmtpTestResult.Forbidden();
        }

        var errors = new List<ValidationError>();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, field) in new[]
                 {
                     (SettingKeys.Smtp.Host, UpdateSmtpSettingsCommandHandler.HostField),
                     (SettingKeys.Smtp.Port, UpdateSmtpSettingsCommandHandler.PortField),
                     (SettingKeys.Smtp.Username, UpdateSmtpSettingsCommandHandler.UsernameField),
                     (SettingKeys.Smtp.FromAddress, UpdateSmtpSettingsCommandHandler.FromAddressField),
                     (SettingKeys.Smtp.FromName, UpdateSmtpSettingsCommandHandler.FromNameField),
                 })
        {
            command.Fields.TryGetValue(field, out var raw);
            values[key] = UpdateSmtpSettingsCommandHandler.CleanField(errors, key, field, raw);
        }

        command.Fields.TryGetValue(UpdateSmtpSettingsCommandHandler.PasswordField, out var rawPassword);
        var typedPassword = UpdateSmtpSettingsCommandHandler.CleanPassword(errors, rawPassword);

        var useStartTls = command.UseStartTls ?? true;
        values[SettingKeys.Smtp.UseStartTls] = useStartTls ? "true" : "false";

        if (values[SettingKeys.Smtp.Host] is null)
        {
            errors.Add(new ValidationError(
                UpdateSmtpSettingsCommandHandler.HostField,
                "Enter the mail server to test."));
        }

        errors.AddRange(UpdateSmtpSettingsCommandHandler.CrossFieldErrors(key => values.GetValueOrDefault(key)));

        if (errors.Count > 0)
        {
            return new SmtpTestResult.Invalid(errors);
        }

        var username = values[SettingKeys.Smtp.Username];
        var password = username is null
            ? null
            : typedPassword ?? await smtpReader.ReadStoredPasswordAsync(cancellationToken);

        var account = new SmtpSettings(
            values[SettingKeys.Smtp.Host]!,
            SmtpSettingRules.ParsePort(values[SettingKeys.Smtp.Port])!.Value,
            username,
            password,
            values[SettingKeys.Smtp.FromAddress]!,
            values[SettingKeys.Smtp.FromName],
            useStartTls);

        if (await recipients.FindActiveStaffAsync(userId, cancellationToken) is not { } me)
        {
            return new SmtpTestResult.Failed(
                "Your account has no email address the test could be sent to. Ask another Super Admin to check it.");
        }

        var composed = SecurityEmailComposer.SmtpTest(me.FirstName, clock.UtcNow.UtcDateTime);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            await transport.SendAsync(
                new OutboundEmail(me.Email, me.FirstName, composed.Subject, composed.Html, composed.Text),
                account,
                timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var (message, status) = Describe(exception, timeout.IsCancellationRequested);
            return new SmtpTestResult.Failed(message, exception.GetType().Name, status);
        }

        return new SmtpTestResult.Sent(me.Email);
    }

    /// <summary>
    /// A fixed sentence for the kind of failure, and the numeric SMTP status where there is one. Never the exception's
    /// own text.
    /// </summary>
    internal static (string Message, int? Status) Describe(Exception exception, bool timedOut)
    {
        if (timedOut || exception is OperationCanceledException or TimeoutException)
        {
            return ($"The mail server did not answer within {Timeout.TotalSeconds:0} seconds. Check the server name "
                + "and the port.", null);
        }

        if (exception is SmtpException smtp)
        {
            var status = (int)smtp.StatusCode;

            if (status == 530 || smtp.StatusCode == SmtpStatusCode.MustIssueStartTlsFirst)
            {
                return ("The mail server requires an encrypted connection, or a user name and password. Switch "
                    + "encryption on and check the account details.", status);
            }

            if (status is 534 or 535)
            {
                return ("The mail server refused the user name or password.", status);
            }

            if (status is 550 or 551 or 553 or 554)
            {
                return ("The mail server would not send from this address to yours. Check the send-from address is "
                    + "one this account may use.", status);
            }

            if (smtp.StatusCode == SmtpStatusCode.GeneralFailure)
            {
                return DescribeConnection(smtp.InnerException) ?? ("Could not reach the mail server. Check the server "
                    + "name and the port.", null);
            }

            return ($"The mail server refused the message (code {status}).", status);
        }

        return DescribeConnection(exception) ?? ("The test email could not be sent. Check the settings and try again.", null);
    }

    private static (string, int?)? DescribeConnection(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AuthenticationException:
                    return ("A secure connection to the mail server could not be set up. Check the port, and whether "
                        + "this server uses STARTTLS.", null);
                case SocketException:
                    return ("Could not connect to the mail server. Check the server name and the port.", null);
                case IOException:
                    return ("The mail server closed the connection. Check the port and the encryption setting.", null);
            }
        }

        return null;
    }
}
