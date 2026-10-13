using SpencerDenim.Domain.Media;

namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.Payments</c> (doc 08 §3.5, PAY-01..03 / ADM-09) [RV]: one attempt to pay for one order.
/// <para>
/// <b>Several rows per order are normal</b>, not an error state: a rejected bank transfer closes its row
/// and a resubmission opens a new one (N7), a PayPal retry cancels the abandoned attempt and starts
/// another, and a late or duplicate capture lands as its own row with
/// <see cref="PaymentStatus.CapturedRequiresRefund"/>. Two indexes carry the whole invariant —
/// <c>UX_Payments_OneSettlement</c> (at most one row may be the one that settles the order) and
/// <c>UX_Payments_OneOpen</c> (at most one Initiated or Pending attempt).
/// </para>
/// <para>
/// <b>Not audited</b>: doc 08 §3.5 marks it [RV] and not [A], so there are no <c>…ByUserId</c> audit
/// columns. Who reviewed a bank transfer is recorded explicitly instead, in
/// <see cref="ApprovedByUserId"/> — a business fact, not an audit trail.
/// </para>
/// <para>
/// <b>No provider calls, no transitions, no <c>IPaymentProvider</c> in this task</b> (steps 2 and 3).
/// This is the shape §4.2 and §4.3 need in order to be implementable.
/// </para>
/// </summary>
public sealed class Payment
{
    /// <summary><c>varchar(100)</c>: the provider's own id for the attempt (<see cref="ProviderReference"/>).</summary>
    public const int ProviderReferenceMaxLength = 100;

    /// <summary>
    /// <c>varchar(100)</c>: the provider's id for the money (<see cref="ProviderTransactionId"/>).
    /// <b><c>varchar</c>, not <c>nvarchar</c></b>, and it sits in a unique index with the provider code, so
    /// text outside the code page is not stored as given but folded to <c>?</c> — two different ids can then
    /// collide. Anything that writes it must therefore hold it to printable ASCII, which every real
    /// gateway's ids are.
    /// </summary>
    public const int ProviderTransactionIdMaxLength = 100;

    /// <summary><c>nvarchar(100)</c>: <see cref="PayerReference"/>.</summary>
    public const int PayerReferenceMaxLength = 100;

    /// <summary><c>varchar(50)</c>: <see cref="FailureCode"/>.</summary>
    public const int FailureCodeMaxLength = 50;

    /// <summary><c>nvarchar(500)</c>: <see cref="FailureMessage"/>.</summary>
    public const int FailureMessageMaxLength = 500;

    public int Id { get; set; }

    public int OrderId { get; set; }

    public Order? Order { get; set; }

    /// <summary>FK <c>sales.PaymentProviders</c>. See <see cref="PaymentProviderCodes"/>.</summary>
    public string ProviderCode { get; set; } = string.Empty;

    public PaymentProvider? PaymentProvider { get; set; }

    /// <summary>CK 0–9 (doc 08 §3.5, §4.3).</summary>
    public PaymentStatus Status { get; set; }

    /// <summary>
    /// DF 0. True on the one payment that settles the order.
    /// <para>
    /// It exists so that "the order is paid" is a fact about a specific row rather than a search for a
    /// Succeeded one: doc 08 §3.5 replaced an older "one succeeded per order" index with this, because a
    /// duplicate capture is also Succeeded-shaped money and must be allowed to exist — as status 7 with
    /// this flag 0, <b>outside</b> <c>UX_Payments_OneSettlement</c>. The CK
    /// <c>IsOrderSettlement = 0 OR Status IN (3,8,9)</c> stops the flag being set on a row that is not
    /// (or is no longer) settled money.
    /// </para>
    /// </summary>
    public bool IsOrderSettlement { get; set; }

    /// <summary>LKR, equal to the order's <c>GrandTotal</c>.</summary>
    public decimal Amount { get; set; }

    /// <summary><c>char(3)</c>, CK <c>LKR</c>.</summary>
    public string Currency { get; set; } = SalesCurrency.Lkr;

    /// <summary>
    /// <c>USD</c> for PayPal (D1), <see langword="null"/> for a bank transfer. <b>Deliberately no CK</b>:
    /// this is the one currency column that is allowed to differ from LKR.
    /// </summary>
    public string? ProviderCurrency { get; set; }

    /// <summary>
    /// <c>round(Amount / ExchangeRate × (1 + FxBufferPercent/100), 2)</c>, shown to the customer before
    /// the redirect. §4.2 step 3.1 compares the captured amount against <i>this</i> value, so it must be
    /// the figure PayPal was actually asked for and never recomputed from today's rate.
    /// </summary>
    public decimal? ProviderAmount { get; set; }

    /// <summary>LKR per 1 USD, admin-set, frozen at initiation. <c>decimal(18,6)</c> (doc 08 §1.4).</summary>
    public decimal? ExchangeRate { get; set; }

    /// <summary>Default 3.00. <c>decimal(5,2)</c>, the percent type of doc 08 §1.4.</summary>
    public decimal? FxBufferPercent { get; set; }

    /// <summary>The PayPal order ID. Filtered unique, so one provider order cannot back two payments.</summary>
    public string? ProviderReference { get; set; }

    /// <summary>The PayPal capture ID. Filtered unique: the same capture must never settle twice.</summary>
    public string? ProviderTransactionId { get; set; }

    /// <summary>The bank reference the customer typed, or the PayPal payer ID.</summary>
    public string? PayerReference { get; set; }

    /// <summary>
    /// FK <c>media.MediaAssets</c> — the optional bank slip (D3), a <b>private</b> asset. One current
    /// file: a re-upload replaces it (doc 08 §3.3 and doc 11 P7 step 3), which is also why this is a
    /// single FK and not a collection.
    /// </summary>
    public int? ProofAssetId { get; set; }

    public MediaAsset? ProofAsset { get; set; }

    /// <summary>
    /// When the capture was claimed. <c>IX_Payments_Capturing</c> leads on it so the recovery job can find
    /// rows stuck in <see cref="PaymentStatus.Capturing"/> for more than two minutes (N1).
    /// </summary>
    public DateTime? CaptureStartedAtUtc { get; set; }

    /// <summary>
    /// <c>REJECTED</c>, <c>INSTRUMENT_DECLINED</c>, <c>ORDER_NOT_PAYABLE</c>, and R9's
    /// <c>AMOUNT_MISMATCH</c> — which §4.2 step 7 treats as unacceptable rather than merely failed.
    /// </summary>
    public string? FailureCode { get; set; }

    public string? FailureMessage { get; set; }

    /// <summary>The staff member who approved or rejected a bank transfer (doc 11 P7 step 6).</summary>
    public int? ApprovedByUserId { get; set; }

    public DateTime? ApprovedAtUtc { get; set; }

    public string? ApprovalNote { get; set; }

    /// <summary>DF 0, in LKR. How much of <see cref="Amount"/> has gone back.</summary>
    public decimal RefundedAmount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>[RV] (doc 08 §3.5).</summary>
    public byte[]? RowVersion { get; set; }

    public ICollection<Refund> Refunds { get; } = [];
}
