using SpencerDenim.Application.Ops.Email.Contracts;
using SpencerDenim.Application.Sales.Payments.Contracts;

namespace SpencerDenim.Application.Ops.Email.Abstractions;

/// <summary>
/// The six order emails, each enlisted into the caller's own transaction: doc 11 §4.1's five, and
/// <see cref="EnlistOrderCancelled"/>, which is <b>not</b> one of §4.1's — §4.4 lists "order cancelled"
/// among the events with no notification in v1. (This summary read "the six order emails of doc 11 §4.1",
/// which attributes the cancellation email to the document that excludes it, in the same file whose
/// member remark below flags it as a scope increase. Corrected by W3-06's tester.)
/// </summary>
/// <remarks>
/// <b>Why this sits between the stores and <see cref="IEmailOutboxWriter"/>.</b> The writer takes a
/// finished <c>EmailOutboxMessage</c> — a subject, two bodies and a recipient. Deciding what those say is
/// composition, and composition needs the shop's name, the links' origin and a money format; a store that
/// is in the middle of a payment transaction should not be assembling HTML. So each method here takes the
/// facts the store already has and does the rest.
/// <para>
/// <b>Six methods rather than one <c>Enlist(request)</c>, and the methods are not symmetrical.</b> Each
/// email needs different facts — lines and totals for a placement, an invoice number for a confirmation,
/// a courier for a dispatch — so one method taking a union of all of them would let a caller pass a
/// shipment to a cancellation. The asymmetry is the type system doing the checking.
/// </para>
/// <para>
/// <b>Nothing here returns anything.</b> A queued email is not a fact a use case can act on: whether it
/// is ever sent is <c>SendEmails</c>'s business minutes later, and a caller that could see a failure here
/// would be tempted to fail the order for it.
/// </para>
/// </remarks>
public interface IOrderEmailOutbox
{
    /// <summary>Doc 11 P6 step 10 / §4.1 <c>OrderPlaced</c>: every order, every payment method.</summary>
    void EnlistOrderPlaced(PlacedOrderEmailFacts facts);

    /// <summary>
    /// Doc 11 P7 step 1 / §4.1 <c>BankTransferInstructions</c>: the account to pay into, the exact
    /// amount, the order number to quote, the deadline and the slip-upload link.
    /// </summary>
    /// <param name="instructions">
    /// The payment provider's own answer (<c>IPaymentProvider.DescribeAsync</c>). <b>The caller must not
    /// call this method when that answer was <see langword="null"/></b> — it is null exactly when the
    /// shop's bank details are unset, and an email telling a customer to transfer money to a blank
    /// account number is worse than no email. The two callers that could reach this check it, and
    /// <c>OrderEmailOutboxTests</c> pins the behaviour.
    /// </param>
    void EnlistPaymentInstructions(OrderEmailHeader header, PaymentInstructions instructions);

    /// <summary>Doc 11 P7 steps 6a and 8, P10 step 11 / §4.1 <c>PaymentConfirmed</c>.</summary>
    void EnlistPaymentConfirmed(PaymentConfirmedEmailFacts facts);

    /// <summary>Doc 11 P9 step 5 / §4.1 <c>OrderShipped</c>, in its courier and its pickup wording (G-35).</summary>
    void EnlistOrderShipped(ShippedOrderEmailFacts facts);

    /// <summary>Doc 11 P7 step 7, P10 flow B / §4.1 <c>OrderExpired</c>.</summary>
    void EnlistOrderExpired(OrderEmailHeader header);

    /// <summary>
    /// Doc 11 P10 flow A. <b>Not a v1 commitment of doc 08 §3.8 or doc 11 §4</b> — see
    /// <c>EmailTemplateCodes.OrderCancelled</c>, which carries the scope note in full.
    /// </summary>
    void EnlistOrderCancelled(CancelledOrderEmailFacts facts);
}
