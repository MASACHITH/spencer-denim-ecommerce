namespace SpencerDenim.Application.Sales.Payments.Abstractions;

/// <summary>
/// The customer's half of doc 11 P7 step 3: the bank reference they typed, and the slip they uploaded.
/// <para>
/// <b>Both are writes to the <i>open</i> payment row and never to a new one</b> (doc 08 §3.5 N7, DB26):
/// "while a bank payment is Pending, a new reference or slip <b>updates</b> that record; a new record is
/// created only after a rejection". The new record after a rejection is created by the customer's next
/// action, not here — and that is the one place <c>UX_Payments_OneOpen</c> can be made to fight, so see
/// <see cref="OpenPaymentAsync"/>.
/// </para>
/// </summary>
public interface IBankTransferProofStore
{
    /// <summary>
    /// Writes <c>Payments.PayerReference</c> on the order's open bank payment.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the payment is no longer <c>Pending</c> — approved, rejected, expired
    /// or cancelled between the read and this statement — so the caller can answer "this order can no
    /// longer be paid" rather than reporting a success that wrote nothing.
    /// </returns>
    Task<bool> SetReferenceAsync(
        int paymentId,
        string? reference,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Points the open bank payment at a newly uploaded slip and soft-deletes the one it held, in one
    /// transaction (doc 08 §3.3 N10: "at most one current file per payment").
    /// </summary>
    /// <returns>
    /// <see langword="false"/> on the same condition as <see cref="SetReferenceAsync"/>, and also when
    /// <paramref name="newAssetPublicId"/> names no usable private asset — which cannot happen from the
    /// upload path and is refused rather than trusted, because the alternative is a payment pointing at an
    /// asset somebody else owns.
    /// </returns>
    Task<bool> ReplaceProofAsync(
        int paymentId,
        Guid newAssetPublicId,
        int? previousAssetId,
        int? actingUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// The slip currently on a payment, re-read immediately before a replacement.
    /// </summary>
    /// <remarks>
    /// Its own call rather than a value carried from an earlier read, because two uploads in quick
    /// succession would otherwise have the second one soft-delete the asset the first had already replaced
    /// — leaving the live slip undeleted and a dead one not.
    /// </remarks>
    Task<int?> CurrentProofAssetIdAsync(int paymentId, CancellationToken cancellationToken);

    /// <summary>
    /// Ensures the order has an open <c>Pending</c> bank payment, creating one if its previous attempt was
    /// rejected, and answers its id.
    /// </summary>
    /// <remarks>
    /// <b>This is the <c>UX_Payments_OneOpen</c> question, and it is the one place in the bank flow that
    /// index can be made to fight.</b> Doc 08 §3.5 N7 says a resubmission after a rejection creates a
    /// <b>new</b> <c>Pending</c> row — and two of those at once (a customer double-tapping Upload, or
    /// uploading in two tabs) would both insert, and the second would violate the index. So the violation
    /// is <i>expected</i> rather than exceptional: it is caught, and the loser re-reads and uses the
    /// winner's row. That is the same "ask the index one more question before answering" shape
    /// <c>PlaceOrderCommandHandler.RefuseUnlessAlreadyPlacedAsync</c> uses, applied to a different index.
    /// <para>
    /// <see langword="null"/> when the order is not <c>PendingPayment</c>, which is the only state doc 08
    /// §3.3 N10 allows an upload in.
    /// </para>
    /// </remarks>
    Task<int?> OpenPaymentAsync(
        Guid orderPublicId,
        DateTime nowUtc,
        CancellationToken cancellationToken);
}
