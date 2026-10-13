using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.Abstractions;

/// <summary>
/// The payment attempt an order is waiting on right now, read by the order's public token (PAY-03).
/// <para>
/// <b>Why this exists.</b> <c>IPaymentProvider.DescribeAsync</c> is asked on every view of an unpaid order
/// page. A provider that redirects needs to know <i>which</i> attempt it is describing — its own order id,
/// the id that will come back in a notification — and the order alone does not say, because an order can
/// hold several payment rows (a retry cancels the abandoned one and opens another, doc 08 §3.5 N7). So the
/// page asks this port for the live row and hands its id and the provider's reference to the provider
/// through <see cref="Contracts.PaymentInstructionsRequest"/>.
/// </para>
/// <para>
/// <b>A read port of its own and not a member of <c>ICheckoutReader</c>.</b> The checkout reader answers
/// "what was ordered"; this answers "what is the customer being asked to pay against", a different
/// question that only the order page puts, and it keeps <c>PaymentStatus</c> out of the checkout namespace
/// (<c>CheckoutNamesNoProviderTests</c> allows checkout no payment-status fields, because a status is the
/// provider's fact and not checkout's).
/// </para>
/// </summary>
public interface IPaymentAttemptReader
{
    /// <summary>
    /// The newest payment of the order that has neither succeeded nor been closed — status
    /// <see cref="PaymentStatus.Initiated"/>, <see cref="PaymentStatus.Pending"/> or
    /// <see cref="PaymentStatus.Capturing"/> — or <see langword="null"/> when there is none (or no such
    /// order).
    /// </summary>
    /// <remarks>
    /// <b>Capturing is included although it is outside <c>UX_Payments_OneOpen</c>.</b> That index leaves it
    /// out for a reason about the database (doc 08 §3.5: a re-claim must not collide with its own row),
    /// not because the attempt is over — a captured-but-unconfirmed payment is the one the customer is
    /// waiting on, and a provider answering "your payment is being processed" needs to see it. Newest
    /// first because a retry can leave a Capturing row beside a newer Initiated one (doc 08 §3.5), and the
    /// newer is the one the customer would pay against.
    /// </remarks>
    Task<PaymentAttempt?> FindInFlightAsync(Guid orderPublicId, CancellationToken cancellationToken);
}

/// <summary>One payment row, as far as a provider describing it needs to know.</summary>
/// <param name="PaymentId"><c>sales.Payments.Id</c>.</param>
/// <param name="Status">The row's status — always one of Initiated, Pending, Capturing.</param>
/// <param name="ProviderReference">
/// <c>sales.Payments.ProviderReference</c>: the provider's own id for the attempt, or null before the
/// provider has created one.
/// </param>
public sealed record PaymentAttempt(int PaymentId, PaymentStatus Status, string? ProviderReference);
