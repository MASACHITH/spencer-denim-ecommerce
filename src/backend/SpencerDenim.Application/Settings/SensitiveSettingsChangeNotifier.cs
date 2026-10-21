using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.Application.Settings;

/// <summary>One sensitive settings change, as the notifier needs it.</summary>
/// <param name="What">The area, plural, e.g. "Email account settings".</param>
/// <param name="ChangedKeys">The keys whose values changed. Names only.</param>
/// <param name="Actor">Who changed them, from where.</param>
/// <param name="NowUtc">The change's own instant.</param>
/// <param name="MailAccountChanged">
/// True for a change to <c>Email.Smtp.*</c>: the warning then goes out through both the new and the previous account
/// (doc 08 §3.8 N2).
/// </param>
/// <param name="PreviousMailAccount">
/// The account that was in effect <i>before</i> the change (rows or configuration), or null when none was. Only read
/// when <paramref name="MailAccountChanged"/> is true.
/// </param>
public sealed record SensitiveSettingsChange(
    string What,
    IReadOnlyList<string> ChangedKeys,
    AdminActor Actor,
    DateTime NowUtc,
    bool MailAccountChanged = false,
    SmtpSettings? PreviousMailAccount = null);

/// <summary>
/// Doc 08 §3.8 DB2 and N2, in the caller's own transaction: the <c>SensitiveSettingsChanged</c> banner for every Super
/// Admin, and the <c>SensitiveSettingsChanged</c> email to every active Super Admin — <b>twice</b> for a mail-account
/// change.
/// </summary>
public interface ISensitiveSettingsChangeNotifier
{
    /// <summary>Enlists the banner and the emails. Commits nothing: the caller's save commits them with the change.</summary>
    Task EnlistAsync(SensitiveSettingsChange change, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ISensitiveSettingsChangeNotifier"/>: the banner through <see cref="IAdminAlertWriter.Enlist"/> and the
/// emails through <see cref="IEmailOutboxWriter.Enlist"/>, so a change that commits always has both and one that rolls
/// back has neither.
/// <para>
/// <b>The dual send (N2).</b> For a mail-account change each Super Admin gets two rows. The first has no override and
/// goes through whatever account is current when <c>SendEmails</c> picks it up — the new one. The second carries the
/// <i>previous</i> account as Data Protection ciphertext in <c>TransportOverrideProtected</c>
/// (<see cref="ISmtpOverrideProtector"/>), and the sweep sends it through that account even if no current account is
/// configured at all. So a hijacker who points the shop at their own server, or clears the account, still triggers a
/// warning through the server the shop really uses. When nothing was configured before, there is no previous channel
/// and the second copy is not queued: there is nobody it could reach.
/// </para>
/// <para>
/// <b>The previous account is the <i>effective</i> one</b> — the rows if they were in use, otherwise
/// <c>Email__Smtp__*</c> — because that is the channel the Super Admins were actually receiving mail through.
/// </para>
/// </summary>
public sealed class SensitiveSettingsChangeNotifier(
    IAdminAlertWriter alerts,
    IEmailOutboxWriter outbox,
    ISecurityAlertRecipients recipients,
    ISmtpOverrideProtector overrides)
    : ISensitiveSettingsChangeNotifier
{
    public async Task EnlistAsync(SensitiveSettingsChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        alerts.Enlist(SensitiveSettingsChangedAlert.For(
            change.What,
            change.ChangedKeys,
            change.Actor.UserId,
            change.NowUtc));

        var superAdmins = await recipients.ListActiveSuperAdminsAsync(cancellationToken);

        // One ciphertext for the whole change: every recipient's old-channel copy carries the same account.
        var previous = change.MailAccountChanged && change.PreviousMailAccount is { } account
            ? overrides.Protect(account)
            : null;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var recipient in superAdmins)
        {
            if (string.IsNullOrWhiteSpace(recipient.Email) || !seen.Add(recipient.Email.Trim()))
            {
                continue;
            }

            if (!change.MailAccountChanged)
            {
                Enlist(change, recipient, SensitiveChangeChannel.Only, transportOverride: null);
                continue;
            }

            Enlist(change, recipient, SensitiveChangeChannel.NewAccount, transportOverride: null);

            if (previous is not null)
            {
                Enlist(change, recipient, SensitiveChangeChannel.PreviousAccount, previous);
            }
        }
    }

    private void Enlist(
        SensitiveSettingsChange change,
        StaffAddressee recipient,
        SensitiveChangeChannel channel,
        string? transportOverride)
    {
        var composed = SecurityEmailComposer.SensitiveSettingsChanged(new SensitiveSettingsChangedEmailFacts(
            recipient.FirstName,
            change.What,
            change.ChangedKeys,
            change.Actor.Email,
            change.Actor.IpAddress,
            change.NowUtc,
            channel));

        outbox.Enlist(new EmailOutboxMessage
        {
            TemplateCode = EmailTemplateCodes.SensitiveSettingsChanged,
            ToEmail = recipient.Email.Trim(),
            ToName = string.IsNullOrWhiteSpace(recipient.FirstName) ? null : recipient.FirstName.Trim(),
            Subject = composed.Subject,
            BodyHtml = composed.Html,
            BodyText = composed.Text,
            TransportOverrideProtected = transportOverride,
            Status = EmailOutboxStatus.Pending,
            AttemptCount = 0,
            MaxAttempts = EmailBackoff.MaxAttempts,
            NextAttemptAtUtc = change.NowUtc,
            CreatedAtUtc = change.NowUtc,
        });
    }
}
