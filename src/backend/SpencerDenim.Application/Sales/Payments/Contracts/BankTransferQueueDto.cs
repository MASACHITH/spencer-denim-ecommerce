namespace SpencerDenim.Application.Sales.Payments.Contracts;

/// <summary>
/// One page of "Orders → Bank transfers awaiting approval" (doc 11 P7 step 4, ADM-09).
/// </summary>
/// <param name="Rows">Oldest first.</param>
/// <param name="Page">1-based.</param>
/// <param name="PageSize">How many rows a page holds.</param>
/// <param name="TotalCount">
/// Every transfer awaiting approval, not only this page's. The count is what tells staff whether the queue
/// is under control, and doc 11 §6.1 has them reading it twice a day.
/// </param>
public sealed record BankTransferQueuePage(
    IReadOnlyList<BankTransferQueueRow> Rows,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>
/// One row of the queue. Doc 11 P7 step 4 lists exactly what it shows: "order number, customer, amount,
/// reference, slip (if any) and time left before expiry".
/// </summary>
/// <param name="OrderPublicId">
/// The token every action and link uses. <b>Never the internal id</b> — GLB-11 keeps ids out of URLs, and
/// the storefront's own order link is this same token.
/// </param>
/// <param name="OrderNumber">Also the reference the customer was told to quote.</param>
/// <param name="CustomerName">First and last name as the order snapshot holds them.</param>
/// <param name="CustomerEmail">For matching a credit against a name on a statement.</param>
/// <param name="AmountLkr">The exact amount to look for, which is the order's grand total.</param>
/// <param name="Currency">Always <c>LKR</c> for a bank transfer; sent so a page never assumes.</param>
/// <param name="PayerReference">
/// The bank reference the customer typed, or <see langword="null"/>. P7's exceptions cover the no-reference
/// case: staff match by amount, name and date.
/// </param>
/// <param name="SlipAssetPublicId">
/// The uploaded slip's token, or <see langword="null"/>. It is a <b>private</b> asset: this token only
/// opens through the authorised endpoint, never through <c>/media</c>.
/// </param>
/// <param name="SlipContentType">
/// So the screen knows whether to draw an image or offer a PDF to open, without fetching the file first.
/// </param>
/// <param name="PlacedAtUtc">When the order was placed.</param>
/// <param name="PaymentDueAtUtc">When the hold lapses.</param>
/// <param name="SecondsUntilExpiry">
/// <b>Time left, computed on the server, and negative once it has passed.</b>
/// <para>
/// Sent as a number of seconds rather than as a rendered "4 h 12 m" for two reasons. A rendered string is
/// stale the moment it is serialised, and this is the one figure on the screen a staff member is making a
/// decision against; and the server owns "now" — a browser with a wrong clock would otherwise show a queue
/// whose deadlines are all wrong, and nothing on the page would say so. Negative rather than clamped to
/// zero because "overdue by eleven minutes" and "due now" are different facts to somebody deciding whether
/// to approve.
/// </para>
/// </summary>
public sealed record BankTransferQueueRow(
    Guid OrderPublicId,
    string OrderNumber,
    string CustomerName,
    string CustomerEmail,
    decimal AmountLkr,
    string Currency,
    string? PayerReference,
    Guid? SlipAssetPublicId,
    string? SlipContentType,
    DateTime PlacedAtUtc,
    DateTime PaymentDueAtUtc,
    long SecondsUntilExpiry);
