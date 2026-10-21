namespace SpencerDenim.Application.Ops.Email.Abstractions;

/// <summary>One message, ready to hand to a mail server.</summary>
/// <param name="ToName">The recipient's display name, or null to send to the bare address.</param>
/// <param name="BodyHtml">
/// The HTML part, or null for a message whose body has already been destroyed (doc 08 §3.8's thirty-day
/// rule, or the immediate rule for the two token-bearing templates). A message with neither part is not
/// sendable and <c>SendEmails</c> refuses it rather than sending an empty one.
/// </param>
public sealed record OutboundEmail(
    string ToEmail,
    string? ToName,
    string Subject,
    string? BodyHtml,
    string? BodyText);

/// <summary>
/// The SMTP boundary (doc 11 §1.4's "Email (SMTP)" actor). Implemented in Infrastructure; the only thing
/// in the system that opens a socket to a mail server.
/// </summary>
/// <remarks>
/// <b>It exists so that no test ever reaches a real mail server or a real address.</b> The unit suite
/// substitutes a fake and the integration suite registers one that records and never connects, so the
/// job, the backoff and the body rules are all provable without a mailbox anywhere. The only untested
/// code is the <c>SmtpClient</c> conversation itself, which is the smallest honest surface to leave
/// untested.
/// </remarks>
public interface IEmailTransport
{
    /// <summary>
    /// Hands one message to the mail server of <paramref name="account"/>. Throws on any failure — the caller
    /// records the message on the outbox row and applies the backoff, or (for the test send) turns it into a safe
    /// sentence.
    /// </summary>
    /// <remarks>
    /// <b>The account is a parameter, not something the transport holds</b> (2026-10-21). Until then the transport was
    /// a singleton over <c>Email__Smtp__*</c> bound once at start-up, which made the account impossible to change
    /// without a restart and impossible to vary per message. Now <c>SendEmails</c> reads the effective account each
    /// sweep (<see cref="ISmtpSettingsReader"/>) and passes it in — or passes the <i>previous</i> account carried on
    /// a row's <c>TransportOverrideProtected</c>, which is how doc 08 §3.8 N2's dual send reaches the old server.
    /// Whether there is an account at all is the reader's question, asked before a sweep claims anything.
    /// </remarks>
    Task SendAsync(OutboundEmail email, SmtpSettings account, CancellationToken cancellationToken);
}
