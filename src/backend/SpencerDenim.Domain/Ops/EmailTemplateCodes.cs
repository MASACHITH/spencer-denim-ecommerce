namespace SpencerDenim.Domain.Ops;

/// <summary>
/// <c>ops.EmailOutbox.TemplateCode</c> values (doc 08 §3.8). <c>varchar(50)</c> with <b>no check
/// constraint</b> — doc 08 lists the codes in the column's notes and pins nothing in the database, so a
/// template added by a later task is a new constant here and not a migration.
/// <para>
/// <b>Eleven codes are declared: the six order emails, W3-01's four account emails and
/// <see cref="SensitiveSettingsChanged"/> (ADM-22, 2026-10-21, with doc 08 §3.8 N2's dual send).</b> Doc 08 lists
/// sixteen, of which ten are declared here — <see cref="OrderCancelled"/> is the eleventh and is <i>not</i>
/// one of doc 08's — so the six below are the rest of doc 08's list. They belong to tasks that are not
/// built: <c>StaffEmailChanged</c> and <c>StaffInvite</c> and <c>BreakGlassUsed</c> are P14's, and <c>PaymentException</c>,
/// <c>CaptureRecoveryFailed</c> and <c>ReconciliationDrift</c> are PayPal's and the nightly jobs'. A
/// constant with no composer and no caller would read as a template this build can send.
/// </para>
/// <para>
/// <b>W3-01 declared the four account codes and <see cref="EmailOutboxMessage.BodyIsClearedOnSend"/> now
/// reads two of them rather than its own literals</b>, which is the hand-off W3-06's tester wrote into
/// <c>OrderEmailOutboxTests</c>: the G-03 privacy rule is keyed on the constants it is about, so renaming
/// one cannot silently take the rule off the template it was written for.
/// </para>
/// </summary>
public static class EmailTemplateCodes
{
    /// <summary>Doc 11 P6 step 10 / §4.1: every order, every payment method.</summary>
    public const string OrderPlaced = "OrderPlaced";

    /// <summary>
    /// Doc 11 P7 step 1: the bank details, the exact amount, the order number to quote, the deadline and
    /// the slip-upload link. Bank-transfer orders only.
    /// </summary>
    public const string BankTransferInstructions = "BankTransferInstructions";

    /// <summary>Doc 11 P7 step 6a and step 8, and P10 step 11: the order became Paid.</summary>
    public const string PaymentConfirmed = "PaymentConfirmed";

    /// <summary>Doc 11 P9 step 5: dispatched, with the courier and the tracking link, or ready to collect.</summary>
    public const string OrderShipped = "OrderShipped";

    /// <summary>Doc 11 P7 step 7 / P10 flow B: nobody paid in time and the items went back on sale.</summary>
    public const string OrderExpired = "OrderExpired";

    /// <summary>
    /// A customer or a staff member closed an unpaid order (doc 11 P10 flow A).
    /// <para>
    /// <b>This code is not in doc 08 §3.8's list, and doc 11 §4.4 puts "order cancelled" among the events
    /// with no notification in v1.</b> It is built on the owner's instruction for W3-06, which is doc 11
    /// §7 <b>G-14</b>'s own recommendation ("Recommend at least Bank transfer rejected, <b>Order
    /// cancelled</b>, Order refunded; client to decide") taken up for the cancellation only. G-14's owner
    /// is the <i>client</i> and the row is still open, so this is a scope increase of one template that
    /// the task report names: doc 11 §4.1 and §4.4 and doc 08 §3.8's code list all need the line adding,
    /// or this constant, its composer and its one call site in <c>OrderCancellationStore</c> removing.
    /// Nothing else in the system reads it.
    /// </para>
    /// </summary>
    public const string OrderCancelled = "OrderCancelled";

    /// <summary>
    /// Doc 11 P11 step A.3 / doc 08 §3.8: the link that confirms a new customer's address, and the link
    /// sent to a <i>new</i> address when a customer changes their email.
    /// <para>
    /// <b>Its body is destroyed the moment it is sent</b> (G-03, <see cref="EmailOutboxMessage.BodyIsClearedOnSend"/>):
    /// the link carries a single-use token, so it must not sit in the outbox table or in a backup.
    /// </para>
    /// </summary>
    public const string EmailConfirmation = "EmailConfirmation";

    /// <summary>
    /// Doc 11 P11 step C.8 / doc 08 §3.8: the password-reset link. <b>Body destroyed on send</b> (G-03),
    /// for the same reason as <see cref="EmailConfirmation"/>.
    /// </summary>
    public const string PasswordReset = "PasswordReset";

    /// <summary>
    /// Doc 11 P11 step A.3 / doc 08 §3.8: the "your account is ready" message a new customer gets beside
    /// the confirmation link.
    /// <para>
    /// <b>It carries no token, so its body is kept for the ordinary thirty days.</b> That is the point of
    /// splitting it from <see cref="EmailConfirmation"/> rather than sending one message: the record of
    /// what the shop told a new customer survives, and the credential does not.
    /// </para>
    /// </summary>
    public const string Welcome = "Welcome";

    /// <summary>
    /// Doc 11 P11 step A.2 / doc 08 §3.8: sent to an address that <b>already has a customer account</b>
    /// when somebody tries to register with it again — "sign in or reset your password".
    /// <para>
    /// <b>This message is the whole of how registration stays non-enumerating.</b> The page says the same
    /// sentence either way (doc 08 §3.1 DB10); the difference between a free address and a taken one is
    /// visible only in the inbox of whoever owns the address, which is exactly who is entitled to know.
    /// A staff match sends nothing at all.
    /// </para>
    /// </summary>
    public const string AccountExists = "AccountExists";

    /// <summary>
    /// Doc 08 §3.8 DB2/N2: a payment, mail or security setting was changed. Sent to every active Super Admin, naming
    /// the keys, the staff user, the time and the IP address — never a value.
    /// <para>
    /// <b>For a mail-account change it is queued twice per recipient</b>: once through the new account, and once
    /// through the previous one, carried as ciphertext in <see cref="EmailOutboxMessage.TransportOverrideProtected"/>.
    /// A hijacked change therefore cannot silence its own warning. <b>No UI or API cancels or deletes it</b> (doc 08
    /// §3.8); it can only be retried.
    /// </para>
    /// </summary>
    public const string SensitiveSettingsChanged = "SensitiveSettingsChanged";

    /// <summary><c>varchar(50)</c>, doc 08 §3.8.</summary>
    public const int MaxLength = 50;
}
