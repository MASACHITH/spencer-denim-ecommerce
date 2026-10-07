namespace SpencerDenim.Application.Sales.Payments;

/// <summary>
/// The one bank-transfer setting that has no table to live in yet (PAY-01, doc 11 P7).
/// </summary>
/// <remarks>
/// <b>It was <c>CheckoutOptions</c> in <c>Application.Sales.Checkout</c>, and moving it is the point of
/// PAY-03.</b> A class called "checkout options" whose only property is
/// <c>BankTransferExpiryHours</c> is a bank-transfer fact sitting in the namespace that is supposed not to
/// know what a bank transfer is — and <c>CheckoutNamesNoProviderTests</c> is what found it, which is the
/// whole reason that test exists. It is read by <c>BankTransferProvider</c> and by nothing else.
/// <para>
/// <b>The configuration section moved with it, from <c>Checkout</c> to <c>Orders</c>.</b> That is doc 08
/// §3.8's own group name for this key (<c>Orders.BankTransferExpiryHours</c>), so the environment variable
/// is now <c>Orders__BankTransferExpiryHours</c> and reads as the setting it stands in for. Nothing in the
/// repository, the deployment files or the documentation set the old name — only W3-02's journal mentioned
/// it, as owed work — so nothing had to be migrated.
/// </para>
/// <para>
/// <b>Still configuration rather than a setting, and still owed to ADM-22.</b> Doc 08 §9 seeds
/// <c>Orders.BankTransferExpiryHours = 48</c> in <c>ops.Settings</c> and doc 11 P7's permissions table
/// puts "expiry hours" under <c>Settings.Edit</c>; this task added the <c>Bank.*</c> group because doc 11
/// P7 makes it a <i>precondition</i> of taking money at all, and left the <c>Orders</c> group alone
/// because the value already works and a half-built settings group is worse than a working environment
/// variable. The move is one options class deleted and one settings read added.
/// </para>
/// </remarks>
public sealed class BankTransferOptions
{
    /// <summary>
    /// The configuration section, which is doc 08 §3.8's group name for the key.
    /// </summary>
    public const string SectionName = "Orders";

    /// <summary>
    /// How long a bank-transfer order is held before the expiry job may release its stock (doc 08 §9's
    /// <c>Orders.BankTransferExpiryHours</c>, doc 11 P7: "due date and time (placed + 48 h)").
    /// </summary>
    /// <remarks>
    /// Unlike W3-02, something now enforces it: <c>ExpireUnpaidOrders</c> runs every five minutes and
    /// releases the stock and the promo use of any order past this window (doc 08 §4.1, §5.6).
    /// </remarks>
    public int BankTransferExpiryHours { get; set; } = 48;
}
