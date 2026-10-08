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
    /// Whether this host has enough configuration to attempt a send
    /// (<c>SmtpOptions.IsConfigured</c>).
    /// <para>
    /// <b>Checked before a sweep claims anything, not caught as an exception after it.</b> An
    /// unconfigured shop must leave its queue exactly as it found it: not Failed, not Dead, not retried
    /// four hours apart against a host that does not exist, and above all not Sent.
    /// </para>
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Hands one message to the mail server. Throws on any failure — the caller records the message on
    /// the outbox row and applies the backoff.
    /// </summary>
    Task SendAsync(OutboundEmail email, CancellationToken cancellationToken);
}
