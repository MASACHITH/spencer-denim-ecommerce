using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;

namespace SpencerDenim.Infrastructure.Ops.Email;

/// <summary>
/// The one place in the system that opens a socket to a mail server (doc 11 §1.4's "Email (SMTP)" actor).
/// </summary>
/// <remarks>
/// <b><c>System.Net.Mail.SmtpClient</c> rather than a package.</b> Microsoft's own documentation says it
/// is not recommended for new development, and the usual replacement is MailKit — which this does not
/// take, for the reason <c>Directory.Packages.props</c> gives about every other dependency in this
/// project: the need has to earn the dependency. What this shop sends is six transactional messages over
/// plain SMTP submission with STARTTLS, which is the subset <c>SmtpClient</c> does correctly; MailKit's
/// advantages are IMAP, OAuth2 and a modern async implementation, none of which is on this path. The
/// seam is <see cref="IEmailTransport"/>, so swapping it is one class.
/// <para>
/// <b>A new client per message, not a pooled one.</b> <c>SmtpClient</c> is not thread-safe and holds a
/// connection; a sweep sends at most twenty messages a few seconds apart, so the cost of a connection
/// each is irrelevant beside the cost of a shared client that two sweeps could use at once.
/// </para>
/// <para>
/// <b>Nothing here is covered by a test that reaches a mail server, and nothing ever will be.</b> The job,
/// the backoff, the lease and the body rules are all proved against a fake
/// <see cref="IEmailTransport"/>; this class's <i>conversation</i> with a server is the one thing left
/// untested, which is the smallest honest surface to leave — and the alternative is a test suite that can
/// send mail to a real address.
/// </para>
/// </remarks>
internal sealed class SmtpEmailTransport(EmailOptions options) : IEmailTransport
{
    public bool IsConfigured => options.Smtp.IsConfigured;

    public async Task SendAsync(OutboundEmail email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);

        var smtp = options.Smtp;

        if (!smtp.IsConfigured)
        {
            // Not reachable through SendEmails, which checks IsConfigured before it claims anything. It
            // is an exception rather than a silent return because the alternative — answering "sent" to a
            // caller that would then mark the row Sent — is the single worst outcome available here: the
            // queue would empty and no customer would get anything.
            throw new InvalidOperationException(
                "SMTP is not configured, so no email can be sent. Set Email__Smtp__Host and "
                + "Email__Smtp__FromAddress (doc 08 §3.8's Email.Smtp group).");
        }

        if (string.IsNullOrWhiteSpace(email.BodyHtml) && string.IsNullOrWhiteSpace(email.BodyText))
        {
            // A message whose body has been destroyed by the thirty-day rule or by G-03's immediate rule
            // has nothing left to send, and sending an empty one would be worse than failing: the
            // recipient would get a blank message from the shop and the row would say Sent.
            throw new InvalidOperationException(
                "This message has no body, so there is nothing to send. Its body columns have been "
                + "cleared (doc 08 §3.8), which means it has already been sent once or is past its "
                + "thirty-day retention.");
        }

        using var message = new MailMessage
        {
            From = smtp.FromName is { Length: > 0 } fromName
                ? new MailAddress(smtp.FromAddress!, fromName, Encoding.UTF8)
                : new MailAddress(smtp.FromAddress!),

            Subject = email.Subject,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,

            // The text part is the body and the HTML part is the alternative view, which is the order
            // that degrades correctly: a client that understands neither header still shows readable
            // text, and every client that prefers HTML picks the alternative.
            Body = email.BodyText ?? string.Empty,
            IsBodyHtml = false,
        };

        message.To.Add(
            email.ToName is { Length: > 0 } toName
                ? new MailAddress(email.ToEmail, toName, Encoding.UTF8)
                : new MailAddress(email.ToEmail));

        if (email.BodyHtml is { Length: > 0 } html)
        {
            var view = AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html);
            message.AlternateViews.Add(view);
        }

        using var client = new SmtpClient(smtp.Host!, smtp.Port)
        {
            EnableSsl = smtp.UseStartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,

            // Default credentials are never used: a mail relay reached with the host process's Windows
            // identity is not a thing this application can be deployed into, and leaving it true would
            // make an unauthenticated send look like a configuration success.
            UseDefaultCredentials = false,
            Credentials = smtp.Username is { Length: > 0 } username
                ? new NetworkCredential(username, smtp.Password ?? string.Empty)
                : null,
        };

        // SendMailAsync's own cancellation support, so a host shutting down does not wait out a mail
        // server's timeout. A cancelled send throws, the caller records a failure, and the message is
        // retried — which is right: we do not know whether the server took it, and a duplicate order
        // confirmation is better than none.
        await client.SendMailAsync(message, cancellationToken);
    }
}
