using Microsoft.Extensions.Logging;

namespace SpencerDenim.Infrastructure.Identity.Customers;

/// <summary>
/// Source-generated log messages for the customer account flows (W3-01, ACC-01/02). The rest of the
/// solution logs this way and the analyser insists on it: an interpolated log line allocates and formats
/// even when the level is switched off (<c>MediaLog</c> makes the same note).
/// </summary>
/// <remarks>
/// <b>No message here carries an email address, a password, a token or a link.</b> Every line is keyed on
/// the user id, which is the one identifier doc 08 §3.1 step 6's anonymisation can find and scrub. An
/// address in a log file is PII in a place the PDPA routine cannot reach, and a token in a log file is a
/// credential in plain text — doc 08 §3.8's G-03 destroys the email body for exactly that reason, which a
/// log line would quietly undo.
/// <para>
/// <b>And no line says which of doc 08 §3.1 DB10's cases a public request took</b> — except the two that
/// are about the shop's own configuration, which name no address either. A log an operator reads is not
/// an oracle an attacker reads, but a log that records "no account for x@y" is a list of addresses that
/// do not have accounts, which is the enumeration the endpoints exist to prevent, written down.
/// </para>
/// </remarks>
internal static partial class CustomerAccountLog
{
    [LoggerMessage(
        EventId = 7101,
        Level = LogLevel.Information,
        Message = "Customer registration refused: the address belongs to a staff account (doc 08 D7). "
            + "No account was created and no email was queued.")]
    public static partial void RegistrationBelongsToStaff(ILogger logger);

    [LoggerMessage(
        EventId = 7102,
        Level = LogLevel.Warning,
        Message = "Customer {UserId} was created but no confirmation email could be queued: "
            + "Email:StorefrontBaseUrl is not configured, so there is no link to build. Their guest "
            + "orders cannot be linked (doc 08 §3.1 D5) until it is set and they ask for a new link.")]
    public static partial void RegistrationWithoutConfirmationLink(ILogger logger, int userId);

    [LoggerMessage(
        EventId = 7103,
        Level = LogLevel.Debug,
        Message = "A password reset was asked for an address with no active customer account. Nothing "
            + "was minted and nothing was queued (doc 08 §3.1 DB10).")]
    public static partial void PasswordResetForUnknownAddress(ILogger logger);

    [LoggerMessage(
        EventId = 7104,
        Level = LogLevel.Warning,
        Message = "A password reset was asked for and no message could be queued: "
            + "Email:StorefrontBaseUrl is not configured, so there is no link to build. No token was "
            + "minted, so any link already in the customer's inbox still works.")]
    public static partial void PasswordResetWithoutLink(ILogger logger);

    [LoggerMessage(
        EventId = 7105,
        Level = LogLevel.Information,
        Message = "Customer {UserId} set a new password from a reset link.")]
    public static partial void PasswordResetRedeemed(ILogger logger, int userId);

    [LoggerMessage(
        EventId = 7106,
        Level = LogLevel.Information,
        Message = "Customer {UserId} changed their own password.")]
    public static partial void PasswordChanged(ILogger logger, int userId);

    [LoggerMessage(
        EventId = 7107,
        Level = LogLevel.Information,
        Message = "Customer {UserId} confirmed their email address; {LinkedCount} guest order(s) were "
            + "linked (doc 08 §3.1 D5).")]
    public static partial void EmailConfirmed(ILogger logger, int userId, int linkedCount);

    [LoggerMessage(
        EventId = 7108,
        Level = LogLevel.Information,
        Message = "A confirmation link for customer {UserId} named an address the account no longer "
            + "uses. The link was spent and nothing was confirmed.")]
    public static partial void ConfirmationForStaleAddress(ILogger logger, int userId);
}
