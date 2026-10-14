namespace SpencerDenim.Application.Sales.Payments.PayPal;

/// <summary>
/// The PayPal REST API as the shop uses it (PAY-02): create an order, read it, capture it, and ask PayPal
/// whether a webhook is genuine. <b>The one port every test fakes</b>, because nothing in this repository may
/// touch the network and there are no sandbox credentials to test against.
/// <para>
/// <b>Answers, not exceptions, for everything PayPal can say.</b> A capture that PayPal declines, an order it
/// has never heard of and a payer who never approved are ordinary outcomes the flow branches on, and
/// "PayPal could not be reached" is a different thing again — it is the one case where the caller must
/// <i>not</i> conclude anything about the money. So each result has a case for what PayPal said and one,
/// <c>Unavailable</c>, for "we do not know". A caller that treated every failure alike would either capture
/// twice or give up on a payment that went through.
/// </para>
/// <para>
/// <b>The implementation reads the shop's settings on every call</b> (<see cref="IPayPalSettingsReader"/>),
/// so switching between sandbox and live, or correcting a credential, needs no restart. If PayPal is not
/// configured every call answers <c>Unavailable</c> — never a half-formed request to PayPal. It never logs a
/// secret, an access token or a whole payload.
/// </para>
/// </summary>
public interface IPayPalGateway
{
    /// <summary>
    /// <c>POST /v2/checkout/orders</c>, intent CAPTURE, one purchase unit in USD. Idempotent on
    /// <see cref="PayPalCreateOrderRequest.RequestId"/>.
    /// </summary>
    Task<PayPalCreateOrderResult> CreateOrderAsync(
        PayPalCreateOrderRequest request,
        CancellationToken cancellationToken);

    /// <summary><c>GET /v2/checkout/orders/{id}</c>: what PayPal says the order is, with its captures.</summary>
    Task<PayPalGetOrderResult> GetOrderAsync(string orderId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>POST /v2/checkout/orders/{id}/capture</c> with <c>PayPal-Request-Id: <paramref name="requestId"/></c>.
    /// <b>Never retried by the implementation</b> beyond what that header makes safe: the claim and the
    /// recovery job decide whether to ask again.
    /// </summary>
    Task<PayPalCaptureCallResult> CaptureOrderAsync(
        string orderId,
        string requestId,
        CancellationToken cancellationToken);

    /// <summary>
    /// <c>POST /v1/notifications/verify-webhook-signature</c> over the five transmission headers, the
    /// configured webhook id and the <b>raw</b> body.
    /// </summary>
    Task<PayPalWebhookVerification> VerifyWebhookAsync(
        PayPalWebhookHeaders headers,
        string rawBody,
        CancellationToken cancellationToken);
}

/// <summary>What the shop asks PayPal to create.</summary>
/// <param name="RequestId">The idempotency key — <see cref="PayPalRequestIds.Create"/>.</param>
/// <param name="Amount">The dollar amount, as <see cref="PayPalUsdAmount.Compute"/> worked it out and the payment recorded it.</param>
/// <param name="Currency">Always <see cref="PayPalUsdAmount.Currency"/>.</param>
/// <param name="CustomId">Comes back in webhooks: <see cref="PayPalRequestIds.CustomId"/>.</param>
/// <param name="Description">What the buyer sees beside the amount: the order number.</param>
/// <param name="ReturnUrl">Where PayPal sends the buyer after approval — the shop's own return endpoint.</param>
/// <param name="CancelUrl">Where PayPal sends a buyer who backs out.</param>
public sealed record PayPalCreateOrderRequest(
    string RequestId,
    decimal Amount,
    string Currency,
    string CustomId,
    string Description,
    string ReturnUrl,
    string CancelUrl);

/// <summary>The answer to <see cref="IPayPalGateway.CreateOrderAsync"/>.</summary>
public abstract record PayPalCreateOrderResult
{
    private PayPalCreateOrderResult()
    {
    }

    /// <summary>PayPal made (or, for a repeated request id, returned) the order.</summary>
    /// <param name="OrderId">PayPal's order id — stored as <c>Payments.ProviderReference</c>.</param>
    /// <param name="ApprovalUrl">Where the buyer approves it.</param>
    public sealed record Created(string OrderId, string ApprovalUrl) : PayPalCreateOrderResult;

    /// <summary>PayPal refused the request (a 4xx): retrying the same request will not help.</summary>
    /// <param name="Reason">A short reason for the log. No payload.</param>
    public sealed record Rejected(string Reason) : PayPalCreateOrderResult;

    /// <summary>PayPal could not be reached, did not answer in time, or the shop is not configured.</summary>
    /// <param name="Reason">A short reason for the log.</param>
    public sealed record Unavailable(string Reason) : PayPalCreateOrderResult;
}

/// <summary>The answer to <see cref="IPayPalGateway.GetOrderAsync"/>.</summary>
public abstract record PayPalGetOrderResult
{
    private PayPalGetOrderResult()
    {
    }

    /// <summary>PayPal knows the order.</summary>
    public sealed record Found(PayPalOrderSnapshot Order) : PayPalGetOrderResult;

    /// <summary>PayPal has no such order (404).</summary>
    public sealed record NotFound : PayPalGetOrderResult;

    /// <summary>PayPal could not be reached, did not answer in time, or the shop is not configured.</summary>
    /// <param name="Reason">A short reason for the log.</param>
    public sealed record Unavailable(string Reason) : PayPalGetOrderResult;
}

/// <summary>The answer to <see cref="IPayPalGateway.CaptureOrderAsync"/>.</summary>
public abstract record PayPalCaptureCallResult
{
    private PayPalCaptureCallResult()
    {
    }

    /// <summary>
    /// PayPal accepted the capture (2xx). <b>The order may be COMPLETED and the capture inside it still
    /// PENDING</b> (an eCheck, a review) or DECLINED: the decision is on the capture's status, never the
    /// order's.
    /// </summary>
    public sealed record Captured(PayPalOrderSnapshot Order) : PayPalCaptureCallResult;

    /// <summary><c>ORDER_ALREADY_CAPTURED</c>: a repeat of a capture that went through. Read the order for the capture.</summary>
    public sealed record AlreadyCaptured : PayPalCaptureCallResult;

    /// <summary><c>INSTRUMENT_DECLINED</c> and its kin: the buyer's funding source was refused. Nothing was captured.</summary>
    /// <param name="Reason">PayPal's issue code, for the log and <c>FailureMessage</c>.</param>
    public sealed record Declined(string Reason) : PayPalCaptureCallResult;

    /// <summary>
    /// <c>ORDER_NOT_APPROVED</c> / <c>PAYER_ACTION_REQUIRED</c>: the buyer came back without approving.
    /// Nothing was captured, and approving later is still possible.
    /// </summary>
    public sealed record NotApproved : PayPalCaptureCallResult;

    /// <summary>PayPal has no such order (404).</summary>
    public sealed record OrderNotFound : PayPalCaptureCallResult;

    /// <summary>
    /// A 4xx the flow has no specific answer for. <b>Not proof that nothing was captured</b> — the caller
    /// leaves the payment Capturing so recovery asks PayPal what the order is.
    /// </summary>
    /// <param name="Reason">A short reason for the log.</param>
    public sealed record Rejected(string Reason) : PayPalCaptureCallResult;

    /// <summary>
    /// A timeout, a connection failure, a 5xx, a refused token — or the shop is not configured. <b>We do not
    /// know</b> whether the money moved.
    /// </summary>
    /// <param name="Reason">A short reason for the log.</param>
    public sealed record Unavailable(string Reason) : PayPalCaptureCallResult;
}

/// <summary>PayPal's order status (<c>status</c> on an order).</summary>
public enum PayPalOrderStatus
{
    /// <summary>A status this build does not know. Treated as "not approved".</summary>
    Unknown = 0,

    /// <summary><c>CREATED</c>: made, not yet approved.</summary>
    Created = 1,

    /// <summary><c>SAVED</c>.</summary>
    Saved = 2,

    /// <summary><c>APPROVED</c>: the buyer approved; nothing captured yet.</summary>
    Approved = 3,

    /// <summary><c>VOIDED</c>: lapsed or cancelled on PayPal's side.</summary>
    Voided = 4,

    /// <summary><c>COMPLETED</c>: a capture exists (see its own status).</summary>
    Completed = 5,

    /// <summary><c>PAYER_ACTION_REQUIRED</c>: waiting for the buyer.</summary>
    PayerActionRequired = 6,
}

/// <summary>
/// PayPal's capture status. The webhook event types map onto these as well (<c>PAYMENT.CAPTURE.DENIED</c> is
/// <see cref="Declined"/>), so one decision table (<see cref="PayPalApplyDecision"/>) serves the capture
/// response, the webhook and the recovery job.
/// </summary>
public enum PayPalCaptureStatus
{
    /// <summary>A status this build does not know. Never acted on.</summary>
    Unknown = 0,

    /// <summary>The money is ours.</summary>
    Completed = 1,

    /// <summary>On hold with PayPal (an eCheck, a review): not yet ours, not yet refused.</summary>
    Pending = 2,

    /// <summary>DECLINED or DENIED: refused.</summary>
    Declined = 3,

    /// <summary>FAILED.</summary>
    Failed = 4,

    /// <summary>Refunded in full — recorded, not applied (doc 08 §4.3).</summary>
    Refunded = 5,

    /// <summary>Refunded in part — recorded, not applied.</summary>
    PartiallyRefunded = 6,

    /// <summary>Taken back by the buyer's bank (a chargeback) — recorded, not applied.</summary>
    Reversed = 7,
}

/// <summary>One capture inside a PayPal order.</summary>
/// <param name="CaptureId">PayPal's capture id — stored as <c>Payments.ProviderTransactionId</c>.</param>
/// <param name="Status">The capture's status.</param>
/// <param name="Amount">The amount captured, or null when PayPal's value could not be read.</param>
/// <param name="Currency">Its currency, or null when it could not be read.</param>
/// <param name="StatusReason">PayPal's <c>status_details.reason</c> for a pending or declined capture.</param>
public sealed record PayPalCaptureSnapshot(
    string CaptureId,
    PayPalCaptureStatus Status,
    decimal? Amount,
    string? Currency,
    string? StatusReason);

/// <summary>An order as PayPal reports it. Holds no personal data: not the payer's name, email or address.</summary>
/// <param name="OrderId">PayPal's order id.</param>
/// <param name="Status">The order's status.</param>
/// <param name="PayerId">PayPal's id for the payer, if it has one yet.</param>
/// <param name="Captures">Every capture on the order, oldest first. Empty before one is made.</param>
public sealed record PayPalOrderSnapshot(
    string OrderId,
    PayPalOrderStatus Status,
    string? PayerId,
    IReadOnlyList<PayPalCaptureSnapshot> Captures);

/// <summary>
/// The five headers PayPal signs a webhook with (<c>PAYPAL-AUTH-ALGO</c>, <c>PAYPAL-CERT-URL</c>,
/// <c>PAYPAL-TRANSMISSION-ID</c>, <c>PAYPAL-TRANSMISSION-SIG</c>, <c>PAYPAL-TRANSMISSION-TIME</c>).
/// </summary>
public sealed record PayPalWebhookHeaders(
    string AuthAlgo,
    string CertUrl,
    string TransmissionId,
    string TransmissionSig,
    string TransmissionTime);

/// <summary>What <see cref="IPayPalGateway.VerifyWebhookAsync"/> found.</summary>
public enum PayPalWebhookVerification
{
    /// <summary>PayPal says <c>SUCCESS</c>: the notification is genuine.</summary>
    Verified = 0,

    /// <summary>PayPal says <c>FAILURE</c>, or the headers are not those of a PayPal notification: forged or corrupt.</summary>
    Invalid = 1,

    /// <summary>
    /// We could not find out: PayPal timed out or errored, or the shop is not configured to verify. <b>Not
    /// the same as invalid</b> — the webhook endpoint answers 503 so PayPal tries again, and stores nothing.
    /// </summary>
    Error = 2,
}
