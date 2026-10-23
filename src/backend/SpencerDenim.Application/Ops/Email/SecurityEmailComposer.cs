using System.Globalization;
using System.Text;

namespace SpencerDenim.Application.Ops.Email;

/// <summary>Which copy of a <c>SensitiveSettingsChanged</c> email this is (doc 08 §3.8 N2).</summary>
public enum SensitiveChangeChannel
{
    /// <summary>A payment or security change that did not touch the mail account: one copy, current account.</summary>
    Only = 0,

    /// <summary>The copy sent through the mail account as it is <i>after</i> the change.</summary>
    NewAccount = 1,

    /// <summary>The copy sent through the mail account as it was <i>before</i> the change.</summary>
    PreviousAccount = 2,
}

/// <summary>What a <c>SensitiveSettingsChanged</c> email says. Keys, actor, time and IP address — never a value.</summary>
/// <param name="FirstName">The recipient's first name, for the greeting.</param>
/// <param name="What">The area, e.g. "Email account settings".</param>
/// <param name="Keys">The keys whose values changed. Names only.</param>
/// <param name="ActorEmail">Who made the change, as the request recorded them, or null.</param>
/// <param name="ActorIpAddress">From where, or null.</param>
/// <param name="OccurredAtUtc">When.</param>
/// <param name="Channel">Which copy this is.</param>
public sealed record SensitiveSettingsChangedEmailFacts(
    string FirstName,
    string What,
    IReadOnlyList<string> Keys,
    string? ActorEmail,
    string? ActorIpAddress,
    DateTime OccurredAtUtc,
    SensitiveChangeChannel Channel);

/// <summary>
/// The two security emails ADM-22's mail-account work sends: the sensitive-change warning (doc 08 §3.8 DB2/N2) and
/// the "Send test email" message. Pure, so a unit test asserts the whole message — including what must not be in it.
/// <para>
/// <b>No setting value is ever written into either.</b> A warning that quoted the new server or user name would hand
/// whoever reads the old mailbox the new account's details; the warning only has to say <i>that</i> something
/// changed, by whom, and where to look. The test email says nothing about the account at all.
/// </para>
/// </summary>
public static class SecurityEmailComposer
{
    public static ComposedEmail SensitiveSettingsChanged(SensitiveSettingsChangedEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var title = $"{facts.What} were changed";
        var lowered = facts.What.ToLowerInvariant();
        var subject = facts.Channel == SensitiveChangeChannel.PreviousAccount
            ? $"Security alert: {lowered} were changed (sent through the previous mail account)"
            : $"Security alert: {lowered} were changed";

        var greeting = string.IsNullOrWhiteSpace(facts.FirstName) ? "Hello," : $"Hello {facts.FirstName.Trim()},";

        var opening =
            $"{facts.What} were changed in the admin. If you or another Super Admin made this change, you need do "
            + "nothing more than acknowledge the alert on the dashboard. If nobody you know made it, sign in now, "
            + "check the settings and the audit log, and change the passwords involved.";

        var channel = facts.Channel switch
        {
            SensitiveChangeChannel.NewAccount =>
                "The mail account itself was changed, so this warning is sent twice: this copy through the new "
                + "account, and another through the account that was in use before. If only one copy reaches you, "
                + "treat it as serious.",
            SensitiveChangeChannel.PreviousAccount =>
                "The mail account itself was changed, so this copy is sent through the account that was in use "
                + "before the change, and another through the new one. If this is the only copy you receive, the new "
                + "account may not be the shop's. Check it at once.",
            _ => null,
        };

        var keys = string.Join(", ", facts.Keys);
        var when = EmailBody.ColomboDateTime(facts.OccurredAtUtc);
        var who = string.IsNullOrWhiteSpace(facts.ActorEmail) ? "Not recorded" : facts.ActorEmail.Trim();
        var where = string.IsNullOrWhiteSpace(facts.ActorIpAddress) ? "Not recorded" : facts.ActorIpAddress.Trim();
        const string noValues = "For your security, this email never contains the new values.";

        var html = new StringBuilder();
        var text = new StringBuilder();

        html.AppendLine(EmailBody.Paragraph(greeting));
        html.AppendLine(EmailBody.Paragraph(opening));

        if (channel is not null)
        {
            html.AppendLine(EmailBody.Paragraph(channel));
        }

        html.AppendLine(EmailBody.DetailBlock(
        [
            EmailBody.DetailRow("Settings changed", keys),
            EmailBody.DetailRow("Changed by", who),
            EmailBody.DetailRow("From address", where),
            EmailBody.DetailRow("When", when),
        ]));
        html.AppendLine(EmailBody.Paragraph(noValues));

        text.AppendLine(greeting);
        text.AppendLine();
        text.AppendLine(opening);
        text.AppendLine();

        if (channel is not null)
        {
            text.AppendLine(channel);
            text.AppendLine();
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"Settings changed: {keys}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Changed by: {who}");
        text.AppendLine(CultureInfo.InvariantCulture, $"From address: {where}");
        text.AppendLine(CultureInfo.InvariantCulture, $"When: {when}");
        text.AppendLine();
        text.AppendLine(noValues);

        return new ComposedEmail(
            subject,
            EmailBody.Html(title, "A security-sensitive setting was changed.", html.ToString().TrimEnd()),
            EmailBody.Text(title, text.ToString()));
    }

    /// <summary>The "Send test email" message: proof the account works, and nothing about the account.</summary>
    public static ComposedEmail SmtpTest(string? firstName, DateTime nowUtc)
    {
        const string title = "Test email";

        var greeting = string.IsNullOrWhiteSpace(firstName) ? "Hello," : $"Hello {firstName.Trim()},";
        const string body =
            "This is the test email you asked for from the shop's admin. If you are reading it, the shop can send "
            + "email with the settings you entered. Remember to save them if you have not already.";
        var when = $"Sent {EmailBody.ColomboDateTime(nowUtc)}.";

        var html = string.Join(
            Environment.NewLine,
            EmailBody.Paragraph(greeting),
            EmailBody.Paragraph(body),
            EmailBody.Paragraph(when));

        var text = string.Join(Environment.NewLine + Environment.NewLine, greeting, body, when);

        return new ComposedEmail(
            title,
            EmailBody.Html(title, "The shop can send email with these settings.", html),
            EmailBody.Text(title, text));
    }
}
