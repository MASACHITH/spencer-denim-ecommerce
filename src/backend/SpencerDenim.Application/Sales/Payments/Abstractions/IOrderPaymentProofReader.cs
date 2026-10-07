namespace SpencerDenim.Application.Sales.Payments.Abstractions;

/// <summary>
/// What the customer has already sent about their payment, for the order page to show back (doc 11 P7
/// step 3).
/// <para>
/// <b>A provider-neutral port, and that is not decoration — <c>CheckoutNamesNoProviderTests</c> found the
/// alternative.</b> <c>GetOrderQueryHandler</c> first took <c>IBankTransferReader</c> directly, which put a
/// gateway's name in the signature of the order read and made PAY-03's claim false in the one file most
/// likely to be copied. The question the order page is asking is "has the customer sent us anything about
/// paying", which is a question about an order; that a bank transfer is the only provider that currently
/// answers it is an implementation fact and belongs behind this line.
/// </para>
/// <para>
/// <b>Not on <see cref="IPaymentProvider"/> either.</b> A proof is not something a provider knows — it is a
/// row on the payment, written by the customer — and putting it there would force a gateway that takes no
/// evidence to implement a method that can only answer null.
/// </para>
/// </summary>
public interface IOrderPaymentProofReader
{
    /// <summary>
    /// The evidence on the order's current payment, or <see langword="null"/> when there is no payment to
    /// attach anything to.
    /// </summary>
    Task<OrderPaymentProof?> FindAsync(Guid orderPublicId, CancellationToken cancellationToken);
}

/// <summary>
/// Evidence a customer submitted for a payment.
/// </summary>
/// <param name="Reference">The reference they typed, or null.</param>
/// <param name="FilePublicId">
/// The uploaded file's token, or null. It <b>identifies</b> the file and does not open it: the download
/// route is keyed on the order, because a private asset has no public URL (doc 08 §3.3).
/// </param>
/// <param name="FileName">What they called it, so a page can say which file is on the order.</param>
/// <param name="ContentType">So a page knows what it is before fetching it.</param>
/// <param name="SubmittedAtUtc">When it arrived.</param>
public sealed record OrderPaymentProof(
    string? Reference,
    Guid? FilePublicId,
    string? FileName,
    string? ContentType,
    DateTime? SubmittedAtUtc);
