using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments.PayPal;

namespace SpencerDenim.IntegrationTests.Sales.PayPal;

/// <summary>
/// A PayPal that lives in memory: the <see cref="IPayPalGateway"/> every PayPal test runs against, because
/// nothing in this repository may touch the network and there are no sandbox credentials to test with.
/// <para>
/// <b>It models just enough of PayPal to be wrong in the ways PayPal can be wrong.</b> Orders are created
/// idempotently by request id, are approved by the test (as a buyer would), and can be captured once;
/// a repeated capture under the <i>same</i> request id returns the first answer, and one under a different
/// id is <c>ORDER_ALREADY_CAPTURED</c>. The test scripts the awkward outcomes - a decline, a PENDING
/// capture, a timeout that nevertheless captured, PayPal being down - and can run code in the middle of a
/// capture call, which is how "the webhook arrives before the API response finishes" and "the order expires
/// while the capture is in flight" are produced without sleeping.
/// </para>
/// <para>
/// Thread-safe, because the claim-race tests drive it from several requests at once, and it counts every
/// call so a test can say "exactly one capture reached PayPal".
/// </para>
/// </summary>
internal sealed class FakePayPalGateway : IPayPalGateway
{
    /// <summary>The transmission signature the fake treats as genuine.</summary>
    public const string ValidSignature = "valid-signature";

    private readonly object _gate = new();
    private readonly Dictionary<string, FakeOrder> _orders = new(StringComparer.Ordinal);
    private readonly Queue<CaptureScript> _script = new();
    private int _sequence;

    /// <summary>
    /// Makes every id this fake hands out unique across tests. The assembly shares one database and
    /// <c>UX_Payments_Provider_Reference</c> / <c>_Txn</c> are unique per provider, so a fake that numbered its
    /// orders from 1 each time would collide with the previous test's rows - as the real PayPal's ids never do.
    /// </summary>
    private readonly string _run = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    /// <summary>What a scripted capture does.</summary>
    internal enum CaptureScript
    {
        /// <summary>Capture it, COMPLETED, for the order's amount.</summary>
        Complete,

        /// <summary>Capture it, PENDING (an eCheck, a review).</summary>
        Pending,

        /// <summary>Refuse it: INSTRUMENT_DECLINED. Nothing is captured.</summary>
        Declined,

        /// <summary>The capture goes through at PayPal, but the answer is lost (a timeout).</summary>
        AnswerLost,

        /// <summary>PayPal is down: nothing happens and the caller is told it could not be reached.</summary>
        Unreachable,

        /// <summary>A 4xx the shop has no specific answer for. Nothing is captured.</summary>
        Rejected,
    }

    /// <summary>Every call fails as though PayPal were down.</summary>
    public bool Down { get; set; }

    /// <summary>Verification cannot be done (a 5xx or a timeout from PayPal).</summary>
    public bool VerificationFails { get; set; }

    /// <summary>The amount a captured order is captured for, instead of the amount it was created for.</summary>
    public decimal? CaptureAmountOverride { get; set; }

    /// <summary>The currency a captured order is captured in.</summary>
    public string CaptureCurrency { get; set; } = "USD";

    /// <summary>
    /// Runs in the middle of a capture call - after the caller has claimed and before PayPal answers. The
    /// arguments are the call's number (from 1), the PayPal order id and the request id.
    /// </summary>
    public Func<int, string, string, Task>? DuringCapture { get; set; }

    /// <summary>PayPal orders for which reading the order throws, as a defect in the shop's own code would.</summary>
    public HashSet<string> GetThrowsFor { get; } = new(StringComparer.Ordinal);

    public List<PayPalCreateOrderRequest> Creates { get; } = [];

    /// <summary>
    /// When set and it answers an issue code for a create request, that request is refused with it, the way PayPal
    /// refuses an address it will not accept (owner decision 2026-10-21's fallback).
    /// </summary>
    public Func<PayPalCreateOrderRequest, string?>? RejectCreate { get; set; }

    public List<(string OrderId, string RequestId)> Captures { get; } = [];

    public List<string> Gets { get; } = [];

    public List<(PayPalWebhookHeaders Headers, string Body)> Verifications { get; } = [];

    public int CaptureCalls
    {
        get
        {
            lock (_gate)
            {
                return Captures.Count;
            }
        }
    }

    /// <summary>Queues what the next capture call does. Unscripted calls <see cref="CaptureScript.Complete"/>.</summary>
    public void Script(params CaptureScript[] outcomes)
    {
        lock (_gate)
        {
            foreach (var outcome in outcomes)
            {
                _script.Enqueue(outcome);
            }
        }
    }

    /// <summary>The buyer approves the order at PayPal.</summary>
    public void Approve(string orderId)
    {
        lock (_gate)
        {
            Find(orderId).Status = PayPalOrderStatus.Approved;
            Find(orderId).PayerId = "PAYER-" + orderId;
        }
    }

    /// <summary>PayPal voids the order (it lapsed, or the merchant cancelled it).</summary>
    public void Void(string orderId)
    {
        lock (_gate)
        {
            Find(orderId).Status = PayPalOrderStatus.Voided;
        }
    }

    /// <summary>Forgets the order, as PayPal does for one it never had.</summary>
    public void Forget(string orderId)
    {
        lock (_gate)
        {
            _orders.Remove(orderId);
        }
    }

    /// <summary>The PayPal order created for this request id, or null.</summary>
    public string? OrderIdFor(string createRequestId)
    {
        lock (_gate)
        {
            return _orders.Values.FirstOrDefault(order => order.CreateRequestId == createRequestId)?.Id;
        }
    }

    /// <summary>The id of the latest capture on the order, or null.</summary>
    public string? CaptureIdOf(string orderId)
    {
        lock (_gate)
        {
            return _orders.TryGetValue(orderId, out var order) ? order.Captures.LastOrDefault()?.CaptureId : null;
        }
    }

    /// <summary>The amount the order was created for.</summary>
    public decimal AmountOf(string orderId)
    {
        lock (_gate)
        {
            return Find(orderId).Amount;
        }
    }

    public int CapturesAtPayPal(string orderId)
    {
        lock (_gate)
        {
            return _orders.TryGetValue(orderId, out var order) ? order.Captures.Count : 0;
        }
    }

    public Task<PayPalCreateOrderResult> CreateOrderAsync(
        PayPalCreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Creates.Add(request);

            if (Down)
            {
                return Task.FromResult<PayPalCreateOrderResult>(new PayPalCreateOrderResult.Unavailable("down"));
            }

            if (RejectCreate?.Invoke(request) is { } issue)
            {
                return Task.FromResult<PayPalCreateOrderResult>(new PayPalCreateOrderResult.Rejected(issue));
            }

            // Idempotent on the request id, as PayPal is: the second call gets the first call's order.
            var existing = _orders.Values.FirstOrDefault(order => order.CreateRequestId == request.RequestId);

            if (existing is null)
            {
                existing = new FakeOrder
                {
                    Id = $"FAKE-{_run}-ORDER-{++_sequence}",
                    CreateRequestId = request.RequestId,
                    Amount = request.Amount,
                    CustomId = request.CustomId,
                    Status = PayPalOrderStatus.Created,
                };

                _orders[existing.Id] = existing;
            }

            return Task.FromResult<PayPalCreateOrderResult>(new PayPalCreateOrderResult.Created(
                existing.Id,
                $"https://www.sandbox.paypal.com/checkoutnow?token={existing.Id}"));
        }
    }

    public Task<PayPalGetOrderResult> GetOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Gets.Add(orderId);

            if (GetThrowsFor.Contains(orderId))
            {
                throw new InvalidOperationException("The test made reading this order fail.");
            }

            if (Down)
            {
                return Task.FromResult<PayPalGetOrderResult>(new PayPalGetOrderResult.Unavailable("down"));
            }

            return Task.FromResult<PayPalGetOrderResult>(
                _orders.TryGetValue(orderId, out var order)
                    ? new PayPalGetOrderResult.Found(order.Snapshot())
                    : new PayPalGetOrderResult.NotFound());
        }
    }

    public async Task<PayPalCaptureCallResult> CaptureOrderAsync(
        string orderId,
        string requestId,
        CancellationToken cancellationToken)
    {
        int call;
        CaptureScript outcome;

        lock (_gate)
        {
            Captures.Add((orderId, requestId));
            call = Captures.Count;
            outcome = _script.Count > 0 ? _script.Dequeue() : CaptureScript.Complete;
        }

        if (DuringCapture is { } during)
        {
            await during(call, orderId, requestId);
        }

        lock (_gate)
        {
            if (Down || outcome == CaptureScript.Unreachable)
            {
                return new PayPalCaptureCallResult.Unavailable("down");
            }

            if (!_orders.TryGetValue(orderId, out var order))
            {
                return new PayPalCaptureCallResult.OrderNotFound();
            }

            // A repeat under the same request id is answered with the first answer; PayPal remembers it.
            if (order.CapturedByRequestId == requestId)
            {
                return new PayPalCaptureCallResult.Captured(order.Snapshot());
            }

            if (order.Status == PayPalOrderStatus.Completed)
            {
                return new PayPalCaptureCallResult.AlreadyCaptured();
            }

            if (order.Status != PayPalOrderStatus.Approved)
            {
                return order.Status == PayPalOrderStatus.Voided
                    ? new PayPalCaptureCallResult.Rejected("ORDER_VOIDED")
                    : new PayPalCaptureCallResult.NotApproved();
            }

            switch (outcome)
            {
                case CaptureScript.Declined:
                    return new PayPalCaptureCallResult.Declined("INSTRUMENT_DECLINED");

                case CaptureScript.Rejected:
                    return new PayPalCaptureCallResult.Rejected("SOMETHING_ELSE");
            }

            var pending = outcome == CaptureScript.Pending;

            order.Captures.Add(new PayPalCaptureSnapshot(
                $"FAKE-{_run}-CAPTURE-{++_sequence}",
                pending ? PayPalCaptureStatus.Pending : PayPalCaptureStatus.Completed,
                CaptureAmountOverride ?? order.Amount,
                CaptureCurrency,
                pending ? "ECHECK" : null));

            order.Status = PayPalOrderStatus.Completed;
            order.CapturedByRequestId = requestId;

            return outcome == CaptureScript.AnswerLost
                ? new PayPalCaptureCallResult.Unavailable("the answer never arrived")
                : new PayPalCaptureCallResult.Captured(order.Snapshot());
        }
    }

    public Task<PayPalWebhookVerification> VerifyWebhookAsync(
        PayPalWebhookHeaders headers,
        string rawBody,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Verifications.Add((headers, rawBody));

            if (VerificationFails)
            {
                return Task.FromResult(PayPalWebhookVerification.Error);
            }

            return Task.FromResult(
                headers.TransmissionSig == ValidSignature
                    ? PayPalWebhookVerification.Verified
                    : PayPalWebhookVerification.Invalid);
        }
    }

    private FakeOrder Find(string orderId) =>
        _orders.TryGetValue(orderId, out var order)
            ? order
            : throw new InvalidOperationException($"The fake PayPal has no order {orderId}.");

    private sealed class FakeOrder
    {
        public required string Id { get; init; }

        public required string CreateRequestId { get; init; }

        public required decimal Amount { get; init; }

        public required string CustomId { get; init; }

        public PayPalOrderStatus Status { get; set; }

        public string? PayerId { get; set; }

        public string? CapturedByRequestId { get; set; }

        public List<PayPalCaptureSnapshot> Captures { get; } = [];

        public PayPalOrderSnapshot Snapshot() => new(Id, Status, PayerId, [.. Captures]);
    }
}

/// <summary>A clock the test moves by hand, so a 24-hour recovery window takes no time to cross.</summary>
internal sealed class TestClock(DateTime start) : IClock
{
    private long _ticks = start.Ticks;

    public DateTime Now
    {
        get => new(Interlocked.Read(ref _ticks), DateTimeKind.Utc);
        set => Interlocked.Exchange(ref _ticks, value.Ticks);
    }

    public DateTimeOffset UtcNow => new(Now, TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
