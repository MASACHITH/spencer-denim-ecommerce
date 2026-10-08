namespace SpencerDenim.Domain.Ops;

/// <summary>
/// <c>ops.EmailOutbox.TemplateCode</c> values (doc 08 §3.8). <c>varchar(50)</c> with <b>no check
/// constraint</b> — doc 08 lists the codes in the column's notes and pins nothing in the database, so a
/// template added by a later task is a new constant here and not a migration.
/// <para>
/// <b>Only the six order emails are declared.</b> Doc 08 lists sixteen codes, five of which are declared
/// here — <see cref="OrderCancelled"/> is the sixth and is <i>not</i> one of doc 08's — so the eleven
/// below are the rest of doc 08's list (this said "the ten that are not here" and then named eleven: the
/// arithmetic counted six declared codes against a list that holds five of them. W3-06's tester). They
/// belong to tasks that are not built — <c>Welcome</c>, <c>EmailConfirmation</c>, <c>AccountExists</c>,
/// <c>PasswordReset</c> and <c>StaffEmailChanged</c> are W3-01's and P14's,
/// <c>SensitiveSettingsChanged</c> needs <c>ops.AdminAlerts</c> and the dual-send of doc 08 §3.8 N2,
/// <c>StaffInvite</c> and <c>BreakGlassUsed</c> are P14's, and <c>PaymentException</c>,
/// <c>CaptureRecoveryFailed</c> and <c>ReconciliationDrift</c> are PayPal's and the nightly jobs'. A
/// constant with no composer and no caller would read as a template this build can send.
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

    /// <summary><c>varchar(50)</c>, doc 08 §3.8.</summary>
    public const int MaxLength = 50;
}
