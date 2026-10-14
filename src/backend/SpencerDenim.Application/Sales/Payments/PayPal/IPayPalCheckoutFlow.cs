namespace SpencerDenim.Application.Sales.Payments.PayPal;

/// <summary>
/// What the customer-facing PayPal endpoints do (doc 11 P8 steps 1 to 4 and 9): send a buyer to PayPal,
/// receive them back, and note that they backed out. The endpoints are three lines each; the rules are here.
/// <para>
/// <b>Every method takes the order link's token and nothing the browser controls.</b> The PayPal order that
/// is captured is the one stored on the payment, not the <c>token</c> PayPal appends to the return address;
/// the amount is the one frozen on the payment, not anything in the request. The return is never proof of
/// payment (doc 08 §3.5, EventSource 3) — only PayPal's capture response or a verified webhook is.
/// </para>
/// </summary>
public interface IPayPalCheckoutFlow
{
    /// <summary>
    /// Finds or makes the order's open PayPal payment, creates the PayPal order for it if there is none yet
    /// (idempotently), and answers where to send the buyer — or why not.
    /// </summary>
    Task<PayPalStartResult> StartAsync(Guid orderPublicId, CancellationToken cancellationToken);

    /// <summary>
    /// The buyer is back from PayPal. Claims the capture, captures and applies the result (doc 08 §4.2), and
    /// answers which sentence the order page should show.
    /// </summary>
    /// <param name="orderPublicId">The order link's token.</param>
    /// <param name="paypalOrderToken">
    /// The <c>token</c> query value PayPal appended, <b>for the log only</b>. It is browser-controlled and is
    /// never used to decide which order is captured.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PayPalReturnResult> ReturnAsync(
        Guid orderPublicId,
        string? paypalOrderToken,
        CancellationToken cancellationToken);

    /// <summary>The buyer backed out at PayPal: the abandoned attempt is cancelled so the next one starts fresh.</summary>
    Task<PayPalReturnResult> CancelAsync(Guid orderPublicId, CancellationToken cancellationToken);
}

/// <summary>Where <see cref="IPayPalCheckoutFlow.StartAsync"/> sends the buyer.</summary>
public abstract record PayPalStartResult
{
    private PayPalStartResult()
    {
    }

    /// <summary>To PayPal, to approve the payment.</summary>
    public sealed record Redirect(string ApprovalUrl) : PayPalStartResult;

    /// <summary>Back to the order page, with the sentence that says why (<see cref="PayPalPageHints"/>).</summary>
    public sealed record Back(string Hint) : PayPalStartResult;

    /// <summary>There is no such order.</summary>
    public sealed record NotFound : PayPalStartResult;
}

/// <summary>Where the buyer goes after returning from, or leaving, PayPal.</summary>
/// <param name="Hint">The sentence the order page shows (<see cref="PayPalPageHints"/>), or null for none.</param>
/// <param name="Found">False when there is no such order.</param>
public sealed record PayPalReturnResult(string? Hint, bool Found = true)
{
    /// <summary>There is no such order.</summary>
    public static PayPalReturnResult NotFound { get; } = new(null, false);
}

/// <summary>
/// What a verified PayPal webhook delivery is answered with (doc 08 §3.5 DB13, §4.2).
/// </summary>
public enum PayPalWebhookResult
{
    /// <summary>Verified, stored and applied (or recorded as not needing to be): HTTP 200.</summary>
    Accepted = 0,

    /// <summary>Verified, and already stored: HTTP 200. Nothing is done a second time.</summary>
    Duplicate = 1,

    /// <summary>The signature did not verify, or the headers are not those of a PayPal notification: HTTP 400, nothing stored.</summary>
    InvalidSignature = 2,

    /// <summary>The body cannot be an event at all: HTTP 400, nothing stored, PayPal not asked.</summary>
    Malformed = 3,

    /// <summary>
    /// The signature could not be checked — PayPal timed out or errored, or the shop is not configured to
    /// verify: HTTP 503, nothing stored, so PayPal sends it again.
    /// </summary>
    VerificationUnavailable = 4,
}

/// <summary>
/// The webhook endpoint's rules, in the order doc 08 §3.5 DB13 fixes: <b>verify the signature first</b> and
/// store nothing before it passes (an unverified request must never consume an idempotency key); an error
/// while verifying is a 503 and stores nothing; an invalid signature is a 400 and stores nothing; a verified
/// event is stored once, a duplicate answers 200, and a new one is applied.
/// </summary>
public interface IPayPalWebhookHandler
{
    /// <summary>Handles one delivery.</summary>
    /// <param name="headers">The five transmission headers.</param>
    /// <param name="rawBody">The body exactly as received: the signature is over these bytes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PayPalWebhookResult> HandleAsync(
        PayPalWebhookHeaders headers,
        string rawBody,
        CancellationToken cancellationToken);
}
