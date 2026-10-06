namespace SpencerDenim.Application.Sales.Checkout;

/// <summary>
/// The two settings checkout needs that have no table to live in yet.
/// </summary>
/// <remarks>
/// <b>These belong in <c>ops.Settings</c> and are configuration until they can get there.</b> Doc 08 §9
/// seeds <c>Orders.BankTransferExpiryHours = 48</c> and doc 02's ADM-22 puts "order payment-expiry hours"
/// on the settings screen; <c>ops.Settings</c> is ADM-22's table and is not in any migration (it is open in
/// PR #104, which this branch deliberately does not depend on). Doc 06 requires configuration through
/// environment variables, so the value is read from <c>Checkout__BankTransferExpiryHours</c> with doc 08's
/// own default, and <b>ADM-22 owes the move</b>: one options class deleted, one settings read added.
/// <para>
/// The alternative — a constant in the placement code — would have put a number the client is entitled to
/// change behind a deployment.
/// </para>
/// </remarks>
public sealed class CheckoutOptions
{
    public const string SectionName = "Checkout";

    /// <summary>
    /// How long a bank-transfer order is held before the expiry job may release its stock (doc 08 §9's
    /// <c>Orders.BankTransferExpiryHours</c>, doc 11 P6: "the order will wait for payment for 48 hours").
    /// <para>
    /// <b>Checkout only writes the deadline; nothing in this task enforces it.</b> The
    /// <c>ExpireUnpaidOrders</c> job is doc 08 §4.1/§5.6 and belongs to W3-03's step 3, so until that
    /// exists an unpaid order holds its stock indefinitely. That is in the task report as owed work, not
    /// hidden here.
    /// </para>
    /// </summary>
    public int BankTransferExpiryHours { get; set; } = 48;
}
