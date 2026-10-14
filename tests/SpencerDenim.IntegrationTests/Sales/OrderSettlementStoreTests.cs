using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// <c>IOrderSettlementStore</c> against a real SQL Server: a provider's completed payment pays an order with
/// the same effects as a staff approval, or — when the order cannot take it — is kept as a payment exception
/// with an alert (doc 08 §4.2 step 3 and 3.4, doc 11 P8/P10).
/// <para>
/// <b>Every assertion is on the rows</b>, as in <see cref="BankTransferReviewTests"/>, whose arrangement
/// this borrows: an order is placed through the real checkout and then <b>re-labelled</b> as a payment
/// provider's — nothing in this build can place one, because the second gateway is seeded disabled and has
/// no code. That is exactly what <c>BankTransferReviewTests.A_PayPal_order_cannot_be_approved_through_the_bank_flow</c>
/// does in the other direction, and for the same reason: the guard has to hold for a row that exists
/// however it got there.
/// </para>
/// </summary>
public sealed class OrderSettlementStoreTests(SqlServerDatabaseFixture sqlServer) : ProviderOrderTestBase(sqlServer)
{
    private static readonly Regex InvoiceNumberShape = new(@"^INV-\d{4}-\d{6,}$", RegexOptions.None);

    [Fact]
    public async Task Settling_a_completed_payment_pays_the_order_with_every_effect_of_an_approval()
    {
        var arranged = await PlaceProviderOrderAsync(quantity: 2);
        var before = await StockAsync(arranged.VariantId);
        Assert.Equal(2, before.Reserved);

        var now = DateTime.UtcNow;
        var result = await SettleAsync(arranged, now: now, transactionId: "CAP-1001", payer: "PAYER-77");

        var settled = Assert.IsType<PaymentSettlementResult.Settled>(result);
        Assert.Equal(arranged.OrderNumber, settled.OrderNumber);
        Assert.Matches(InvoiceNumberShape, settled.InvoiceNumber);

        var row = await ReadOrderAsync(arranged.PublicId);

        // Doc 08 §4.1's whole Paid-effects list, read back — the same list BankTransferReviewTests asserts
        // for an approval, because it is the same code.
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Equal(OrderStockState.Committed, row.StockState);
        Assert.Equal(now, row.PaidAtUtc!.Value, TimeSpan.FromMilliseconds(5));
        Assert.Null(row.CaptureLockedUntilUtc);
        Assert.Null(row.ClosedAtUtc);
        Assert.Equal(settled.InvoiceNumber, row.InvoiceNumber);
        Assert.Equal(row.PaidAtUtc, row.InvoiceIssuedAtUtc);

        var payment = Assert.Single(row.Payments);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);
        Assert.Equal("CAP-1001", payment.ProviderTransactionId);
        Assert.Equal("PAYER-77", payment.PayerReference);
        Assert.Equal(row.PaidAtUtc, payment.CompletedAtUtc);

        // Nobody approved it: the columns that record a staff member stay empty.
        Assert.Null(payment.ApprovedByUserId);
        Assert.Null(payment.ApprovedAtUtc);

        // Doc 08 §5.5: reserved becomes sold.
        var after = await StockAsync(arranged.VariantId);
        Assert.Equal(before.OnHand - 2, after.OnHand);
        Assert.Equal(before.Reserved - 2, after.Reserved);
        Assert.Equal(before.Available, after.Available);

        // Doc 08 §4: PaymentProvider is the source "reserved for a signature-verified webhook", and a
        // System payment has nobody to name.
        var history = Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
        Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);
        Assert.Equal(OrderStatusChangeSource.PaymentProvider, history.Source);
        Assert.Null(history.ChangedByUserId);
        Assert.Equal($"Payment confirmed by {Provider}", history.Note);

        // Doc 11 P8 step 6: "Payment confirmed email", queued in the same transaction.
        var email = Assert.Single(await OutboxAsync(arranged.PublicId, EmailTemplateCodes.PaymentConfirmed));
        Assert.Contains(settled.InvoiceNumber, email.BodyText!, StringComparison.Ordinal);

        // A System payment is not a person's decision: no audit row, no alert.
        Assert.Equal(0, await AuditRowCountAsync(arranged.PublicId));
        Assert.Empty(await AlertsAsync(arranged.PublicId));
    }

    [Fact]
    public async Task A_second_delivery_of_the_same_payment_is_a_duplicate_and_changes_nothing()
    {
        var arranged = await PlaceProviderOrderAsync();

        var first = await SettleAsync(arranged);
        Assert.IsType<PaymentSettlementResult.Settled>(first);

        var stockAfterFirst = await StockAsync(arranged.VariantId);
        var seriesAfterFirst = await InvoiceSeriesNextValueAsync();
        var orderAfterFirst = await ReadOrderAsync(arranged.PublicId);

        var second = await SettleAsync(arranged, now: DateTime.UtcNow.AddMinutes(5));

        var duplicate = Assert.IsType<PaymentSettlementResult.AlreadyHandled>(second);
        Assert.Equal(PaymentStatus.Succeeded, duplicate.PaymentStatus);

        // Nothing moved: not the stock, not the invoice series, not the order's timestamps, not the history.
        Assert.Equal(stockAfterFirst, await StockAsync(arranged.VariantId));
        Assert.Equal(seriesAfterFirst, await InvoiceSeriesNextValueAsync());

        var orderAfterSecond = await ReadOrderAsync(arranged.PublicId);
        Assert.Equal(orderAfterFirst.PaidAtUtc, orderAfterSecond.PaidAtUtc);
        Assert.Equal(orderAfterFirst.InvoiceNumber, orderAfterSecond.InvoiceNumber);
        Assert.Equal(orderAfterFirst.StatusHistory.Count, orderAfterSecond.StatusHistory.Count);

        Assert.Single(await OutboxAsync(arranged.PublicId, EmailTemplateCodes.PaymentConfirmed));
        Assert.Empty(await AlertsAsync(arranged.PublicId));
    }

    /// <summary>
    /// A webhook and a return page, or two redeliveries, arriving together: the claim on the order row is the
    /// lock, so exactly one of them pays the order and the rest are told it was already done.
    /// </summary>
    [Fact]
    public async Task Parallel_deliveries_of_one_payment_settle_it_exactly_once()
    {
        var arranged = await PlaceProviderOrderAsync(quantity: 2);
        var before = await StockAsync(arranged.VariantId);
        var seriesBefore = await InvoiceSeriesNextValueAsync();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => Task.Run(() => SettleAsync(arranged), Token)));

        Assert.Equal(1, results.Count(result => result is PaymentSettlementResult.Settled));

        // The losers are duplicates — not exceptions, because the payment is the settling one by the time
        // they look — and not errors.
        Assert.Equal(5, results.Count(result =>
            result is PaymentSettlementResult.AlreadyHandled { PaymentStatus: PaymentStatus.Succeeded }));

        var row = await ReadOrderAsync(arranged.PublicId);
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Equal(PaymentStatus.Succeeded, Assert.Single(row.Payments).Status);

        // Stock deducted once, one invoice number taken, one history row, one email, no alert.
        var after = await StockAsync(arranged.VariantId);
        Assert.Equal(before.OnHand - 2, after.OnHand);
        Assert.Equal(before.Reserved - 2, after.Reserved);
        Assert.Equal((seriesBefore ?? 1) + 1, await InvoiceSeriesNextValueAsync());
        Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);
        Assert.Single(await OutboxAsync(arranged.PublicId, EmailTemplateCodes.PaymentConfirmed));
        Assert.Empty(await AlertsAsync(arranged.PublicId));
    }

    [Fact]
    public async Task A_payment_that_arrives_after_the_order_expired_is_kept_as_an_exception_and_alerts_staff()
    {
        var arranged = await PlaceProviderOrderAsync();
        Assert.True(await ExpireAsync(arranged.OrderId));

        // Arranged state, asserted: the open payment was expired with the order, which is the row a late
        // success arrives against (R1: "6 Expired" is a status a COMPLETED payment is accepted from).
        var expired = await ReadOrderAsync(arranged.PublicId);
        Assert.Equal(OrderStatus.Expired, expired.Status);
        Assert.Equal(PaymentStatus.Expired, Assert.Single(expired.Payments).Status);

        var stockBefore = await StockAsync(arranged.VariantId);
        var seriesBefore = await InvoiceSeriesNextValueAsync();

        try
        {
            var result = await SettleAsync(arranged, transactionId: "CAP-LATE");

            var routed = Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(result);
            Assert.Equal(PaymentExceptionReasons.OrderExpired, routed.Reason);
            Assert.Equal(arranged.OrderNumber, routed.OrderNumber);

            var row = await ReadOrderAsync(arranged.PublicId);

            // The order is exactly as the expiry left it. The money is on record; a person decides.
            Assert.Equal(OrderStatus.Expired, row.Status);
            Assert.Equal(OrderStockState.Released, row.StockState);
            Assert.Null(row.PaidAtUtc);
            Assert.Null(row.InvoiceNumber);
            Assert.DoesNotContain(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);

            // Doc 08 §4.2 step 3.4: status 7, the settlement flag clear, the transaction id kept so the
            // refund can name it.
            var payment = Assert.Single(row.Payments);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.False(payment.IsOrderSettlement);
            Assert.Equal("CAP-LATE", payment.ProviderTransactionId);
            Assert.NotNull(payment.CompletedAtUtc);

            // R9's code is for a mismatch only; accepting this payment is allowed, so it must not carry it.
            Assert.Null(payment.FailureCode);
            Assert.Contains("expired", payment.FailureMessage!, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(stockBefore, await StockAsync(arranged.VariantId));
            Assert.Equal(seriesBefore, await InvoiceSeriesNextValueAsync());
            Assert.Empty(await OutboxAsync(arranged.PublicId, EmailTemplateCodes.PaymentConfirmed));

            // The alert: PaymentException, a Warning, about this order, naming the payment and the reason.
            var alert = Assert.Single(await AlertsAsync(arranged.PublicId));
            Assert.Equal(AdminAlertTypes.PaymentException, alert.AlertType);
            Assert.Equal(AdminAlertSeverity.Warning, alert.Severity);
            Assert.Equal(AdminAlert.OrderEntityType, alert.RelatedEntityType);
            Assert.Contains(arranged.OrderNumber, alert.Title, StringComparison.Ordinal);
            Assert.Contains($"\"paymentId\":{arranged.PaymentId}", alert.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("\"reason\":\"order_expired\"", alert.DetailsJson, StringComparison.Ordinal);
            Assert.Null(alert.AcknowledgedAtUtc);

            // And delivering it again raises no second alert: the row is already status 7.
            var again = await SettleAsync(arranged, transactionId: "CAP-LATE");
            var duplicate = Assert.IsType<PaymentSettlementResult.AlreadyHandled>(again);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, duplicate.PaymentStatus);
            Assert.Single(await AlertsAsync(arranged.PublicId));
        }
        finally
        {
            await DeleteAlertsAsync(arranged.PublicId);
        }
    }

    [Fact]
    public async Task A_payment_that_arrives_after_the_order_was_cancelled_is_kept_as_an_exception()
    {
        var arranged = await PlaceProviderOrderAsync(
            beforeRelabel: async (publicId, _) =>
            {
                var cancelled = await InScopeAsync(services => services
                    .GetRequiredService<IOrderCancellationStore>()
                    .CancelAsync(
                        new OrderCancellation(publicId, OrderCancelledBy.Customer, null, "Changed my mind", DateTime.UtcNow),
                        Token));

                Assert.IsType<CancelOrderOutcome.Applied>(cancelled);
            });

        try
        {
            var result = await SettleAsync(arranged);

            var routed = Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(result);
            Assert.Equal(PaymentExceptionReasons.OrderCancelled, routed.Reason);

            var row = await ReadOrderAsync(arranged.PublicId);
            Assert.Equal(OrderStatus.Cancelled, row.Status);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, Assert.Single(row.Payments).Status);
            Assert.Single(await AlertsAsync(arranged.PublicId));
        }
        finally
        {
            await DeleteAlertsAsync(arranged.PublicId);
        }
    }

    /// <summary>
    /// Doc 08 §4.1: "an order that is already Paid and receives a duplicate capture keeps its status. Only
    /// the extra payment is refunded." A <i>second payment</i>, not a redelivery of the first.
    /// </summary>
    [Fact]
    public async Task A_second_payment_on_an_order_that_is_already_paid_is_an_exception_and_the_order_is_untouched()
    {
        var arranged = await PlaceProviderOrderAsync(quantity: 2);
        Assert.IsType<PaymentSettlementResult.Settled>(await SettleAsync(arranged));

        var duplicatePaymentId = await InsertPaymentAsync(arranged.OrderId, PaymentStatus.Initiated);

        var stockBefore = await StockAsync(arranged.VariantId);
        var seriesBefore = await InvoiceSeriesNextValueAsync();
        var orderBefore = await ReadOrderAsync(arranged.PublicId);

        try
        {
            var result = await SettleAsync(arranged, paymentId: duplicatePaymentId, transactionId: "CAP-DUP");

            var routed = Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(result);
            Assert.Equal(PaymentExceptionReasons.OrderAlreadyPaid, routed.Reason);

            var row = await ReadOrderAsync(arranged.PublicId);

            // The order keeps its status, its invoice and its stock; the first payment is still the one
            // that settles it.
            Assert.Equal(OrderStatus.Paid, row.Status);
            Assert.Equal(orderBefore.InvoiceNumber, row.InvoiceNumber);
            Assert.Equal(orderBefore.PaidAtUtc, row.PaidAtUtc);
            Assert.Equal(stockBefore, await StockAsync(arranged.VariantId));
            Assert.Equal(seriesBefore, await InvoiceSeriesNextValueAsync());

            var first = row.Payments.Single(candidate => candidate.Id == arranged.PaymentId);
            Assert.Equal(PaymentStatus.Succeeded, first.Status);
            Assert.True(first.IsOrderSettlement);

            // The extra one is money held for refund, outside UX_Payments_OneSettlement.
            var extra = row.Payments.Single(candidate => candidate.Id == duplicatePaymentId);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, extra.Status);
            Assert.False(extra.IsOrderSettlement);
            Assert.Equal("CAP-DUP", extra.ProviderTransactionId);

            Assert.Single(await AlertsAsync(arranged.PublicId));
        }
        finally
        {
            await DeleteAlertsAsync(arranged.PublicId);
        }
    }

    /// <summary>
    /// Doc 08 §4.2 R9: a payment whose amount or currency is not what was requested is kept as an
    /// exception that can <b>only be refunded</b>; the order stays payable and nothing is deducted.
    /// </summary>
    [Theory]
    [InlineData("31.24", "USD")]
    [InlineData("31.26", "USD")]
    [InlineData("31.25", "LKR")]
    [InlineData("9375.00", "LKR")]
    public async Task A_payment_for_the_wrong_amount_or_currency_is_an_exception_that_can_only_be_refunded(
        string amount,
        string currency)
    {
        var arranged = await PlaceProviderOrderAsync(quantity: 2);

        // A capture lock, as a capture claim would have set (doc 08 §4.2 step 1).
        await SetCaptureLockAsync(arranged.OrderId, DateTime.UtcNow.AddHours(3));

        var stockBefore = await StockAsync(arranged.VariantId);
        var seriesBefore = await InvoiceSeriesNextValueAsync();

        // A transaction id of its own per case: (ProviderCode, ProviderTransactionId) is unique, so the
        // four rows of this theory cannot share one — which is the index doing its job, and the first
        // version of this test found it by sharing "CAP-WRONG".
        var transactionId = SalesRowWriter.Unique("CAP-WRONG-");

        try
        {
            var result = await SettleAsync(
                arranged,
                amount: decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
                currency: currency,
                transactionId: transactionId);

            var routed = Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(result);
            Assert.Equal(PaymentExceptionReasons.AmountMismatch, routed.Reason);

            var row = await ReadOrderAsync(arranged.PublicId);

            // "A still-payable order stays PendingPayment, and no stock is deducted" — and the lock is
            // cleared so the customer is not held out of paying again for three hours.
            Assert.Equal(OrderStatus.PendingPayment, row.Status);
            Assert.Equal(OrderStockState.Reserved, row.StockState);
            Assert.Null(row.CaptureLockedUntilUtc);
            Assert.Null(row.InvoiceNumber);
            Assert.Equal(stockBefore, await StockAsync(arranged.VariantId));
            Assert.Equal(seriesBefore, await InvoiceSeriesNextValueAsync());

            var payment = Assert.Single(row.Payments);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.False(payment.IsOrderSettlement);
            Assert.Equal(PaymentFailureCodes.AmountMismatch, payment.FailureCode);
            Assert.Equal(transactionId, payment.ProviderTransactionId);

            var alert = Assert.Single(await AlertsAsync(arranged.PublicId));
            Assert.Contains("\"reason\":\"amount_mismatch\"", alert.DetailsJson, StringComparison.Ordinal);

            // Both the amount that was asked for and the one that came are in the alert.
            Assert.Contains("\"expectedAmount\":31.25", alert.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("\"expectedCurrency\":\"USD\"", alert.DetailsJson, StringComparison.Ordinal);
            Assert.Contains($"\"confirmedCurrency\":\"{currency}\"", alert.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAlertsAsync(arranged.PublicId);
        }
    }

    /// <summary>
    /// A payment that was never converted (the gateway charges in LKR) is held to the order's amount in
    /// LKR: the same check, with the conversion columns empty.
    /// </summary>
    [Fact]
    public async Task A_payment_that_was_never_converted_is_checked_against_the_order_amount_in_LKR()
    {
        var arranged = await PlaceProviderOrderAsync(converted: false);

        // The right amount, in the wrong currency: 31.25 USD is not Rs. 31.25 — the amount alone matching
        // is not enough.
        var wrong = await SettleAsync(arranged, amount: arranged.GrandTotal, currency: Usd);
        Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(wrong);
        await DeleteAlertsAsync(arranged.PublicId);

        var second = await PlaceProviderOrderAsync(converted: false);

        var right = await SettleAsync(second, amount: second.GrandTotal, currency: "LKR");

        Assert.IsType<PaymentSettlementResult.Settled>(right);
        Assert.Equal(OrderStatus.Paid, (await ReadOrderAsync(second.PublicId)).Status);
    }

    /// <summary>
    /// Doc 08 §4.2 R1 and Appendix B1: a late COMPLETED on a Failed payment is <b>not</b> a duplicate. It pays
    /// the order, and the newer attempt the customer opened meanwhile is closed in the same transaction.
    /// </summary>
    [Fact]
    public async Task A_late_success_on_a_failed_payment_pays_the_order_and_closes_the_newer_open_attempt()
    {
        var arranged = await PlaceProviderOrderAsync(status: PaymentStatus.Failed, failureCode: "INSTRUMENT_DECLINED");
        var retryId = await InsertPaymentAsync(arranged.OrderId, PaymentStatus.Initiated);

        var result = await SettleAsync(arranged, transactionId: "CAP-LATE-OK");

        Assert.IsType<PaymentSettlementResult.Settled>(result);

        var row = await ReadOrderAsync(arranged.PublicId);
        Assert.Equal(OrderStatus.Paid, row.Status);

        var paid = row.Payments.Single(candidate => candidate.Id == arranged.PaymentId);
        Assert.Equal(PaymentStatus.Succeeded, paid.Status);
        Assert.True(paid.IsOrderSettlement);

        // The failure it carried is cleared: a settling payment must not read as "declined".
        Assert.Null(paid.FailureCode);
        Assert.Null(paid.FailureMessage);

        var closed = row.Payments.Single(candidate => candidate.Id == retryId);
        Assert.Equal(PaymentStatus.Cancelled, closed.Status);
        Assert.False(closed.IsOrderSettlement);
        Assert.NotNull(closed.CompletedAtUtc);

        Assert.Empty(await AlertsAsync(arranged.PublicId));
    }

    /// <summary>
    /// Doc 08 §4.2 step 3.3 and Appendix B4, made to happen rather than waited for: the order claim has
    /// succeeded, and then **the payment is taken by another path before the settlement's own update reaches
    /// it**. The savepoint is rolled back, the exception path finds the payment already routed, and the
    /// answer is "already handled" — with the order exactly as it was. Nothing is retried forever, and
    /// nothing is left half done.
    /// </summary>
    /// <remarks>
    /// <b>The interleaving is forced with a lock, not a sleep.</b> A second connection takes an <i>update</i>
    /// lock on the payment row, which an ordinary read is compatible with — so the store gets past its
    /// reads and its order claim, and blocks on the payment <c>UPDATE</c>, exactly where the race lives.
    /// The test waits until SQL Server says a request is blocked by that connection, then changes the
    /// payment from there and commits.
    /// </remarks>
    [Fact]
    public async Task A_payment_taken_by_another_path_after_the_order_claim_rolls_back_to_the_savepoint_and_changes_nothing()
    {
        var arranged = await PlaceProviderOrderAsync(quantity: 2);
        var stockBefore = await StockAsync(arranged.VariantId);
        var seriesBefore = await InvoiceSeriesNextValueAsync();

        await using var holder = new Microsoft.Data.SqlClient.SqlConnection(SqlServer.ConnectionString);
        await holder.OpenAsync(Token);

        await using var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await holder.BeginTransactionAsync(Token);

        int holderSpid;

        await using (var take = holder.CreateCommand())
        {
            take.Transaction = transaction;
            take.CommandText =
                "SELECT Id FROM sales.Payments WITH (UPDLOCK, ROWLOCK) WHERE Id = @id; ";
            take.Parameters.AddWithValue("@id", arranged.PaymentId);
            Assert.NotNull(await take.ExecuteScalarAsync(Token));
        }

        await using (var spid = holder.CreateCommand())
        {
            spid.Transaction = transaction;
            spid.CommandText = "SELECT @@SPID;";
            holderSpid = Convert.ToInt32(await spid.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
        }

        var settling = Task.Run(() => SettleAsync(arranged, transactionId: "CAP-RACE"), Token);

        try
        {
            await WaitUntilBlockedByAsync(holderSpid);

            // The other path wins: it routes the payment to the exception state and commits.
            await using (var take = holder.CreateCommand())
            {
                take.Transaction = transaction;
                take.CommandText =
                    "UPDATE sales.Payments SET Status = @status, ProviderTransactionId = 'CAP-OTHER' WHERE Id = @id;";
                take.Parameters.AddWithValue("@status", (byte)PaymentStatus.CapturedRequiresRefund);
                take.Parameters.AddWithValue("@id", arranged.PaymentId);
                Assert.Equal(1, await take.ExecuteNonQueryAsync(Token));
            }

            await transaction.CommitAsync(Token);

            var result = await settling.WaitAsync(TimeSpan.FromSeconds(60), Token);

            var handled = Assert.IsType<PaymentSettlementResult.AlreadyHandled>(result);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, handled.PaymentStatus);
        }
        finally
        {
            // A transaction left open would hold the row for the rest of the run.
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }

        // The order claim was undone: the order is payable exactly as before, nothing was deducted, no
        // invoice number was taken, and the other path's writes are the only ones on the payment.
        var row = await ReadOrderAsync(arranged.PublicId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(OrderStockState.Reserved, row.StockState);
        Assert.Null(row.PaidAtUtc);
        Assert.Null(row.InvoiceNumber);
        Assert.Equal(stockBefore, await StockAsync(arranged.VariantId));
        Assert.Equal(seriesBefore, await InvoiceSeriesNextValueAsync());

        var payment = Assert.Single(row.Payments);
        Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
        Assert.Equal("CAP-OTHER", payment.ProviderTransactionId);
        Assert.False(payment.IsOrderSettlement);

        Assert.Empty(await OutboxAsync(arranged.PublicId, EmailTemplateCodes.PaymentConfirmed));

        // This store routed nothing, so it raised no alert: whoever routed the payment owns that.
        Assert.Empty(await AlertsAsync(arranged.PublicId));
    }

    [Fact]
    public async Task A_payment_of_another_provider_or_one_that_does_not_exist_is_not_found_and_nothing_changes()
    {
        var arranged = await PlaceProviderOrderAsync();
        var seriesBefore = await InvoiceSeriesNextValueAsync();

        Assert.IsType<PaymentSettlementResult.NotFound>(await SettleAsync(arranged, paymentId: 2_000_000_000));

        // The payment exists but is PayPal's: another provider's notification cannot settle it.
        var wrongProvider = await InScopeAsync(services => services
            .GetRequiredService<IOrderSettlementStore>()
            .SettleAsync(
                new PaymentSettlement(arranged.PaymentId, "Koko", UsdAmount, Usd, DateTime.UtcNow),
                Token));
        Assert.IsType<PaymentSettlementResult.NotFound>(wrongProvider);

        var row = await ReadOrderAsync(arranged.PublicId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(PaymentStatus.Initiated, Assert.Single(row.Payments).Status);
        Assert.Equal(seriesBefore, await InvoiceSeriesNextValueAsync());
        Assert.Empty(await AlertsAsync(arranged.PublicId));
    }

    /// <summary>
    /// The order's own provider is pinned in the claim, as the bank flow's is: a payment of one provider
    /// does not pay an order that belongs to another. The money arrived, so it is kept — as an exception.
    /// </summary>
    [Fact]
    public async Task A_payment_whose_order_belongs_to_another_provider_is_kept_as_an_exception_not_applied()
    {
        var arranged = await PlaceProviderOrderAsync();

        // The order says bank transfer; the payment says the gateway.
        using (var scope = CreateScope())
        {
            var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
                .Where(order => order.Id == arranged.OrderId)
                .ExecuteUpdateAsync(
                    order => order.SetProperty(row => row.PaymentProviderCode, PaymentProviderCodes.BankTransfer),
                    Token);

            Assert.Equal(1, moved);
        }

        try
        {
            var result = await SettleAsync(arranged);

            var routed = Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(result);
            Assert.Equal(PaymentExceptionReasons.OrderNotPayable, routed.Reason);

            var row = await ReadOrderAsync(arranged.PublicId);
            Assert.Equal(OrderStatus.PendingPayment, row.Status);
            Assert.Equal(OrderStockState.Reserved, row.StockState);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, Assert.Single(row.Payments).Status);
        }
        finally
        {
            await DeleteAlertsAsync(arranged.PublicId);
        }
    }

    [Fact]
    public async Task A_bank_transfer_is_settled_by_staff_and_never_by_a_providers_notification()
    {
        var (order, _) = await PlaceOrderAsync();
        var paymentId = (await ReadOrderAsync(order.PublicId)).Payments.Single().Id;

        // Refused loudly rather than answered: this is a programming error, and the audit trail, the
        // approver column and the staff permission all belong to the other path.
        await Assert.ThrowsAsync<ArgumentException>(() => InScopeAsync(services => services
            .GetRequiredService<IOrderSettlementStore>()
            .SettleAsync(
                new PaymentSettlement(paymentId, PaymentProviderCodes.BankTransfer, order.GrandTotal, "LKR", DateTime.UtcNow),
                Token)));

        Assert.Equal(OrderStatus.PendingPayment, (await ReadOrderAsync(order.PublicId)).Status);
    }

    [Fact]
    public async Task Routing_a_payment_to_the_exception_path_directly_raises_one_alert_however_often_it_is_asked()
    {
        var arranged = await PlaceProviderOrderAsync();

        try
        {
            var routing = new PaymentExceptionRouting(
                arranged.PaymentId,
                Provider,
                PaymentExceptionReasons.AmountMismatch,
                DateTime.UtcNow,
                ConfirmedAmount: 12.00m,
                ConfirmedCurrency: Usd,
                ProviderTransactionId: "CAP-ROUTED");

            var first = await InScopeAsync(services => services
                .GetRequiredService<IOrderSettlementStore>()
                .RouteToExceptionAsync(routing, Token));

            var routed = Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(first);
            Assert.Equal(PaymentExceptionReasons.AmountMismatch, routed.Reason);

            var row = await ReadOrderAsync(arranged.PublicId);
            var payment = Assert.Single(row.Payments);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.Equal(PaymentFailureCodes.AmountMismatch, payment.FailureCode);
            Assert.Equal("CAP-ROUTED", payment.ProviderTransactionId);
            Assert.Equal(OrderStatus.PendingPayment, row.Status);

            var second = await InScopeAsync(services => services
                .GetRequiredService<IOrderSettlementStore>()
                .RouteToExceptionAsync(routing, Token));

            Assert.IsType<PaymentSettlementResult.AlreadyHandled>(second);
            Assert.Single(await AlertsAsync(arranged.PublicId));
        }
        finally
        {
            await DeleteAlertsAsync(arranged.PublicId);
        }
    }

    [Fact]
    public async Task Routing_with_a_reason_the_system_does_not_know_is_refused_before_anything_is_written()
    {
        var arranged = await PlaceProviderOrderAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => InScopeAsync(services => services
            .GetRequiredService<IOrderSettlementStore>()
            .RouteToExceptionAsync(
                new PaymentExceptionRouting(arranged.PaymentId, Provider, "because", DateTime.UtcNow),
                Token)));

        Assert.Equal(PaymentStatus.Initiated, Assert.Single((await ReadOrderAsync(arranged.PublicId)).Payments).Status);
    }
}
