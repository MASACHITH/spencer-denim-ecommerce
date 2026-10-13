namespace SpencerDenim.Application.Sales.Payments.Contracts;

/// <summary>
/// What a provider is asked in order to describe the next step to a customer (PAY-03).
/// </summary>
/// <param name="OrderNumber">
/// The order number. Doc 11 P7 step 1 makes it <b>the reference the customer must quote</b>, which is the
/// only thing tying an anonymous bank credit to an order, so it is an input to the instructions rather
/// than something the page is left to work out.
/// </param>
/// <param name="AmountLkr">The exact amount to transfer (<c>Orders.GrandTotal</c>).</param>
/// <param name="PaymentDueAtUtc">When the hold lapses (placed + 48 h for a bank transfer).</param>
/// <param name="OrderUrl">
/// The absolute order link, built from the order's <c>PublicId</c>. P7 step 1 lists it beside the bank
/// details because it is a guest's only route back to the order, and the shop sends no email yet (W3-06).
/// </param>
/// <param name="PaymentId">
/// <c>sales.Payments.Id</c> of the order's payment attempt that is still in flight (Initiated, Pending or
/// Capturing), or <see langword="null"/> when there is none.
/// <para>
/// <b>Null at placement, always, and that is not a shortcoming but an ordering fact.</b> Checkout asks
/// <c>DescribeAsync</c> <i>before</i> the placement transaction (doc 08 §5.4 keeps every read out of the
/// transaction that holds the stock locks), so the payment row does not exist yet and has no id. The order
/// page asks later, after the row exists, and fills this in. A provider that has to name the exact attempt
/// to the customer — a hosted checkout whose reference carries the payment id, so that the notification
/// that comes back can be tied to this attempt and not merely to this order — therefore answers
/// <see langword="null"/> (nothing to say yet) when this is null, and describes itself on the order page.
/// A bank transfer ignores it.
/// </para>
/// </param>
/// <param name="ProviderReference">
/// The provider's own identifier for that attempt (<c>sales.Payments.ProviderReference</c> — the PayPal
/// order id, for instance), or <see langword="null"/> when the provider has not created one yet or has no
/// such thing. Together with <paramref name="PaymentId"/> it is what lets <c>DescribeAsync</c> find the
/// provider's own approval page for an <b>existing</b> payment instead of creating a second one on every
/// page view.
/// </param>
public sealed record PaymentInstructionsRequest(
    string OrderNumber,
    decimal AmountLkr,
    DateTime PaymentDueAtUtc,
    string? OrderUrl,
    int? PaymentId = null,
    string? ProviderReference = null);

/// <summary>
/// The customer-facing "what to do next" for one payment provider, in a shape both v1 providers fit
/// (PAY-03, doc 11 P7 step 1).
/// </summary>
/// <param name="ProviderCode">Which provider produced this, so a page can key its copy off it.</param>
/// <param name="Reference">
/// The reference the customer must quote. The order number for a bank transfer; null for a provider that
/// identifies the payment itself.
/// </param>
/// <param name="AmountLkr">The exact amount, repeated here so the block is self-contained.</param>
/// <param name="DueAtUtc">The deadline, likewise.</param>
/// <param name="Fields">
/// Label-and-value pairs to print, in order — bank name, branch, account name, account number. A list
/// rather than four properties because the next provider's details are not these four, and a page that
/// renders a list needs no change when they differ.
/// </param>
/// <param name="Notes">Free text under the fields (<c>Bank.Instructions</c>), plain text, never markup.</param>
/// <param name="RedirectUrl">
/// Where to send the customer to pay, for a provider whose hosted page is reached by a plain link (a GET
/// redirect). Always null for a bank transfer. An absolute <c>https</c> URL; the order page refuses to
/// render anything else as a link.
/// </param>
/// <param name="AcceptsProof">
/// Whether this provider takes a customer-uploaded reference and payment slip (doc 11 P7 step 3). True for
/// bank transfer, false for a gateway that confirms itself.
/// </param>
/// <param name="RedirectForm">
/// For a provider whose hosted page can only be reached by <b>posting</b> a form to it — the browser has to
/// submit fields the server computed and signed, so they cannot be a link. Null for a bank transfer and for
/// a GET redirect. If both this and <paramref name="RedirectUrl"/> are set, the form wins.
/// </param>
public sealed record PaymentInstructions(
    string ProviderCode,
    string? Reference,
    decimal AmountLkr,
    DateTime DueAtUtc,
    IReadOnlyList<PaymentInstructionField> Fields,
    string? Notes,
    string? RedirectUrl,
    bool AcceptsProof,
    PaymentRedirectForm? RedirectForm = null);

/// <summary>One labelled detail of <see cref="PaymentInstructions"/>, e.g. "Account number" / "1234567890".</summary>
/// <param name="Label">What to print beside the value. Set by the provider, never by a caller.</param>
/// <param name="Value">The value. Non-empty: a provider omits a field it has no value for.</param>
/// <param name="Copyable">
/// Whether the page should offer a copy button. True for the account number and the reference — the two
/// values a shopper retypes into online banking and the two where a typo costs them money.
/// </param>
public sealed record PaymentInstructionField(string Label, string Value, bool Copyable = false);
