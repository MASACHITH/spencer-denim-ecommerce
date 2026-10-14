using System.Globalization;
using System.Text;
using SpencerDenim.Application.Ops.Email.Contracts;

namespace SpencerDenim.Application.Ops.Email;

/// <summary>
/// Turns the facts of a customer account event into doc 11 P11's four emails: <c>EmailConfirmation</c>,
/// <c>Welcome</c>, <c>AccountExists</c> and <c>PasswordReset</c>.
/// </summary>
/// <remarks>
/// <b>It is pure, like <see cref="OrderEmailComposer"/>, and for the same reason.</b> Nothing here reads
/// a database, a clock or a request; every value arrives on <see cref="AccountEmailFacts"/> or on
/// <see cref="EmailOptions"/>. So the unit suite asserts the bodies themselves — that no body carries a
/// token when no origin is configured, that no body promises a message the shop may not be able to send,
/// and that no body spells the shop's name out.
/// <para>
/// <b>The three rules <see cref="OrderEmailComposer"/> states apply here unchanged</b>, and the second
/// one bites hardest on this set: <i>nothing is promised that this build cannot do</i>. A promise made
/// <i>inside</i> a sent message is safe, because a reader holding it proves the transport works — so
/// <see cref="AccountExists"/> may say "use the forgot-password link", which is a page and not an email.
/// It may <b>not</b> say "we have sent you a reset email", because the shop has not sent one.
/// </para>
/// <para>
/// <b>Why <see cref="EmailConfirmation"/> and <see cref="PasswordReset"/> take a non-null link while the
/// other two take nothing.</b> Their entire content is the link. Composing one without it would produce a
/// message whose instructions are missing, so the type refuses instead and the caller skips the queue
/// (see <c>AccountEmailOutbox</c>).
/// </para>
/// </remarks>
public sealed class AccountEmailComposer(EmailOptions options)
{
    /// <summary>The storefront link that confirms an address, or null when no origin is configured.</summary>
    /// <remarks>
    /// <c>/account/confirm-email</c> is the storefront's own route (<c>app.routes.ts</c>) and the token is
    /// the one-time value minted into <c>identity.UserTokens</c>. It travels as a query parameter rather
    /// than a path segment because it is a credential and not an address: GLB-11's friendly-URL rule is
    /// about pages a crawler and a shopper read, and this link names no page content of its own.
    /// </remarks>
    public string? ConfirmEmailUrl(string token) =>
        options.NormalisedStorefrontBaseUrl is { } origin
            ? $"{origin}/account/confirm-email?token={Uri.EscapeDataString(token)}"
            : null;

    /// <summary>The storefront link that sets a new password, or null when no origin is configured.</summary>
    public string? ResetPasswordUrl(string token) =>
        options.NormalisedStorefrontBaseUrl is { } origin
            ? $"{origin}/account/reset-password?token={Uri.EscapeDataString(token)}"
            : null;

    /// <summary>Where a customer signs in. Null when no origin is configured.</summary>
    public string? SignInUrl =>
        options.NormalisedStorefrontBaseUrl is { } origin ? $"{origin}/account/sign-in" : null;

    /// <summary>Where a customer asks for a reset link. Null when no origin is configured.</summary>
    public string? ForgotPasswordUrl =>
        options.NormalisedStorefrontBaseUrl is { } origin ? $"{origin}/account/forgot-password" : null;

    /// <summary>
    /// Doc 11 P11 step A.3 / step 10: confirm this address.
    /// </summary>
    /// <remarks>
    /// <b>It says what confirming is <i>for</i>, which is the one thing a customer cannot guess.</b> Doc
    /// 11 §7 G-24 lets an unconfirmed customer sign in, shop and check out, so "confirm or you cannot use
    /// your account" would be false. What confirmation actually buys is doc 08 §3.1 D5: the orders they
    /// placed as a guest with this address become theirs. A customer who is told that has a reason to
    /// click; one who is told nothing has none.
    /// </remarks>
    public ComposedEmail EmailConfirmation(AccountEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var link = Required(facts.Link, nameof(EmailConfirmation));

        const string title = "Confirm your email address";

        var html = new StringBuilder();
        var text = new StringBuilder();

        html.AppendLine(EmailBody.Paragraph(facts.To.Greeting));
        text.AppendLine(facts.To.Greeting);
        text.AppendLine();

        const string why =
            "Confirming your address links any orders you placed as a guest with it to your account, so "
            + "they appear in your order history with their tracking details.";

        html.AppendLine(EmailBody.Paragraph(why));
        text.AppendLine(why);
        text.AppendLine();

        html.AppendLine(EmailBody.Button(link, "Confirm my email address"));
        text.AppendLine("Confirm my email address:");
        text.AppendLine(link);
        text.AppendLine();

        var window = ValidFor(facts.LinkValidHours);
        html.AppendLine(EmailBody.Paragraph(window));
        text.AppendLine(window);
        text.AppendLine();

        const string unasked =
            "If you did not create an account, you can ignore this message and nothing will happen.";

        html.AppendLine(EmailBody.Paragraph(unasked));
        text.AppendLine(unasked);

        return new ComposedEmail(
            title,
            EmailBody.Html(title, "One link, and your guest orders join your account.", html.ToString().TrimEnd()),
            EmailBody.Text(title, text.ToString()));
    }

    /// <summary>
    /// Doc 11 P11 step A.3 / §4's <c>Welcome</c>: the account is ready.
    /// </summary>
    /// <remarks>
    /// <b>It carries no token and no link that is a credential</b>, which is why doc 08 §3.8's G-03 rule
    /// does not destroy its body on send: it is the record of what the shop told a new customer, and it
    /// is kept for the ordinary thirty days.
    /// <para>
    /// <b>It does not say "we have also sent you a confirmation link", even though the same transaction
    /// queues one.</b> That promise would be true at the moment of composing and false in the one case
    /// that matters: <see cref="EmailConfirmation"/> is skipped when no storefront origin is configured,
    /// and the two are separate rows that a reader may receive in either order or only one of. So this
    /// message points at the account instead, and the confirmation message explains itself.
    /// </para>
    /// </remarks>
    public ComposedEmail Welcome(AccountEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        const string title = "Your account is ready";

        var html = new StringBuilder();
        var text = new StringBuilder();

        html.AppendLine(EmailBody.Paragraph(facts.To.Greeting));
        text.AppendLine(facts.To.Greeting);
        text.AppendLine();

        const string body =
            "Your account is set up. You can sign in with your email address and the password you chose, "
            + "save delivery addresses, and follow your orders from one place.";

        html.AppendLine(EmailBody.Paragraph(body));
        text.AppendLine(body);
        text.AppendLine();

        if (SignInUrl is { } signIn)
        {
            html.AppendLine(EmailBody.Button(signIn, "Sign in"));
            text.AppendLine("Sign in:");
            text.AppendLine(signIn);
        }

        return new ComposedEmail(
            title,
            EmailBody.Html(title, "You can sign in and follow your orders.", html.ToString().TrimEnd()),
            EmailBody.Text(title, text.ToString()));
    }

    /// <summary>
    /// Doc 11 P11 step A.2 / §4's <c>AccountExists</c>: somebody tried to register with an address that
    /// already has a customer account.
    /// </summary>
    /// <remarks>
    /// <b>This message is the whole of how registration stays non-enumerating</b> (doc 08 §3.1 DB10). The
    /// page says one sentence either way; the difference is visible only in the inbox of whoever owns the
    /// address, which is the only person entitled to know.
    /// <para>
    /// <b>It never says what the attempt's password was, or that one was given, or when.</b> It also does
    /// not say an attempt came from a particular place: the request's own IP address is forgeable
    /// (W1-01's finding) and would be a fact about a proxy at best.
    /// </para>
    /// </remarks>
    public ComposedEmail AccountExists(AccountEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        const string title = "You already have an account";

        var html = new StringBuilder();
        var text = new StringBuilder();

        html.AppendLine(EmailBody.Paragraph(facts.To.Greeting));
        text.AppendLine(facts.To.Greeting);
        text.AppendLine();

        const string body =
            "Somebody just tried to create an account with this email address, and there is already one. "
            + "Nothing has changed: your existing account and password still work.";

        html.AppendLine(EmailBody.Paragraph(body));
        text.AppendLine(body);
        text.AppendLine();

        if (SignInUrl is { } signIn)
        {
            html.AppendLine(EmailBody.Button(signIn, "Sign in"));
            text.AppendLine("Sign in:");
            text.AppendLine(signIn);
            text.AppendLine();
        }

        // A page, not a message: "use the forgot-password page" is honest whatever the mail transport is
        // doing, where "we have emailed you a reset link" would not be.
        if (ForgotPasswordUrl is { } forgot)
        {
            const string forgotten = "If you have forgotten your password, you can set a new one here:";

            html.AppendLine(EmailBody.Paragraph(forgotten));
            html.AppendLine(EmailBody.Button(forgot, "Set a new password"));
            text.AppendLine(forgotten);
            text.AppendLine(forgot);
        }

        return new ComposedEmail(
            title,
            EmailBody.Html(title, "Nothing has changed; your account still works.", html.ToString().TrimEnd()),
            EmailBody.Text(title, text.ToString()));
    }

    /// <summary>
    /// Doc 11 P11 step C.8 / §4's <c>PasswordReset</c>.
    /// </summary>
    /// <remarks>
    /// <b>Body destroyed on send</b> (doc 08 §3.8 G-03,
    /// <c>EmailOutboxMessage.BodyIsClearedOnSend</c>): the link is a credential, so it must not sit in
    /// the outbox table or in a backup once it has left.
    /// <para>
    /// <b>It states what happens to existing sessions</b>, because that is the surprising part: setting a
    /// new password bumps the security stamp, so every browser that was signed in is signed out. A
    /// customer who is not told that reads it as the site having broken.
    /// </para>
    /// </remarks>
    public ComposedEmail PasswordReset(AccountEmailFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var link = Required(facts.Link, nameof(PasswordReset));

        const string title = "Set a new password";

        var html = new StringBuilder();
        var text = new StringBuilder();

        html.AppendLine(EmailBody.Paragraph(facts.To.Greeting));
        text.AppendLine(facts.To.Greeting);
        text.AppendLine();

        const string body = "Use this link to choose a new password for your account.";

        html.AppendLine(EmailBody.Paragraph(body));
        text.AppendLine(body);
        text.AppendLine();

        html.AppendLine(EmailBody.Button(link, "Set a new password"));
        text.AppendLine("Set a new password:");
        text.AppendLine(link);
        text.AppendLine();

        var window = ValidFor(facts.LinkValidHours);
        html.AppendLine(EmailBody.Paragraph(window));
        text.AppendLine(window);
        text.AppendLine();

        const string effect =
            "Setting a new password signs you out everywhere else, so any browser still signed in will "
            + "ask for the new one.";

        html.AppendLine(EmailBody.Paragraph(effect));
        text.AppendLine(effect);
        text.AppendLine();

        const string unasked =
            "If you did not ask for this, you can ignore this message. Your password has not changed and "
            + "nobody can change it without this link.";

        html.AppendLine(EmailBody.Paragraph(unasked));
        text.AppendLine(unasked);

        return new ComposedEmail(
            title,
            EmailBody.Html(title, "One link, valid once.", html.ToString().TrimEnd()),
            EmailBody.Text(title, text.ToString()));
    }

    /// <summary>
    /// "This link can be used once, within the next <i>n</i> hours."
    /// </summary>
    /// <remarks>
    /// Singular and plural are both written out. A sentence reading "within the next 1 hours" is the sort
    /// of thing a shopper notices and a developer never does, because the default is 2 and nobody runs
    /// the other branch.
    /// </remarks>
    private static string ValidFor(int hours) =>
        hours == 1
            ? "This link can be used once, within the next hour."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"This link can be used once, within the next {hours} hours.");

    private static string Required(string? link, string composer) =>
        link ?? throw new InvalidOperationException(
            $"{composer} cannot be composed without a link: its entire content is that link, and "
            + "Email:StorefrontBaseUrl is what one is built from. The caller must skip queueing instead "
            + "of sending a message the customer cannot act on (see AccountEmailOutbox).");
}
