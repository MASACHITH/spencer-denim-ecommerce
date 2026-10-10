using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.Abstractions;

/// <summary>
/// PAY-03, and doc 03 §3c's port: one way to pay, behind a contract checkout does not have to know the
/// implementations of. <c>BankTransferProvider</c> is the first; <c>PayPalProvider</c> is W4-01 and is a
/// new class plus settings rows, with <b>no change to checkout</b>.
/// <para>
/// <b>What is on this interface is exactly what checkout asks a provider and nothing else</b>, because
/// every extra member is a thing the next gateway has to answer and a thing checkout could come to depend
/// on. Checkout asks two questions:
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="InitiateAsync"/> — "what does the payment row for this order look like, and when is it
/// due?" Doc 08 §4.1's placement side effects differ per provider and only here: a bank transfer opens
/// <see cref="PaymentStatus.Pending"/> for 48 h, PayPal opens <see cref="PaymentStatus.Initiated"/> for
/// <c>PayPalExpiryMinutes</c> with a USD <c>ProviderAmount</c> at a frozen rate. Everything else
/// placement writes — the order, the lines, the reservation, the history row — is identical.
/// </description></item>
/// <item><description>
/// <see cref="DescribeAsync"/> — "what do I tell the customer to do next?" Bank transfer answers with
/// account details and the reference to quote; PayPal would answer with an approval URL. Both are
/// <see cref="PaymentInstructions"/>, which is why that record has a <c>RedirectUrl</c> a bank transfer
/// never sets and <c>Fields</c> PayPal never would.
/// </description></item>
/// </list>
/// <para>
/// <b>What is deliberately <i>not</i> here.</b> Approve and reject are not provider operations: no
/// gateway has them, they are a staff decision about a bank statement, and putting them on this interface
/// would force <c>PayPalProvider</c> to implement two methods that can only throw (the Liskov point doc 03
/// makes about this port). They live in their own use cases, and the part they share with a PayPal capture
/// — order <c>PendingPayment → Paid</c>, stock reserved → sold, a gapless invoice number — is
/// <see cref="IOrderSettlementStore"/>, which is a transaction and not a provider.
/// </para>
/// <para>
/// <b>What a provider that is confirmed by the provider itself builds on</b> (PayPal, W4-01; a second
/// gateway, W4-01b) — all of it ports, none of it checkout's:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="IOrderSettlementStore"/> — "this payment is complete": pay the order, or put the money on
/// record as a payment exception when the order can no longer take it.
/// </description></item>
/// <item><description>
/// <see cref="IPaymentEventStore"/> — the provider event log: verify the signature, record the event once
/// per idempotency key, apply it, say what became of it.
/// </description></item>
/// <item><description>
/// <see cref="IPaymentAttemptReader"/> and the <see cref="PaymentInstructionsRequest"/> it feeds — which
/// payment the order page is describing, so <see cref="DescribeAsync"/> can find the provider's own page
/// for an attempt that exists.
/// </description></item>
/// </list>
/// <para>
/// <b>A provider the customer pays on its own page answers <see cref="DescribeAsync"/> in two stages.</b>
/// At placement the payment row does not exist (checkout asks before the transaction that inserts it), so
/// <see cref="PaymentInstructionsRequest.PaymentId"/> is null and the provider answers
/// <see langword="null"/> — which also means no bank-transfer instructions email is queued. On the order
/// page the attempt exists and is named, and the provider answers with
/// <see cref="PaymentInstructions.RedirectForm"/> (a form the browser must POST) or
/// <see cref="PaymentInstructions.RedirectUrl"/> (a link), with <see cref="PaymentInstructions.AcceptsProof"/>
/// false. The order page renders either without knowing which provider it is.
/// </para>
/// <para>
/// <b>Liskov, concretely (doc 03 §3c).</b> Every implementation must satisfy
/// <c>PaymentProviderContractTests</c>: <see cref="Code"/> is a seeded <c>sales.PaymentProviders.Code</c>,
/// <see cref="InitiateAsync"/> returns a status that <c>CK_Payments_IsOrderSettlement</c> allows on a new
/// row and a due time strictly after the placement time, and <see cref="DescribeAsync"/> never throws for
/// an order of its own provider. The tests are written against the interface and run over every
/// registration, so a second provider is covered the day it is added.
/// </para>
/// </summary>
public interface IPaymentProvider
{
    /// <summary>
    /// The <c>sales.PaymentProviders.Code</c> this implementation serves — <c>BankTransfer</c>,
    /// <c>PayPal</c>. The row is the on/off switch and the checkout copy; this is the code that matches it.
    /// </summary>
    string Code { get; }

    /// <summary>
    /// Whether this provider can take an order <b>right now</b>, on top of the shop having switched its
    /// <c>sales.PaymentProviders</c> row on. Checkout lists a provider only when both are true, and refuses
    /// an order placed with one that answers <see langword="false"/> — so a gateway whose merchant account
    /// is not filled in yet, or whose public address is not configured, is hidden from shoppers instead of
    /// taking orders nobody can pay for.
    /// <para>
    /// <b>True by default</b> (a default interface member), so a provider with nothing to configure — the
    /// bank transfer, which has its own "details not set" state on the order page — is unchanged. A provider
    /// that needs settings overrides it. It must be cheap and must not throw: it is asked on every view of
    /// the checkout page.
    /// </para>
    /// </summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    /// <summary>
    /// The payment row a newly placed order starts with, and the deadline to put on the order.
    /// <para>
    /// Asynchronous because a provider may have to read its own settings — PayPal needs an exchange rate
    /// and an FX buffer out of <c>ops.Settings</c>, and a synchronous signature would make checkout block
    /// or make PayPal's arrival a change to this interface.
    /// </para>
    /// </summary>
    ValueTask<PaymentInitiation> InitiateAsync(
        PaymentInitiationRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// What the customer is told to do next, or <see langword="null"/> when this provider cannot say right
    /// now — for bank transfer, when the client has not filled their bank details in yet.
    /// <para>
    /// <b>Null is a real answer and not an error.</b> Doc 11 P7's preconditions make "bank details set by
    /// a Super Admin" a precondition of the process, and the shop can be deployed before the client sends
    /// them. The order page then says the details are being confirmed and gives the shopper the order link
    /// to come back to, rather than printing an empty account number somebody might transfer money to.
    /// </para>
    /// <para>
    /// <b>It is also the right answer at placement for a provider that needs the payment row</b> — see the
    /// type remarks and <see cref="PaymentInstructionsRequest.PaymentId"/>.
    /// </para>
    /// </summary>
    Task<PaymentInstructions?> DescribeAsync(
        PaymentInstructionsRequest request,
        CancellationToken cancellationToken);
}
