using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// What a hostile or broken caller, a loaded database and an odd provider can do to
/// <c>IOrderSettlementStore</c>, and what it must do back: never deadlock, never pay a bank order on a
/// spelling, never lose a payment to a long id.
/// <para>
/// These are the tester's findings on the first version, each written as the test that would have caught it.
/// </para>
/// </summary>
public sealed class OrderSettlementRobustnessTests(SqlServerDatabaseFixture sqlServer) : ProviderOrderTestBase(sqlServer)
{
    // ---------------------------------------------------------------------------------------------------
    // Deadlock: a settlement must not touch another order's payment rows
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Twelve settlements of twelve different orders in parallel: none is killed as a deadlock victim.</b>
    /// </summary>
    /// <remarks>
    /// The first version closed "other open attempts" with <c>UPDATE sales.Payments … WHERE OrderId = @o AND
    /// Id &lt;&gt; @p AND Status IN (0, 1)</c> on every settlement. On a small table the optimiser answers that
    /// with a range seek on the clustered key — in effect a scan — and every settlement already held an
    /// exclusive lock on the payment row it had just updated, so two settlements of <i>different</i> orders
    /// scanned into each other's rows: 16 to 31 deadlocks per run of twelve, and a call that ran out of its
    /// three retries. The execution strategy retries a victim silently, so the symptom was slowness and an
    /// occasional exception, never a wrong answer — which is why it needs a counter and not an assertion on
    /// the result alone.
    /// <para>
    /// <b>The count is this host's own</b> (<see cref="CommandCountingInterceptor.DeadlockCount"/>), not the
    /// server's <c>Number of Deadlocks/sec</c>: the test server is shared, and another run's contention would
    /// fail a test about this code. The held-lock test below is the deterministic version of the same claim.
    /// Statuses where the close statement runs (Capturing, Failed) <i>and</i> where it does not (Initiated)
    /// are both covered.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(PaymentStatus.Initiated)]
    [InlineData(PaymentStatus.Capturing)]
    [InlineData(PaymentStatus.Failed)]
    public async Task Settling_twelve_orders_in_parallel_never_deadlocks(PaymentStatus openingStatus)
    {
        const int orders = 12;

        var arranged = new List<ProviderOrder>();

        for (var index = 0; index < orders; index++)
        {
            arranged.Add(await PlaceProviderOrderAsync(
                status: openingStatus == PaymentStatus.Initiated ? null : openingStatus,
                failureCode: openingStatus == PaymentStatus.Failed ? "INSTRUMENT_DECLINED" : null));
        }

        CommandCounter.Reset();

        var results = await Task.WhenAll(arranged.Select(order => Task.Run(() => SettleAsync(order), Token)));

        Assert.All(results, result => Assert.IsType<PaymentSettlementResult.Settled>(result));

        // One invoice number each, all different: twelve payments, twelve orders, nothing shared.
        Assert.Equal(
            orders,
            results.Cast<PaymentSettlementResult.Settled>().Select(settled => settled.InvoiceNumber).Distinct().Count());

        Assert.Equal(
            0,
            CommandCounter.DeadlockCount);

        foreach (var order in arranged)
        {
            Assert.Equal(OrderStatus.Paid, (await ReadOrderAsync(order.PublicId)).Status);
        }
    }

    /// <summary>
    /// <b>The same claim, made deterministic:</b> while another order's payment row is locked by somebody
    /// else, a settlement completes. If any statement of the settlement reached outside its own order — a
    /// scan, a range seek on the clustered key — it would block on that row and this test would time out,
    /// whatever the timing of a parallel run.
    /// </summary>
    /// <remarks>
    /// An exclusive lock on order B's payment row is held in an open transaction on a second connection, then
    /// order A is settled. The close-other-attempts statement runs on every settlement, whatever status A's
    /// payment starts in; unhinted it blocks here, with <c>IX_Payments_OrderId</c> it never looks at B.
    /// </remarks>
    [Theory]
    [InlineData(PaymentStatus.Initiated)]
    [InlineData(PaymentStatus.Capturing)]
    [InlineData(PaymentStatus.Failed)]
    public async Task A_settlement_is_not_blocked_by_a_lock_on_another_orders_payment(PaymentStatus openingStatus)
    {
        var failed = openingStatus == PaymentStatus.Failed ? "INSTRUMENT_DECLINED" : null;

        var settling = await PlaceProviderOrderAsync(
            status: openingStatus == PaymentStatus.Initiated ? null : openingStatus,
            failureCode: failed);

        var bystander = await PlaceProviderOrderAsync();

        await using var holder = new SqlConnection(SqlServer.ConnectionString);
        await holder.OpenAsync(Token);

        await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync(Token);

        try
        {
            await using (var lockRow = holder.CreateCommand())
            {
                lockRow.Transaction = transaction;
                lockRow.CommandText = "UPDATE sales.Payments SET UpdatedAtUtc = SYSUTCDATETIME() WHERE Id = @id;";
                lockRow.Parameters.AddWithValue("@id", bystander.PaymentId);

                // The arrangement, asserted: the lock is held on exactly the row meant.
                Assert.Equal(1, await lockRow.ExecuteNonQueryAsync(Token));
            }

            var result = await SettleAsync(settling).WaitAsync(TimeSpan.FromSeconds(30), Token);

            Assert.IsType<PaymentSettlementResult.Settled>(result);
        }
        finally
        {
            // Released either way: an open transaction would hold the row for the rest of the run, and a
            // settlement blocked behind it (the failure this test exists to show) needs it to end.
            await transaction.RollbackAsync(CancellationToken.None);
        }

        // The bystander was not touched: still its own open attempt.
        Assert.Equal(
            PaymentStatus.Initiated,
            Assert.Single((await ReadOrderAsync(bystander.PublicId)).Payments).Status);
    }

    /// <summary>
    /// The statement that closes other attempts still does its job — on the order's own rows, found through
    /// the index — for each status where it runs.
    /// </summary>
    [Theory]
    [InlineData(PaymentStatus.Capturing)]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Cancelled)]
    [InlineData(PaymentStatus.Expired)]
    public async Task A_payment_outside_the_one_open_index_closes_the_other_open_attempt(PaymentStatus openingStatus)
    {
        var arranged = await PlaceProviderOrderAsync(status: openingStatus);
        var newer = await InsertPaymentAsync(arranged.OrderId, PaymentStatus.Initiated);

        Assert.IsType<PaymentSettlementResult.Settled>(await SettleAsync(arranged));

        var row = await ReadOrderAsync(arranged.PublicId);

        Assert.Equal(PaymentStatus.Succeeded, row.Payments.Single(candidate => candidate.Id == arranged.PaymentId).Status);
        Assert.Equal(PaymentStatus.Cancelled, row.Payments.Single(candidate => candidate.Id == newer).Status);
    }

    /// <summary>
    /// <b>The race the status gate lost, made to happen.</b> The settlement reads payment A as Initiated, then
    /// blocks on the order row, which a retry flow (it locks the order first) is holding. That flow cancels A,
    /// opens a newer attempt B and commits. The settlement then pays A — and B, opened after its first read,
    /// must be closed in the same Paid transaction (doc 08 Appendix B1), which is only so if the close
    /// statement does not trust what the settlement read before it had the lock.
    /// </summary>
    [Fact]
    public async Task An_attempt_opened_while_the_settlement_waited_for_the_order_is_closed_with_it()
    {
        var arranged = await PlaceProviderOrderAsync();

        await using var holder = new SqlConnection(SqlServer.ConnectionString);
        await holder.OpenAsync(Token);

        await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync(Token);

        int holderSpid;
        Task<PaymentSettlementResult> settling;

        try
        {
            // The retry flow's first move: lock the order row.
            await using (var lockOrder = holder.CreateCommand())
            {
                lockOrder.Transaction = transaction;
                lockOrder.CommandText = "UPDATE sales.Orders SET UpdatedAtUtc = SYSUTCDATETIME() WHERE Id = @id;";
                lockOrder.Parameters.AddWithValue("@id", arranged.OrderId);

                // The arrangement, asserted: the lock is held on exactly the row meant.
                Assert.Equal(1, await lockOrder.ExecuteNonQueryAsync(Token));
            }

            await using (var spid = holder.CreateCommand())
            {
                spid.Transaction = transaction;
                spid.CommandText = "SELECT @@SPID;";
                holderSpid = Convert.ToInt32(await spid.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);
            }

            settling = Task.Run(() => SettleAsync(arranged), Token);

            // It has read the payment (Initiated) and is now waiting on the order row.
            await WaitUntilBlockedByAsync(holderSpid);

            // The retry: cancel the abandoned attempt, open a newer one, commit.
            await using (var retry = holder.CreateCommand())
            {
                retry.Transaction = transaction;
                retry.CommandText =
                    """
                    UPDATE sales.Payments SET Status = @cancelled WHERE Id = @id;
                    INSERT INTO sales.Payments (OrderId, ProviderCode, Status, Amount, ProviderReference)
                         SELECT OrderId, ProviderCode, @initiated, Amount, @reference
                           FROM sales.Payments WHERE Id = @id;
                    """;
                retry.Parameters.AddWithValue("@cancelled", (byte)PaymentStatus.Cancelled);
                retry.Parameters.AddWithValue("@initiated", (byte)PaymentStatus.Initiated);
                retry.Parameters.AddWithValue("@id", arranged.PaymentId);
                retry.Parameters.AddWithValue("@reference", SalesRowWriter.Unique("PP-RETRY-"));

                await retry.ExecuteNonQueryAsync(Token);
            }

            await transaction.CommitAsync(Token);
        }
        finally
        {
            // A transaction left open would hold the order for the rest of the run.
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }

        var result = await settling.WaitAsync(TimeSpan.FromSeconds(60), Token);

        Assert.IsType<PaymentSettlementResult.Settled>(result);

        var row = await ReadOrderAsync(arranged.PublicId);
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.Equal(2, row.Payments.Count);

        // A, read as Initiated and cancelled behind its back, is the one that settled (R1: a late success on
        // a Cancelled payment is not a duplicate)...
        var paid = row.Payments.Single(candidate => candidate.Id == arranged.PaymentId);
        Assert.Equal(PaymentStatus.Succeeded, paid.Status);
        Assert.True(paid.IsOrderSettlement);

        // ...and B, which did not exist when it was read, is closed in the same transaction.
        var newer = row.Payments.Single(candidate => candidate.Id != arranged.PaymentId);
        Assert.Equal(PaymentStatus.Cancelled, newer.Status);
        Assert.False(newer.IsOrderSettlement);
    }

    // ---------------------------------------------------------------------------------------------------
    // The provider code is decided on the stored row, not on how the caller spelled it
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>A full-width spelling of the bank-transfer code paid a bank order with no approver.</b> The
    /// payment lookup compared the caller's text in SQL, under a case- and width-insensitive collation, so
    /// "ＢａｎｋＴｒａｎｓｆｅｒ" found the bank payment, while the guard compared the same text in C# and found
    /// it different.
    /// </summary>
    [Theory]
    [InlineData("ＢａｎｋＴｒａｎｓｆｅｒ")] // fullwidth BankTransfer
    [InlineData("BankTransfer")]
    [InlineData("banktransfer")]
    [InlineData("BANKTRANSFER")]
    [InlineData("  BankTransfer  ")]
    [InlineData("Bank­Transfer")] // soft hyphen
    [InlineData("Payments.BankTransfer")]
    public async Task A_bank_payment_is_never_settled_whatever_the_provider_code_is_spelled(string spelling)
    {
        // The premise, asserted: this is why the guard exists. The database folds a full-width code onto
        // the real one; if a different collation ever stopped doing so the guard would still hold, and this
        // line would say the test no longer shows the original defect.
        if (spelling.StartsWith('Ｂ'))
        {
            await using var probe = new SqlConnection(SqlServer.ConnectionString);
            await probe.OpenAsync(Token);
            await using var command = probe.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sales.PaymentProviders WHERE Code = @code;";
            command.Parameters.Add(new SqlParameter("@code", System.Data.SqlDbType.NVarChar, 50) { Value = spelling });

            Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture));
        }

        var (order, _) = await PlaceOrderAsync();
        var paymentId = (await ReadOrderAsync(order.PublicId)).Payments.Single().Id;
        var seriesBefore = await InvoiceSeriesNextValueAsync();

        // The amount is right, so only the provider code stands between this and a paid order.
        await AssertRefusedAsync(() => InScopeAsync(services => services
            .GetRequiredService<IOrderSettlementStore>()
            .SettleAsync(
                new PaymentSettlement(paymentId, spelling, order.GrandTotal, "LKR", DateTime.UtcNow, "CAP-BANK"),
                Token)));

        await AssertRefusedAsync(() => InScopeAsync(services => services
            .GetRequiredService<IOrderSettlementStore>()
            .RouteToExceptionAsync(
                new PaymentExceptionRouting(
                    paymentId,
                    spelling,
                    PaymentExceptionReasons.AmountMismatch,
                    DateTime.UtcNow,
                    ProviderTransactionId: "CAP-BANK"),
                Token)));

        // Nothing moved: the order is awaiting payment, the payment is the bank's pending one, no invoice
        // was taken, and nobody was alerted about a payment that was never one.
        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(OrderStockState.Reserved, row.StockState);
        Assert.Null(row.InvoiceNumber);

        var payment = Assert.Single(row.Payments);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.False(payment.IsOrderSettlement);
        Assert.Null(payment.ProviderTransactionId);
        Assert.Equal(seriesBefore, await InvoiceSeriesNextValueAsync());
        Assert.Empty(await AlertsAsync(order.PublicId));
    }

    /// <summary>
    /// A bank-transfer payment is refused with the loud error however it is reached; a gateway's payment
    /// spelled differently from how it is stored is simply not found. (Surrounding whitespace is trimmed
    /// first and is not a different spelling: the statements use the stored row's code either way.)
    /// </summary>
    [Theory]
    [InlineData("paypal")]
    [InlineData("PAYPAL")]
    [InlineData("ＰａｙＰａｌ")] // fullwidth PayPal
    public async Task A_gateway_payment_is_found_only_by_the_code_it_is_stored_under(string spelling)
    {
        var arranged = await PlaceProviderOrderAsync();

        var settle = await InScopeAsync(services => services
            .GetRequiredService<IOrderSettlementStore>()
            .SettleAsync(
                new PaymentSettlement(arranged.PaymentId, spelling, UsdAmount, Usd, DateTime.UtcNow, "CAP-SPELLING"),
                Token));

        var route = await InScopeAsync(services => services
            .GetRequiredService<IOrderSettlementStore>()
            .RouteToExceptionAsync(
                new PaymentExceptionRouting(
                    arranged.PaymentId,
                    spelling,
                    PaymentExceptionReasons.OrderNotPayable,
                    DateTime.UtcNow),
                Token));

        // Ordinal, so a different case or a look-alike is another provider's payment, which is not found —
        // the same answer as a payment that does not exist.
        Assert.IsType<PaymentSettlementResult.NotFound>(settle);
        Assert.IsType<PaymentSettlementResult.NotFound>(route);

        var row = await ReadOrderAsync(arranged.PublicId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(PaymentStatus.Initiated, Assert.Single(row.Payments).Status);
        Assert.Empty(await AlertsAsync(arranged.PublicId));
    }

    // ---------------------------------------------------------------------------------------------------
    // What the provider sends has to fit the columns, and an anomaly must never lose the payment
    // ---------------------------------------------------------------------------------------------------

    private static readonly string LongId = new('X', Payment.ProviderTransactionIdMaxLength + 1);

    /// <summary>
    /// <b>An amount mismatch with an id too long for its column used to be recorded nowhere</b>: the
    /// exception path's own <c>UPDATE</c> failed with "String or binary data would be truncated", so the
    /// payment — real money, already taken — was not an exception and not an anything.
    /// </summary>
    [Fact]
    public async Task A_mismatched_payment_with_an_id_too_long_to_store_is_still_recorded_as_an_exception()
    {
        var arranged = await PlaceProviderOrderAsync();

        try
        {
            var result = await SettleAsync(arranged, amount: UsdAmount + 1m, transactionId: LongId);

            var routed = Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(result);
            Assert.Equal(PaymentExceptionReasons.AmountMismatch, routed.Reason);

            var payment = Assert.Single((await ReadOrderAsync(arranged.PublicId)).Payments);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.Equal(PaymentFailureCodes.AmountMismatch, payment.FailureCode);

            // Not stored as given and not stored cut: a truncated id could collide with another payment's in
            // UX_Payments_Provider_Txn. The row says so and quotes where the real one begins, so staff can
            // find the money at the provider.
            Assert.Null(payment.ProviderTransactionId);
            Assert.Contains("could not be stored as given", payment.FailureMessage!, StringComparison.Ordinal);
            Assert.Contains(new string('X', 60), payment.FailureMessage, StringComparison.Ordinal);
            Assert.True(payment.FailureMessage!.Length <= Payment.FailureMessageMaxLength);

            Assert.Single(await AlertsAsync(arranged.PublicId));
        }
        finally
        {
            await DeleteAlertsAsync(arranged.PublicId);
        }
    }

    /// <summary>
    /// A <b>payable</b> order paid with a transaction id that cannot be stored is not refused and not quietly
    /// settled with a falsified reference: the money is on record as an exception, with a reason that says
    /// why — and with the claim on the order undone, so the order is exactly as payable as it was.
    /// </summary>
    [Theory]
    [InlineData("ascii-too-long")]
    [InlineData("sinhala")]
    [InlineData("cyrillic")]
    [InlineData("control")]
    public async Task A_payable_order_paid_with_an_unusable_id_is_kept_as_an_exception_not_settled(string kind)
    {
        var arranged = await PlaceProviderOrderAsync(quantity: 2);
        var stockBefore = await StockAsync(arranged.VariantId);
        var seriesBefore = await InvoiceSeriesNextValueAsync();

        var id = kind switch
        {
            "ascii-too-long" => LongId,
            "sinhala" => "CAP-සිංහල",
            "cyrillic" => "CAP-данные",
            _ => "CAP-1\u0007",
        };

        try
        {
            var result = await SettleAsync(arranged, transactionId: id);

            var routed = Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(result);
            Assert.Equal(PaymentExceptionReasons.ProviderDataInvalid, routed.Reason);

            var row = await ReadOrderAsync(arranged.PublicId);

            // The order is exactly as it was: payable, reserved, no invoice, no deduction — the claim made
            // before the id was looked at went back to the savepoint.
            Assert.Equal(OrderStatus.PendingPayment, row.Status);
            Assert.Equal(OrderStockState.Reserved, row.StockState);
            Assert.Null(row.PaidAtUtc);
            Assert.Null(row.InvoiceNumber);
            Assert.Equal(stockBefore, await StockAsync(arranged.VariantId));
            Assert.Equal(seriesBefore, await InvoiceSeriesNextValueAsync());
            Assert.DoesNotContain(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);

            var payment = Assert.Single(row.Payments);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.False(payment.IsOrderSettlement);
            Assert.Null(payment.ProviderTransactionId);
            Assert.Null(payment.FailureCode);

            // What staff are told: check it with the provider, then refund it or record the reference by
            // hand. Not "accept" - the row would carry no way to find the money at the provider.
            Assert.Contains("could not be stored", payment.FailureMessage!, StringComparison.Ordinal);
            Assert.Contains("Verify the payment with the provider", payment.FailureMessage, StringComparison.Ordinal);
            Assert.Contains("refund it or record the reference by hand", payment.FailureMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("accept", payment.FailureMessage, StringComparison.OrdinalIgnoreCase);

            var alert = Assert.Single(await AlertsAsync(arranged.PublicId));
            Assert.Contains("\"reason\":\"provider_data_invalid\"", alert.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("Check it with the provider", alert.Title, StringComparison.Ordinal);
            Assert.Contains("refund it or record the reference by hand", alert.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("accept", alert.Title, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DeleteAlertsAsync(arranged.PublicId);
        }
    }

    /// <summary>
    /// <b>An unusable id must not hide the more useful fact.</b> The order is looked at first: an order that has
    /// expired is reported as expired, and one that is already paid as already paid, with the id dropped from
    /// the row and a note saying so — "provider data invalid" is only for an order that could have been paid.
    /// </summary>
    [Fact]
    public async Task An_unusable_id_on_an_order_that_has_expired_is_reported_as_expired()
    {
        var arranged = await PlaceProviderOrderAsync();
        Assert.True(await ExpireAsync(arranged.OrderId));

        try
        {
            var result = await SettleAsync(arranged, transactionId: LongId);

            var routed = Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(result);
            Assert.Equal(PaymentExceptionReasons.OrderExpired, routed.Reason);

            var payment = Assert.Single((await ReadOrderAsync(arranged.PublicId)).Payments);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.Null(payment.ProviderTransactionId);
            Assert.Contains("expired", payment.FailureMessage!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("could not be stored as given", payment.FailureMessage, StringComparison.Ordinal);

            var alert = Assert.Single(await AlertsAsync(arranged.PublicId));
            Assert.Contains("\"reason\":\"order_expired\"", alert.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAlertsAsync(arranged.PublicId);
        }
    }

    [Fact]
    public async Task An_unusable_id_on_an_order_that_is_already_paid_is_reported_as_already_paid()
    {
        var arranged = await PlaceProviderOrderAsync();
        Assert.IsType<PaymentSettlementResult.Settled>(await SettleAsync(arranged));

        var extra = await InsertPaymentAsync(arranged.OrderId, PaymentStatus.Initiated);
        var orderBefore = await ReadOrderAsync(arranged.PublicId);

        try
        {
            var result = await SettleAsync(arranged, paymentId: extra, transactionId: LongId);

            var routed = Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(result);
            Assert.Equal(PaymentExceptionReasons.OrderAlreadyPaid, routed.Reason);

            var row = await ReadOrderAsync(arranged.PublicId);
            Assert.Equal(OrderStatus.Paid, row.Status);
            Assert.Equal(orderBefore.InvoiceNumber, row.InvoiceNumber);

            var duplicate = row.Payments.Single(candidate => candidate.Id == extra);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, duplicate.Status);
            Assert.Null(duplicate.ProviderTransactionId);
            Assert.Contains("already paid", duplicate.FailureMessage!, StringComparison.OrdinalIgnoreCase);

            var alert = Assert.Single(await AlertsAsync(arranged.PublicId));
            Assert.Contains("\"reason\":\"order_already_paid\"", alert.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAlertsAsync(arranged.PublicId);
        }
    }

    [Fact]
    public async Task An_id_exactly_as_long_as_its_column_is_stored_as_given()
    {
        var arranged = await PlaceProviderOrderAsync();
        var exact = new string('Z', Payment.ProviderTransactionIdMaxLength);

        Assert.IsType<PaymentSettlementResult.Settled>(await SettleAsync(arranged, transactionId: exact));

        Assert.Equal(exact, Assert.Single((await ReadOrderAsync(arranged.PublicId)).Payments).ProviderTransactionId);
    }

    /// <summary>
    /// The payer reference is informational and unindexed: too long, it is **cut and the payment settles**.
    /// It never holds a payable order's money out.
    /// </summary>
    [Fact]
    public async Task A_payer_reference_too_long_is_cut_and_the_payment_settles_normally()
    {
        var arranged = await PlaceProviderOrderAsync();
        var tooLong = new string('P', Payment.PayerReferenceMaxLength + 25);

        Assert.IsType<PaymentSettlementResult.Settled>(await SettleAsync(arranged, payer: tooLong));

        var row = await ReadOrderAsync(arranged.PublicId);
        Assert.Equal(OrderStatus.Paid, row.Status);

        var payment = Assert.Single(row.Payments);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);
        Assert.Equal(new string('P', Payment.PayerReferenceMaxLength), payment.PayerReference);
        Assert.Empty(await AlertsAsync(arranged.PublicId));
    }

    /// <summary>
    /// On the exception path the payer reference is cut too, with a note: on a mismatch the payment is an
    /// exception whatever the payer reference was, and it must still be recorded.
    /// </summary>
    [Fact]
    public async Task A_payer_reference_too_long_is_cut_on_the_exception_path_too()
    {
        var mismatched = await PlaceProviderOrderAsync();
        var tooLong = new string('P', Payment.PayerReferenceMaxLength + 25);

        try
        {
            var result = await SettleAsync(mismatched, amount: UsdAmount + 2m, payer: tooLong);

            Assert.Equal(
                PaymentExceptionReasons.AmountMismatch,
                Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(result).Reason);

            var payment = Assert.Single((await ReadOrderAsync(mismatched.PublicId)).Payments);
            Assert.Equal(Payment.PayerReferenceMaxLength, payment.PayerReference!.Length);
            Assert.Contains("was cut", payment.FailureMessage!, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAlertsAsync(mismatched.PublicId);
        }
    }

    [Fact]
    public async Task Routing_with_an_unusable_id_records_the_exception_and_does_not_throw()
    {
        var arranged = await PlaceProviderOrderAsync();

        try
        {
            var result = await InScopeAsync(services => services
                .GetRequiredService<IOrderSettlementStore>()
                .RouteToExceptionAsync(
                    new PaymentExceptionRouting(
                        arranged.PaymentId,
                        Provider,
                        PaymentExceptionReasons.OrderNotPayable,
                        DateTime.UtcNow,
                        ConfirmedAmount: 1m,
                        ConfirmedCurrency: new string('C', 500),
                        ProviderTransactionId: LongId,
                        PayerReference: new string('R', 400)),
                    Token));

            Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(result);

            var payment = Assert.Single((await ReadOrderAsync(arranged.PublicId)).Payments);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.Null(payment.ProviderTransactionId);
            Assert.Equal(Payment.PayerReferenceMaxLength, payment.PayerReference!.Length);

            // The currency in the alert is clipped: an ISO code is three letters.
            var alert = Assert.Single(await AlertsAsync(arranged.PublicId));
            Assert.DoesNotContain(new string('C', 20), alert.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAlertsAsync(arranged.PublicId);
        }
    }

    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The bank-transfer refusal, whichever way it is raised: the loud <see cref="ArgumentException"/> for a
    /// code that is the bank's, or — for a spelling of a gateway's — not found. Never settled, never routed.
    /// </summary>
    private static async Task AssertRefusedAsync(Func<Task<PaymentSettlementResult>> act)
    {
        try
        {
            var result = await act();

            // Not found is also a refusal; anything that wrote something is not.
            Assert.IsType<PaymentSettlementResult.NotFound>(result);
        }
        catch (ArgumentException)
        {
            // The loud refusal.
        }
    }
}
