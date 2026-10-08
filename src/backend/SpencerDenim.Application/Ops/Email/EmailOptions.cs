namespace SpencerDenim.Application.Ops.Email;

/// <summary>
/// Everything the shop needs in order to send an email, from configuration (<c>CLAUDE.md</c>: "Config via
/// environment variables (Azure-portable)").
/// <para>
/// <b>Why configuration and not <c>ops.Settings</c>, which is where doc 08 §3.8 puts these keys.</b> That
/// document files <c>Email.Smtp.*</c> under the <c>Settings.Security</c> permission, and
/// <c>Settings.Security</c> "applies only through the Super Admin role's <c>GrantsAll</c> flag" with a
/// sensitive-change alert, an <c>ops.AdminAlerts</c> banner and the dual-send of N2 behind every write.
/// None of that machinery exists, and a settings group with no screen and no alert would be a worse place
/// for a mail password than an environment variable the host already protects. The property names below
/// are doc 08's key names, so the move is a reader swapped in behind this class.
/// </para>
/// <para>
/// <b>The whole section may be absent, and that is a supported state rather than a misconfiguration.</b>
/// We do not have the client's mail credentials. An unconfigured shop queues its emails and sends none:
/// see <see cref="SmtpOptions.IsConfigured"/> and <c>SendEmails</c>.
/// </para>
/// </summary>
public sealed class EmailOptions
{
    /// <summary>
    /// The configuration section. Environment variables are therefore <c>Email__Smtp__Host</c>,
    /// <c>Email__StorefrontBaseUrl</c> and so on — <c>Email:Smtp</c> being doc 08 §3.8's own group name.
    /// </summary>
    public const string SectionName = "Email";

    /// <summary>The mail server, or an empty <see cref="SmtpOptions"/> when the section is absent.</summary>
    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>
    /// The storefront's origin, e.g. <c>https://colombowardrobecompany.com</c>, with no trailing slash.
    /// <para>
    /// <b>Every link in every email is built from this, and when it is unset the emails carry no links at
    /// all.</b> An email is the one place a relative path is useless, and the API cannot work the origin
    /// out for itself: taking it from a request's <c>Host</c> header would put a forgeable value into a
    /// message we send on the shop's behalf, which is the hole W1-01 closed for IP addresses, and
    /// guessing from the domain constant would be inventing configuration. So an unset value costs the
    /// links and nothing else — the order number, the amount and the bank details are all still there —
    /// and <c>SendEmails</c> says so once per process.
    /// </para>
    /// </summary>
    public string? StorefrontBaseUrl { get; set; }

    /// <summary>
    /// How often <c>SendEmails</c> sweeps, in seconds.
    /// <para>
    /// <b>Thirty seconds, and doc 11 §1.4's job table gives this job no interval</b> (unlike
    /// <c>ExpireUnpaidOrders</c>'s five minutes and <c>AutoDeliverShipped</c>'s daily), so it is a
    /// decision rather than a figure. A newly queued row is due immediately, so the sweep interval
    /// <i>is</i> the delay a shopper waits for their order confirmation; a minute of that looks broken
    /// when the page has already said the order is placed. It costs one seek on
    /// <c>IX_EmailOutbox_Due</c> per sweep when there is nothing to do. It is configurable because a
    /// container with a dozen replicas may want it longer, not because a shop would tune it.
    /// </para>
    /// </summary>
    public int SweepSeconds { get; set; } = 30;

    /// <summary>
    /// How many messages one sweep claims. Doc 08 §3.8: "the worker claims batches with
    /// <c>UPDATE TOP (20) … OUTPUT</c>".
    /// </summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// How long a sender's lease on a claimed row lasts, in minutes.
    /// <para>
    /// Longer than any sane SMTP conversation and shorter than the first rung of the backoff ladder
    /// would be ideal, and those two cannot both hold: the first rung is one minute. Five minutes is the
    /// safer end — a lease that expires while the send is still in flight would let a second sender send
    /// the same message twice, which is the one failure this field exists to prevent, whereas a lease
    /// that outlives a crashed process only delays that message by a few minutes.
    /// </para>
    /// </summary>
    public int LeaseMinutes { get; set; } = 5;

    /// <summary>
    /// How often the "SMTP is not configured and <i>n</i> messages are waiting" warning repeats, in
    /// minutes.
    /// <para>
    /// Thirty, for <c>ExpireUnpaidOrders</c>'s reason: "a job that logs its failure once per cycle for a
    /// day is a job whose log nobody reads". At the default sweep that is one line per thirty minutes
    /// instead of sixty per thirty minutes, which is still unmissable in a day's log and still tells an
    /// operator the count is growing.
    /// </para>
    /// </summary>
    public int UnconfiguredWarningMinutes { get; set; } = 30;

    /// <summary>
    /// <see cref="StorefrontBaseUrl"/> without its trailing slash, or <see langword="null"/> when it is
    /// not set.
    /// </summary>
    public string? NormalisedStorefrontBaseUrl =>
        string.IsNullOrWhiteSpace(StorefrontBaseUrl) ? null : StorefrontBaseUrl.Trim().TrimEnd('/');
}

/// <summary>
/// The <c>Email.Smtp</c> group of doc 08 §3.8, property for property: <c>Host</c>, <c>Port</c>,
/// <c>Username</c>, <c>Password</c>, <c>FromAddress</c>, <c>FromName</c>, <c>UseStartTls</c>.
/// </summary>
public sealed class SmtpOptions
{
    public string? Host { get; set; }

    /// <summary>587 by default: the submission port, which is what <see cref="UseStartTls"/> goes with.</summary>
    public int Port { get; set; } = 587;

    /// <summary>Null for a relay that authenticates by IP address rather than by credentials.</summary>
    public string? Username { get; set; }

    /// <summary>
    /// The mail password. <b>Never logged and never written to an outbox row</b> — doc 08 §3.8 logs a
    /// secret's change as <c>"[secret changed]"</c>, and the same discipline applies to anything this
    /// class touches.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>The envelope sender. Required: a message with no From cannot be sent.</summary>
    public string? FromAddress { get; set; }

    /// <summary>
    /// The display name beside <see cref="FromAddress"/>, or null to send the address alone.
    /// <para>
    /// <b>It is not defaulted to the shop's name.</b> A From name is part of what lands in a stranger's
    /// inbox and belongs to whoever owns the mailbox. The shop's name <i>heads and signs</i> every message
    /// either way (<c>EmailBody.Html</c> and <c>EmailBody.Text</c>) — it is deliberately not in any
    /// subject line, which carries the order number instead.
    /// </para>
    /// </summary>
    public string? FromName { get; set; }

    /// <summary>
    /// Whether to upgrade the connection with STARTTLS. <b>True by default</b>: a shop's order emails and
    /// its mail password must not cross the network in the clear because a setting was left out.
    /// </summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>
    /// Whether there is enough here to attempt a send: a host and an address to send from.
    /// <para>
    /// <b>Username and password are not required</b> — a relay on a private network or an Azure
    /// Communication Services connection may need neither — and a host with no <c>FromAddress</c> is not
    /// usable, so both of the two that are checked really are necessary.
    /// </para>
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}
