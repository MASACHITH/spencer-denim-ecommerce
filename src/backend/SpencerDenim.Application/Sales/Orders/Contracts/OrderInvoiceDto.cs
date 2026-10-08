namespace SpencerDenim.Application.Sales.Orders.Contracts;

/// <summary>
/// One order's invoice and packing copy (ADM-09, doc 11 P9 step 3).
/// <para>
/// <b>The invoice number is not issued here.</b> It is allocated inside the Paid transaction, gaplessly
/// (doc 08 §4.1), so this is a rendering of a document that already exists. An order with no number has no
/// invoice, which is why <see cref="InvoiceNumber"/> is not nullable and the query answers "not available"
/// instead.
/// </para>
/// <para>
/// <b>It shows no VAT line</b> (doc 08 D2, doc 11 P9): prices are VAT-inclusive, so a VAT row would be
/// double-counting. <see cref="InvoiceSellerDetails.VatNumber"/> is the seller's registration number in
/// the header and never a calculation.
/// </para>
/// </summary>
/// <param name="InvoiceNumber">The gapless number, e.g. <c>INV-2026-000045</c>.</param>
/// <param name="IssuedAtUtc">When it was issued, which is when the order was paid.</param>
/// <param name="OrderNumber">The order it belongs to.</param>
/// <param name="OrderPublicId">The order's token, for the link back.</param>
/// <param name="PlacedAtUtc">When the order was placed.</param>
/// <param name="Seller">
/// The store's legal details, <b>every field of which may be <see langword="null"/></b> — see
/// <see cref="InvoiceSellerDetails"/>.
/// </param>
/// <param name="Customer">Who it is billed to.</param>
/// <param name="Delivery">Where it is going.</param>
/// <param name="Money">What was charged.</param>
/// <param name="Lines">What was bought.</param>
/// <param name="PaymentMethodLabel">
/// How it was paid, in words — "Bank transfer" rather than <c>BankTransfer</c>. From the settlement
/// payment's provider, so a rejected attempt never names the method on the document.
/// </param>
/// <param name="PaymentReference">
/// The reference the customer gave their bank, where there is one. It is on the invoice because it is what
/// reconciles this document against a bank statement.
/// </param>
public sealed record OrderInvoiceDto(
    string InvoiceNumber,
    DateTime IssuedAtUtc,
    string OrderNumber,
    Guid OrderPublicId,
    DateTime PlacedAtUtc,
    InvoiceSellerDetails Seller,
    AdminOrderCustomerDto Customer,
    AdminOrderDeliveryDto Delivery,
    AdminOrderMoneyDto Money,
    IReadOnlyList<AdminOrderLineDto> Lines,
    string PaymentMethodLabel,
    string? PaymentReference);

/// <summary>
/// The seller's details as they must appear on an invoice (doc 08 §3.5 <c>InvoiceSellerSnapshotJson</c>,
/// §4.1).
/// </summary>
/// <remarks>
/// <b>Every field is nullable, and an unset field prints nothing at all.</b> That is the whole point of
/// this type rather than four strings with defaults. These values are the client's — the registered name,
/// the registered address, the BR and VAT numbers — and this project has twice shipped invented client
/// data as a Major finding. An invoice is a legal document: an invented registered address on one is
/// worse than a blank somebody can see and ask about, and a "placeholder" that renders is indistinguishable
/// from data to whoever reads the printout.
/// <para>
/// <b>Where they come from, and the one thing that is owed.</b> Today they are read <i>live</i> from the
/// <c>Store.*</c> settings. Doc 08 §4.1 wants them frozen on to the order as
/// <c>InvoiceSellerSnapshotJson</c> at the moment the number is issued, so that reprinting a two-year-old
/// invoice shows that year's registration details — and <b>that column is still null on every order ever
/// paid</b>, because W3-03 could not write a snapshot of keys that did not exist. This renderer therefore
/// uses the snapshot when there is one and falls back to the live settings when there is not, which is the
/// only honest behaviour available: it is right for every invoice issued from now on, and for the ones
/// already issued it shows today's details and says so is impossible to tell apart. Writing the snapshot
/// at issue is one statement in <c>BankTransferReviewStore.IssueInvoiceAsync</c> and is owed to whoever
/// touches that transaction next.
/// </para>
/// </remarks>
/// <param name="LegalName">
/// The registered company name. <b>Not the shop's name</b> — everything a shopper reads says Colombo
/// Wardrobe (docs/02 §I2); an invoice names the company that issues it.
/// </param>
/// <param name="AddressLines">
/// The registered address, already split into lines, or <b>empty</b> when unset. Empty rather than null so
/// a renderer can loop without a second null check, and the "print nothing" rule is then the natural
/// consequence of an empty loop rather than something each renderer has to remember.
/// </param>
/// <param name="BRNumber">The business registration number.</param>
/// <param name="VatNumber">The VAT registration number. No VAT line is calculated from it.</param>
/// <param name="IsComplete">
/// Whether the client has filled in the legal name and the address — the two a printed invoice is
/// unusable without.
/// <para>
/// It exists so the <i>admin</i> screen can warn a staff member before they print ("the invoice will not
/// show the company's details until they are filled in in Settings"), which is the one place saying so
/// helps. The document itself still prints, because a staff member may want a packing copy today and the
/// legal details next week.
/// </para>
/// </param>
public sealed record InvoiceSellerDetails(
    string? LegalName,
    IReadOnlyList<string> AddressLines,
    string? BRNumber,
    string? VatNumber,
    bool IsComplete)
{
    /// <summary>The empty set: nothing filled in, so an invoice prints none of it.</summary>
    public static InvoiceSellerDetails None { get; } = new(null, [], null, null, false);
}
