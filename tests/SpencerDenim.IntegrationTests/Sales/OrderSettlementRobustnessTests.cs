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
    /// order A is settled. A's payment starts <c>Capturing</c> or <c>Failed</c> so that the close-other-attempts
    /// statement runs; with it unhinted it blocks here, with <c>IX_Payments_OrderId</c> it never looks at B.
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

    [Theory]
    [InlineData(PaymentStatus.Initiated, false)]
    [InlineData(PaymentStatus.Pending, false)]
    [InlineData(PaymentStatus.Capturing, true)]
    [InlineData(PaymentStatus.Failed, true)]
    [InlineData(PaymentStatus.Cancelled, true)]
    [InlineData(PaymentStatus.Expired, true)]
    [InlineData(PaymentStatus.Succeeded, false)]
    [InlineData(PaymentStatus.CapturedRequiresRefund, false)]
    public void Only_a_payment_outside_the_one_open_index_can_have_another_attempt_beside_it(
        PaymentStatus status,
        bool expected)
    {
        // UX_Payments_OneOpen allows one Initiated-or-Pending row per order, so a payment that is one of
        // those is the open attempt. The settled statuses never reach the question.
        Assert.Equal(
            expected,
            SpencerDenim.Infrastructure.Sales.Payments.OrderSettlementEffects.CanHaveOtherOpenAttempt(status));
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
    /// A payable order paid with an unusable id is not refused and not quietly settled with a falsified
    /// reference: the money is on record as an exception, with a reason that says why.
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

            // The order is exactly as it was: payable, reserved, no invoice, no deduction.
            Assert.Equal(OrderStatus.PendingPayment, row.Status);
            Assert.Equal(OrderStockState.Reserved, row.StockState);
            Assert.Null(row.InvoiceNumber);
            Assert.Equal(stockBefore, await StockAsync(arranged.VariantId));
            Assert.Equal(seriesBefore, await InvoiceSeriesNextValueAsync());

            var payment = Assert.Single(row.Payments);
            Assert.Equal(PaymentStatus.CapturedRequiresRefund, payment.Status);
            Assert.False(payment.IsOrderSettlement);
            Assert.Null(payment.ProviderTransactionId);

            // Not AMOUNT_MISMATCH: staff may accept this one once they have looked.
            Assert.Null(payment.FailureCode);
            Assert.Contains("could not be stored", payment.FailureMessage!, StringComparison.Ordinal);

            var alert = Assert.Single(await AlertsAsync(arranged.PublicId));
            Assert.Contains("\"reason\":\"provider_data_invalid\"", alert.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAlertsAsync(arranged.PublicId);
        }
    }

    [Fact]
    public async Task An_unusable_id_on_an_order_that_has_expired_is_still_recorded()
    {
        var arranged = await PlaceProviderOrderAsync();
        Assert.True(await ExpireAsync(arranged.OrderId));

        try
        {
            var result = await SettleAsync(arranged, transactionId: LongId);

            // Recorded, whichever reason wins; what matters is that the anomaly did not stop it.
            Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(result);
            Assert.Equal(
                PaymentStatus.CapturedRequiresRefund,
                Assert.Single((await ReadOrderAsync(arranged.PublicId)).Payments).Status);
            Assert.Single(await AlertsAsync(arranged.PublicId));
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

    [Fact]
    public async Task A_payer_reference_too_long_is_cut_on_the_exception_path_and_makes_a_settlement_an_exception()
    {
        var tooLong = new string('P', Payment.PayerReferenceMaxLength + 25);

        // On a mismatch the payment is an exception whatever the payer reference was, and it is stored cut.
        var mismatched = await PlaceProviderOrderAsync();

        try
        {
            var result = await SettleAsync(mismatched, amount: UsdAmount + 2m, payer: tooLong);

            Assert.Equal(
                PaymentExceptionReasons.AmountMismatch,
                Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(result).Reason);

            var payment = Assert.Single((await ReadOrderAsync(mismatched.PublicId)).Payments);
            Assert.Equal(Payment.PayerReferenceMaxLength, payment.PayerReference!.Length);
            Assert.Contains("was cut", payment.FailureMessage!, StringComparison.Ordinal);

            // On a payable order a cut payer reference is a provider anomaly, kept as an exception.
            var payable = await PlaceProviderOrderAsync();
            var second = await SettleAsync(payable, payer: tooLong);

            Assert.Equal(
                PaymentExceptionReasons.ProviderDataInvalid,
                Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(second).Reason);

            await DeleteAlertsAsync(payable.PublicId);
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
