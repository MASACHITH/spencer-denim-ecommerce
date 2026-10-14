using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Ops.Email.Contracts;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.Infrastructure.Ops.Email;

/// <summary>
/// Composes each of doc 11 P11's account emails and enlists it into the caller's transaction (ACC-01).
/// </summary>
/// <remarks>
/// <b>Plumbing and one decision, which is where the two token-bearing messages are refused.</b> It is
/// <see cref="OrderEmailOutbox"/>'s shape — what a message says is the composer's, whether it is queued
/// at all is the caller's — with the single exception that this class may answer "no": a confirmation or
/// a reset whose link cannot be built is not queued, because its entire content is that link.
/// </remarks>
internal sealed class AccountEmailOutbox(IEmailOutboxWriter writer, AccountEmailComposer composer)
    : IAccountEmailOutbox
{
    public bool EnlistEmailConfirmation(AccountEmailFacts facts, string token)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(token);

        if (composer.ConfirmEmailUrl(token) is not { } link)
        {
            return false;
        }

        Enlist(EmailTemplateCodes.EmailConfirmation, facts, composer.EmailConfirmation(facts with { Link = link }));
        return true;
    }

    public void EnlistWelcome(AccountEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        Enlist(EmailTemplateCodes.Welcome, facts, composer.Welcome(facts));
    }

    public void EnlistAccountExists(AccountEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        Enlist(EmailTemplateCodes.AccountExists, facts, composer.AccountExists(facts));
    }

    public bool EnlistPasswordReset(AccountEmailFacts facts, string token)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(token);

        if (composer.ResetPasswordUrl(token) is not { } link)
        {
            return false;
        }

        Enlist(EmailTemplateCodes.PasswordReset, facts, composer.PasswordReset(facts with { Link = link }));
        return true;
    }

    /// <summary>
    /// The outbox row, in the one state doc 08 §3.8 allows an insert to write: <c>Pending</c>, nothing
    /// attempted, due now.
    /// </summary>
    /// <remarks>
    /// <b>No <c>RelatedEntityType</c> / <c>RelatedEntityId</c>.</b> Doc 08 §3.8 gives that pair as "a
    /// loose pair with no FK" for an order, so the <c>EmailDeliveryFailed</c> alert can name the order a
    /// staff member must open. An account email is about no order, and the only thing it <i>could</i>
    /// carry is the customer's user id — which would put a durable pointer to a person on a table doc 08
    /// §3.8 lists as "never audited" precisely because its bodies are sensitive, and would survive the
    /// body being destroyed. <see cref="EmailOutboxMessage.ToEmail"/> already identifies the recipient
    /// for as long as the row lives, and the PDPA routine deletes these rows by that column (§3.1 step
    /// 2); a second identifier would be a second thing to scrub.
    /// <para>
    /// <b>No <c>DeduplicationKey</c> either, and this is the one place doc 08 says one would belong</b>
    /// — <see cref="EmailOutboxMessage.DeduplicationKey"/>: "it is left for the account emails, where a
    /// customer pressing 'send me a new link' twice is an ordinary thing to do". It is deliberately not
    /// used, because a <i>unique violation</i> is the wrong way to express that: the second press must
    /// mint a fresh single-use token (the first is invalidated by the mint, see
    /// <c>CustomerAccountTokens</c>), so the second message is a different message and must be sent. A
    /// dedup key would suppress the only link that still works. Rate limiting is the right control for
    /// repeated presses, and it is named in the task report as outstanding — doc 11 §7 G-41 asks for the
    /// same thing on the guest order link and nothing rate-limits that either.
    /// </para>
    /// </remarks>
    private void Enlist(string templateCode, AccountEmailFacts facts, ComposedEmail composed) =>
        writer.Enlist(new EmailOutboxMessage
        {
            TemplateCode = templateCode,
            ToEmail = facts.To.Email,
            ToName = facts.To.DisplayName,
            Subject = composed.Subject,
            BodyHtml = composed.Html,
            BodyText = composed.Text,
            Status = EmailOutboxStatus.Pending,
            AttemptCount = 0,
            MaxAttempts = EmailBackoff.MaxAttempts,
            NextAttemptAtUtc = facts.NowUtc,
            CreatedAtUtc = facts.NowUtc,
        });
}
