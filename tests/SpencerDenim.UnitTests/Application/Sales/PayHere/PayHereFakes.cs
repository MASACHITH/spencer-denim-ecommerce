using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Application.Sales.PayHere;

/// <summary>
/// The values the PayHere unit tests share, so that a known-answer test and the handler test above it are
/// about the same notification and cannot drift apart.
/// </summary>
internal static class PayHereSample
{
    public const string MerchantId = "1211149";
    public const string Secret = "TestSecret";
    public const string OrderNumber = "SD-2026-000123";
    public const int PaymentId = 4567;
    public const string OrderId = "SD-2026-000123-4567";
    public const string PayHerePaymentId = "320032123456";

    public static readonly DateTime Now = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

    public static readonly Guid OrderPublicId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    public static PayHereSettings Settings(
        string? mode = "sandbox",
        string? merchantId = MerchantId,
        string? secret = Secret,
        int pendingMaxDays = 5,
        int expiryMinutes = 60) =>
        new(mode, merchantId, secret, pendingMaxDays, expiryMinutes);

    /// <summary>The posted form of a genuine notification, signed with <see cref="Secret"/>.</summary>
    public static Dictionary<string, string> Form(
        string status = "2",
        string amount = "1500.50",
        string currency = "LKR",
        string orderId = OrderId,
        string merchantId = MerchantId,
        string paymentId = PayHerePaymentId,
        string secret = Secret,
        string? signature = null)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["merchant_id"] = merchantId,
            ["order_id"] = orderId,
            ["payment_id"] = paymentId,
            ["payhere_amount"] = amount,
            ["payhere_currency"] = currency,
            ["status_code"] = status,
            ["md5sig"] = signature
                ?? PayHereHash.NotificationSignature(merchantId, orderId, amount, currency, status, secret),
            ["status_message"] = "Successfully received the payment",
            ["method"] = "VISA",
        };

        return form;
    }

    public static PayHerePayment Payment(
        PaymentStatus status = PaymentStatus.Initiated,
        OrderStatus orderStatus = OrderStatus.PendingPayment,
        string? transactionId = null) =>
        new(
            PaymentId,
            77,
            OrderPublicId,
            OrderNumber,
            status,
            orderStatus,
            1_500.50m,
            ProviderReference: null,
            transactionId);

    public static PayHereNotification Notification(
        string status = "2",
        string amount = "1500.50",
        string currency = "LKR",
        string paymentId = PayHerePaymentId,
        string orderId = OrderId,
        string? message = "Successfully received the payment") =>
        new(MerchantId, orderId, paymentId, amount, currency, status, message, "VISA");
}

internal sealed class PayHereTestClock(DateTime nowUtc) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(nowUtc, TimeSpan.Zero);
}

internal sealed class FakePayHereSettings(PayHereSettings settings) : IPayHereSettingsReader
{
    public Task<PayHereSettings> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(settings);
}

/// <summary>An event store that remembers everything it was asked and behaves as the real one does about keys.</summary>
internal sealed class FakePayHereEvents : IPaymentEventStore
{
    private readonly Dictionary<string, long> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public List<PaymentEventRecord> Recorded { get; } = [];

    public List<(long EventId, string Outcome, string? Failure, DateTime? RetryAt)> Marks { get; } = [];

    public List<(long EventId, int? PaymentId, int? OrderId)> Links { get; } = [];

    public Task<PaymentEventReceipt> RecordAsync(PaymentEventRecord record, CancellationToken cancellationToken)
    {
        if (_byKey.TryGetValue(record.IdempotencyKey, out var existing))
        {
            return Task.FromResult(new PaymentEventReceipt(
                existing, IsNew: false, PaymentEventProcessingStatus.Received, false, false));
        }

        var id = _byKey.Count + 1;
        _byKey[record.IdempotencyKey] = id;
        Recorded.Add(record);

        return Task.FromResult(new PaymentEventReceipt(id, IsNew: true, PaymentEventProcessingStatus.Received, false, false));
    }

    public Task<bool> MarkProcessedAsync(long eventId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        Marks.Add((eventId, "Processed", null, null));
        return Task.FromResult(true);
    }

    public Task<bool> MarkIgnoredAsync(long eventId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        Marks.Add((eventId, "Ignored", null, null));
        return Task.FromResult(true);
    }

    public Task<bool> MarkFailedAsync(
        long eventId,
        string failure,
        DateTime? retryAtUtc,
        CancellationToken cancellationToken)
    {
        Marks.Add((eventId, "Failed", failure, retryAtUtc));
        return Task.FromResult(true);
    }

    public Task<bool> LinkAsync(long eventId, int? paymentId, int? orderId, CancellationToken cancellationToken)
    {
        Links.Add((eventId, paymentId, orderId));
        return Task.FromResult(true);
    }
}

internal sealed class FakePayHerePayments : IPayHerePaymentStore
{
    public PayHerePayment? Payment { get; set; } = PayHereSample.Payment();

    public PayHereTransition PendingResult { get; set; } = PayHereTransition.Applied;

    public PayHereFailureResult FailedResult { get; set; } =
        new(PayHereTransition.Applied, WasPending: false, ReplacementOpened: true);

    public Exception? Throw { get; set; }

    public List<string> Calls { get; } = [];

    public PayHereFailure? LastFailure { get; private set; }

    public int? LastPendingDays { get; private set; }

    public string? LastReference { get; private set; }

    public Task<PayHerePayment?> FindAsync(string orderNumber, int paymentId, CancellationToken cancellationToken)
    {
        Calls.Add("Find");
        return Task.FromResult(
            Payment is { } found && found.OrderNumber == orderNumber && found.PaymentId == paymentId ? found : null);
    }

    public PayHereCheckoutContext? Context { get; set; }

    public Task<PayHereCheckoutContext?> FindCheckoutContextAsync(int paymentId, CancellationToken cancellationToken)
    {
        Calls.Add("FindCheckoutContext");
        return Task.FromResult(Context);
    }

    public Task RecordReferenceAsync(
        int paymentId,
        int orderId,
        string payHerePaymentId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        Calls.Add("RecordReference");
        LastReference = payHerePaymentId;
        return Task.CompletedTask;
    }

    public Task<PayHereTransition> MarkPendingAsync(
        int paymentId,
        int orderId,
        string payHerePaymentId,
        int pendingMaxDays,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        Calls.Add("MarkPending");
        LastPendingDays = pendingMaxDays;
        return Throw is { } error ? Task.FromException<PayHereTransition>(error) : Task.FromResult(PendingResult);
    }

    public Task<PayHereFailureResult> MarkFailedAsync(PayHereFailure failure, CancellationToken cancellationToken)
    {
        Calls.Add("MarkFailed");
        LastFailure = failure;
        return Throw is { } error ? Task.FromException<PayHereFailureResult>(error) : Task.FromResult(FailedResult);
    }
}

internal sealed class FakePayHereSettlements : IOrderSettlementStore
{
    public PaymentSettlementResult Result { get; set; } = new PaymentSettlementResult.Settled("SD-2026-000123", "INV-2026-000001");

    public Exception? Throw { get; set; }

    public PaymentSettlement? Settled { get; private set; }

    public PaymentExceptionRouting? Routed { get; private set; }

    public Task<PaymentSettlementResult> SettleAsync(PaymentSettlement settlement, CancellationToken cancellationToken)
    {
        Settled = settlement;
        return Throw is { } error ? Task.FromException<PaymentSettlementResult>(error) : Task.FromResult(Result);
    }

    public Task<PaymentSettlementResult> RouteToExceptionAsync(
        PaymentExceptionRouting routing,
        CancellationToken cancellationToken)
    {
        Routed = routing;
        return Task.FromResult(Result);
    }
}

internal sealed class FakePayHereAlerts : IAdminAlertWriter
{
    public List<AdminAlert> Raised { get; } = [];

    public Task RaiseAsync(AdminAlert alert, CancellationToken cancellationToken)
    {
        Raised.Add(alert);
        return Task.CompletedTask;
    }

    public void Enlist(AdminAlert alert) => Raised.Add(alert);
}
