using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Issue #185: the four <c>sales.Payments</c> existence guards inside order claims wait for a payment write
/// that has not committed, whatever plan SQL Server picks, because they read the table
/// <c>WITH (READCOMMITTEDLOCK)</c>.
/// <list type="bullet">
/// <item><description>the cancellation claim (<c>OrderCancellationStore</c>), <c>NOT EXISTS</c> a settlement;</description></item>
/// <item><description>the settlement claim (<c>OrderSettlementEffects.ClaimPendingOrderPaidAsync</c>), reached here through a provider settlement;</description></item>
/// <item><description>the bank late acceptance (<c>BankTransferReviewStore.AcceptLateAsync</c>), <c>NOT EXISTS</c> a settlement;</description></item>
/// <item><description>PayPal's declined path (<c>PayPalPaymentStore.RecordDeclinedAsync</c>), <c>EXISTS</c> a Pending payment.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <b>What the hint is for.</b> Since #163 every database runs <c>READ_COMMITTED_SNAPSHOT</c>, where a plain
/// read takes the last committed version of a row instead of waiting for the transaction that holds it. A
/// guard of the form "<c>UPDATE sales.Orders … AND NOT EXISTS (SELECT 1 FROM sales.Payments …)</c>" then
/// decides on the payments as they were before an in-flight write. The hint puts back the wait the guard had
/// before the option was on, and makes it independent of which table the plan reads first (under the option a
/// statement's read point is fixed at its first versioned read, so a payments read that came before the
/// order's own wait would be stale even though the order's wait is real).
/// <para>
/// <b>Each test holds back exactly one thing: a write to <c>sales.Payments</c> that has not committed, with
/// nothing else locked.</b> The order row is free, so the statement's target does not wait for anybody, and
/// the hint on the payments is the only thing between the guard and the writer. That is also how the pair
/// lesson of #163 is met: nothing else in the statement waits, so a hint cannot survive its own removal.
/// No write in the code base settles a payment without holding the order row first, so this is the shape the
/// guards' own remarks describe as "a settlement in a transaction of its own, which is exactly what a webhook
/// is": the shape the guards exist for, and one no in-tree path produces today.
/// </para>
/// <para>
/// <b>Why the plan is not forced.</b> Issue #185 asked for a test that forces a plan reading
/// <c>sales.Payments</c> before the order. None exists for these four statements. Their subqueries are keyed on
/// a parameter, so there is no join predicate between the order and the payments, and the optimiser has one
/// operator to use, a nested loops anti semi join with the order seek outside and the payments seek inside.
/// Measured on SQL Server 2025 (LocalDB) by running each production statement through an interceptor that
/// appended a query hint, with a writer holding both the order row and the payment: <c>OPTION (HASH JOIN)</c>
/// and <c>OPTION (MERGE JOIN)</c> fail with error 8622 ("could not produce a query plan because of the
/// hints") for all four statements, and <c>FORCE ORDER</c>, <c>LOOP JOIN</c> and <c>RECOMPILE</c> give the
/// natural plan, which waits at the order row first and so decides on fresh payments with or without the
/// hint. A payment write that has not committed, though, is invisible to an unhinted guard <i>in every plan
/// these statements can have</i>, which is what these tests use. The plan-order exposure the issue describes
/// needs a subquery correlated with the target (an equality between the two tables), and these are not;
/// if one ever becomes so, a forced payments-first test belongs here too.
/// </para>
/// <para>
/// <b>Every guard has a control that removes its hint at run time</b> (<see cref="HintStrippingInterceptor"/>
/// strips <c>WITH (READCOMMITTEDLOCK)</c> from that one statement and nothing else) and asserts the wrong
/// outcome it permits. That is how this suite shows, on every run and without editing source, that each
/// scenario can fail and that each hint alone is what stands between its guard and the writer. If an engine
/// change made the unhinted guard wait, the control would fail and the hint would be redundant.
/// </para>
/// </remarks>
public sealed class PaymentGuardLockTests(SqlServerDatabaseFixture sqlServer) : ProviderOrderTestBase(sqlServer)
{
    /// <summary>How long a test lets the guarded call run before it gives up waiting for it to block.</summary>
    private static readonly TimeSpan BlockedWindow = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan Finish = TimeSpan.FromSeconds(60);

    private readonly HintStrippingInterceptor _stripper = new();

    private ApiFactory? _host;

    /// <summary>A second host over the same database whose commands pass through <see cref="_stripper"/>.</summary>
    private ApiFactory Host
    {
        get
        {
            SqlServer.SkipIfUnavailable();
            return _host ??= new ApiFactory(SqlServer.ConnectionString, commandInterceptor: _stripper);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    // ---------------------------------------------------------------------------------------------------
    // Cancellation: the claim has no other backstop (issue #185's priority).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_cancellation_claim_waits_for_a_settlement_that_has_not_committed()
    {
        var seen = await CancelBesideAnInFlightSettlementAsync(withoutTheHint: false);

        Assert.True(
            seen.Observed.Failure is null,
            $"The cancellation threw: {seen.Observed.Failure}");

        Assert.True(
            seen.Observed.BlockedOn?.Contains("NOT EXISTS", StringComparison.Ordinal) == true,
            "The claim did not wait for the settlement that was still uncommitted. The statement that was blocked "
            + $"(null: none ever was) was: {seen.Observed.BlockedOn} [the call returned {seen.Observed.Value}]");

        var refused = Assert.IsType<CancelOrderOutcome.Refused>(seen.Observed.Value);
        Assert.Equal(CancelOrderRefusalCodes.PaymentSettling, refused.Code);

        var row = await ReadOrderAsync(seen.PublicId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(OrderStockState.Reserved, row.StockState);
        Assert.Null(row.ClosedAtUtc);
        Assert.Equal(seen.StockBefore, await StockAsync(seen.VariantId));

        var payment = Assert.Single(row.Payments);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(payment.IsOrderSettlement);
    }

    /// <summary>
    /// The control: with the hint taken off this one statement the same scenario cancels an order whose
    /// settlement is in flight, which is the defect the hint closes.
    /// </summary>
    [Fact]
    public async Task Without_its_hint_the_cancellation_claim_cancels_an_order_whose_settlement_is_in_flight()
    {
        var seen = await CancelBesideAnInFlightSettlementAsync(withoutTheHint: true);

        Assert.True(seen.HintWasRemoved, "The control removed no hint: the statement carries none, or its text moved.");
        Assert.True(seen.Observed.Failure is null, $"The cancellation threw: {seen.Observed.Failure}");
        Assert.IsType<CancelOrderOutcome.Applied>(seen.Observed.Value);

        // The wrong state, whole: closed and released while the payment that settles it is Succeeded.
        var row = await ReadOrderAsync(seen.PublicId);
        Assert.Equal(OrderStatus.Cancelled, row.Status);
        Assert.Equal(OrderStockState.Released, row.StockState);
        Assert.True(Assert.Single(row.Payments).IsOrderSettlement);
    }

    private sealed record CancelSeen(
        Observed<CancelOrderOutcome> Observed,
        Guid PublicId,
        int VariantId,
        (int OnHand, int Reserved, int Available) StockBefore,
        bool HintWasRemoved);

    private async Task<CancelSeen> CancelBesideAnInFlightSettlementAsync(bool withoutTheHint)
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 5);
        var orderId = await OrderIdAsync(order.PublicId);
        var paymentId = await OnlyPaymentIdAsync(orderId);
        var stockBefore = await StockAsync(variantId);

        Aim(
            text => text.Contains("UPDATE sales.Orders", StringComparison.Ordinal)
                && text.Contains("settlement.IsOrderSettlement = 1", StringComparison.Ordinal),
            withoutTheHint);

        var observed = await RunBesideAsync(
            (connection, transaction) => SetPaymentAsync(connection, transaction, paymentId, PaymentStatus.Succeeded, settles: true),
            services => services.GetRequiredService<IOrderCancellationStore>().CancelAsync(
                new OrderCancellation(order.PublicId, OrderCancelledBy.Customer, null, null, DateTime.UtcNow),
                Token));

        return new CancelSeen(observed, order.PublicId, variantId, stockBefore, _stripper.Stripped == 1);
    }

    // ---------------------------------------------------------------------------------------------------
    // Settlement: UX_Payments_OneSettlement is the backstop, which turns a stale read into a thrown 2627.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_settlement_claim_waits_for_another_payments_settlement_that_has_not_committed()
    {
        var seen = await SettleBesideAnInFlightSettlementAsync(withoutTheHint: false);

        Assert.True(
            seen.Observed.Failure is null,
            "The settlement threw instead of recording the money as an exception (a unique-index violation is "
            + $"what a claim that decided on stale payments runs into): {seen.Observed.Failure}");

        Assert.True(
            seen.Observed.BlockedOn?.Contains("NOT EXISTS", StringComparison.Ordinal) == true,
            "The claim did not wait for the settlement that was still uncommitted. The statement that was blocked "
            + $"(null: none ever was) was: {seen.Observed.BlockedOn} [the call returned {seen.Observed.Value}]");

        var routed = Assert.IsType<PaymentSettlementResult.CapturedRequiresRefund>(seen.Observed.Value);
        Assert.Equal(PaymentExceptionReasons.OrderNotPayable, routed.Reason);

        var row = await ReadOrderAsync(seen.PublicId);
        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(OrderStockState.Reserved, row.StockState);
        Assert.Equal(PaymentStatus.CapturedRequiresRefund, row.Payments.Single(payment => payment.Id == seen.PaymentId).Status);
        Assert.Equal(PaymentStatus.Succeeded, row.Payments.Single(payment => payment.Id == seen.OtherPaymentId).Status);
    }

    [Fact]
    public async Task Without_its_hint_the_settlement_claim_runs_into_the_unique_index_instead_of_refusing()
    {
        var seen = await SettleBesideAnInFlightSettlementAsync(withoutTheHint: true);

        Assert.True(seen.HintWasRemoved, "The control removed no hint: the statement carries none, or its text moved.");

        // The claim took the order on a read that could not see the other settlement; only the unique index
        // stopped the second one, as an error.
        Assert.True(
            seen.Observed.Failure is not null && IsUniqueViolation(seen.Observed.Failure),
            $"Expected a unique-index violation (2601 or 2627) from the second settlement; got "
            + $"{seen.Observed.Failure?.GetType().Name ?? "no exception"} with result {seen.Observed.Value}.");
    }

    private sealed record SettleSeen(
        Observed<PaymentSettlementResult> Observed,
        Guid PublicId,
        int PaymentId,
        int OtherPaymentId,
        bool HintWasRemoved);

    private async Task<SettleSeen> SettleBesideAnInFlightSettlementAsync(bool withoutTheHint)
    {
        // A provider order whose payment is Initiated, and a second payment of the same order that is not open.
        var arranged = await PlaceProviderOrderAsync();
        var otherPaymentId = await InsertPaymentAsync(arranged.OrderId, PaymentStatus.Failed);

        Aim(
            text => text.Contains("UPDATE sales.Orders", StringComparison.Ordinal)
                && text.Contains("FROM sales.Payments s", StringComparison.Ordinal)
                && text.Contains("CaptureLockedUntilUtc = NULL", StringComparison.Ordinal),
            withoutTheHint);

        var observed = await RunBesideAsync(
            (connection, transaction) => SetPaymentAsync(connection, transaction, otherPaymentId, PaymentStatus.Succeeded, settles: true),
            services => services.GetRequiredService<IOrderSettlementStore>().SettleAsync(
                new PaymentSettlement(
                    arranged.PaymentId,
                    Provider,
                    UsdAmount,
                    Usd,
                    DateTime.UtcNow,
                    SalesRowWriter.Unique("CAP-"),
                    null),
                Token));

        return new SettleSeen(observed, arranged.PublicId, arranged.PaymentId, otherPaymentId, _stripper.Stripped == 1);
    }

    // ---------------------------------------------------------------------------------------------------
    // Bank late acceptance: the same guard on an Expired order.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_late_acceptance_claim_waits_for_a_settlement_that_has_not_committed()
    {
        var seen = await AcceptLateBesideAnInFlightSettlementAsync(withoutTheHint: false);

        Assert.True(
            seen.Observed.Failure is null,
            "The late acceptance threw instead of refusing (a unique-index violation is what a claim that decided "
            + $"on stale payments runs into): {seen.Observed.Failure}");

        Assert.True(
            seen.Observed.BlockedOn?.Contains("NOT EXISTS", StringComparison.Ordinal) == true,
            "The claim did not wait for the settlement that was still uncommitted. The statement that was blocked "
            + $"(null: none ever was) was: {seen.Observed.BlockedOn} [the call returned {seen.Observed.Value}]");

        // AlreadySettled is the refusal that names the NOT EXISTS guard, as opposed to the status guards.
        var refused = Assert.IsType<BankTransferDecisionResult.Refused>(seen.Observed.Value);
        Assert.Equal(BankTransferRefusalCodes.AlreadySettled, refused.Code);

        var row = await ReadOrderAsync(seen.PublicId);
        Assert.Equal(OrderStatus.Expired, row.Status);
        Assert.Equal(OrderStockState.Released, row.StockState);
        Assert.Null(row.PaidAtUtc);
        Assert.Equal(seen.StockBefore, await StockAsync(seen.VariantId));
    }

    [Fact]
    public async Task Without_its_hint_the_late_acceptance_claim_runs_into_the_unique_index_instead_of_refusing()
    {
        var seen = await AcceptLateBesideAnInFlightSettlementAsync(withoutTheHint: true);

        Assert.True(seen.HintWasRemoved, "The control removed no hint: the statement carries none, or its text moved.");
        Assert.True(
            seen.Observed.Failure is not null && IsUniqueViolation(seen.Observed.Failure),
            $"Expected a unique-index violation (2601 or 2627) from the second settlement; got "
            + $"{seen.Observed.Failure?.GetType().Name ?? "no exception"} with result {seen.Observed.Value}.");
    }

    private sealed record LateSeen(
        Observed<BankTransferDecisionResult> Observed,
        Guid PublicId,
        int VariantId,
        (int OnHand, int Reserved, int Available) StockBefore,
        bool HintWasRemoved);

    private async Task<LateSeen> AcceptLateBesideAnInFlightSettlementAsync(bool withoutTheHint)
    {
        var (order, variantId) = await PlaceOrderAsync(quantity: 1, onHand: 5);
        var orderId = await OrderIdAsync(order.PublicId);

        // Expired the way the sweep does it (clock moved past the deadline), so the order is Expired/Released
        // with its payment Expired, which is what a late acceptance starts from.
        Assert.True(await ExpireAsync(orderId));

        // A second bank payment on the order, not open, that the in-flight write settles.
        int otherPaymentId;
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var total = await db.Orders.Where(candidate => candidate.Id == orderId).Select(candidate => candidate.GrandTotal).SingleAsync(Token);
            var other = new Payment
            {
                OrderId = orderId,
                ProviderCode = PaymentProviderCodes.BankTransfer,
                Status = PaymentStatus.Failed,
                Amount = total,
                Currency = SalesCurrency.Lkr,
                CreatedAtUtc = DateTime.UtcNow,
            };

            db.Payments.Add(other);
            await db.SaveChangesAsync(Token);
            otherPaymentId = other.Id;
        }

        var stockBefore = await StockAsync(variantId);

        Aim(
            text => text.Contains("UPDATE sales.Orders", StringComparison.Ordinal)
                && text.Contains("FROM sales.Payments s", StringComparison.Ordinal)
                && !text.Contains("CaptureLockedUntilUtc = NULL", StringComparison.Ordinal),
            withoutTheHint);

        var observed = await RunBesideAsync(
            (connection, transaction) => SetPaymentAsync(connection, transaction, otherPaymentId, PaymentStatus.Succeeded, settles: true),
            services => services.GetRequiredService<IBankTransferReviewStore>().AcceptLateAsync(
                new BankTransferDecision(orderId, Staff(), null, DateTime.UtcNow),
                Token));

        return new LateSeen(observed, order.PublicId, variantId, stockBefore, _stripper.Stripped == 1);
    }

    // ---------------------------------------------------------------------------------------------------
    // PayPal declined: the guard decides whether the hold is cut back.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_declined_path_waits_for_a_payment_that_is_moving_to_pending_and_cuts_the_hold_back()
    {
        var seen = await DeclineBesideAnInFlightPendingAsync(withoutTheHint: false);

        Assert.True(seen.Observed.Failure is null, $"The decline threw: {seen.Observed.Failure}");

        Assert.True(
            seen.Observed.BlockedOn?.Contains("pending.Id", StringComparison.Ordinal) == true,
            "The decline did not wait for the payment write that was still uncommitted. The statement that was "
            + $"blocked (null: none ever was) was: {seen.Observed.BlockedOn} [the call returned {seen.Observed.Value}]");

        Assert.Equal(PayPalMoveResult.Applied, seen.Observed.Value);

        // Doc 08 §4.2 step 4 / R2: the payment was Pending, so the long hold is cut back to now + 30 minutes.
        Assert.True(
            Math.Abs((seen.DueAtUtc - seen.Now.AddMinutes(CutMinutes)).TotalSeconds) < 5,
            $"The hold was not cut back to now + {CutMinutes} minutes: it is now due at {seen.DueAtUtc:O} (now {seen.Now:O}).");
        Assert.Equal(PaymentStatus.Failed, seen.PaymentStatus);
    }

    [Fact]
    public async Task Without_its_hint_the_declined_path_leaves_the_long_hold_in_place()
    {
        var seen = await DeclineBesideAnInFlightPendingAsync(withoutTheHint: true);

        Assert.True(seen.HintWasRemoved, "The control removed no hint: the statement carries none, or its text moved.");
        Assert.True(seen.Observed.Failure is null, $"The decline threw: {seen.Observed.Failure}");
        Assert.Equal(PayPalMoveResult.Applied, seen.Observed.Value);
        Assert.Equal(PaymentStatus.Failed, seen.PaymentStatus);

        // The payment was Pending by the time the decline committed, but the guard read it as Initiated, so
        // the 48-hour hold stayed: the order stayed payable far longer than R2 allows after a decline.
        Assert.True(
            seen.DueAtUtc > seen.Now.AddHours(1),
            $"Expected the hold to be left alone; it is due at {seen.DueAtUtc:O} (now {seen.Now:O}).");
    }

    private const int CutMinutes = 30;

    private sealed record DeclineSeen(
        Observed<PayPalMoveResult> Observed,
        DateTime Now,
        DateTime DueAtUtc,
        PaymentStatus PaymentStatus,
        bool HintWasRemoved);

    private async Task<DeclineSeen> DeclineBesideAnInFlightPendingAsync(bool withoutTheHint)
    {
        // A PayPal order with its attempt Initiated; the in-flight write moves that attempt to Pending.
        var arranged = await PlaceProviderOrderAsync();

        Aim(text => text.Contains("pending.Id", StringComparison.Ordinal), withoutTheHint);

        var now = DateTime.UtcNow;
        var observed = await RunBesideAsync(
            (connection, transaction) => SetPaymentAsync(connection, transaction, arranged.PaymentId, PaymentStatus.Pending, settles: false),
            services => services.GetRequiredService<IPayPalPaymentStore>().RecordDeclinedAsync(
                arranged.OrderId,
                arranged.PaymentId,
                "INSTRUMENT_DECLINED",
                null,
                CutMinutes,
                now,
                Token));

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var due = await db.Orders.AsNoTracking().Where(order => order.Id == arranged.OrderId).Select(order => order.PaymentDueAtUtc).SingleAsync(Token);
        var status = await db.Payments.AsNoTracking().Where(payment => payment.Id == arranged.PaymentId).Select(payment => payment.Status).SingleAsync(Token);

        return new DeclineSeen(observed, now, due, status, _stripper.Stripped == 1);
    }

    // ---------------------------------------------------------------------------------------------------
    // The arrangement every test shares.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>What the guarded call did, and which statement it was waiting in when the writer let go.</summary>
    private sealed record Observed<T>(T? Value, Exception? Failure, string? BlockedOn);

    /// <summary>
    /// Points the interceptor at one statement. With <paramref name="withoutTheHint"/> it removes that
    /// statement's <c>WITH (READCOMMITTEDLOCK)</c> on its way to the server; otherwise it leaves every command
    /// alone.
    /// </summary>
    private void Aim(Func<string, bool> statement, bool withoutTheHint)
    {
        if (withoutTheHint)
        {
            _stripper.Arm(statement);
        }
        else
        {
            _stripper.Disarm();
        }
    }

    /// <summary>
    /// Builds the host and runs one trivial command through it, so the first use of its container and its
    /// model is not paid inside the window in which the test waits for the guarded call to block.
    /// </summary>
    private async Task WarmHostAsync()
    {
        using var scope = Host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync("SELECT 1", Token);
    }

    /// <summary>
    /// Writes the payment inside a transaction it leaves open, starts the guarded call, waits until SQL Server
    /// reports it blocked by that transaction (or finished), records which statement it was blocked in, and only
    /// then commits the write and lets the call finish.
    /// </summary>
    private async Task<Observed<T>> RunBesideAsync<T>(
        Func<SqlConnection, SqlTransaction, Task> writeInFlight,
        Func<IServiceProvider, Task<T>> guardedCall)
    {
        await WarmHostAsync();

        await using var writer = new SqlConnection(SqlServer.ConnectionString);
        await writer.OpenAsync(Token);
        await using var transaction = (SqlTransaction)await writer.BeginTransactionAsync(Token);

        int spid;
        await using (var command = new SqlCommand("SELECT @@SPID", writer, transaction))
        {
            spid = Convert.ToInt32(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
        }

        await writeInFlight(writer, transaction);

        var call = Task.Run(
            async () =>
            {
                using var scope = Host.Services.CreateScope();
                return await guardedCall(scope.ServiceProvider);
            },
            Token);

        string? blockedOn = null;
        var give = DateTime.UtcNow + BlockedWindow;
        while (!call.IsCompleted && blockedOn is null && DateTime.UtcNow < give)
        {
            blockedOn = await BlockedStatementAsync(spid);

            if (blockedOn is null)
            {
                await Task.Delay(50, Token);
            }
        }

        await transaction.CommitAsync(Token);

        try
        {
            return new Observed<T>(await call.WaitAsync(Finish, Token), null, blockedOn);
        }
#pragma warning disable CA1031 // The call's failure is the observation: a test reports it, whatever it is.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return new Observed<T>(default, exception, blockedOn);
        }
    }

    /// <summary>The text of a statement SQL Server reports as blocked by <paramref name="blockerSpid"/>, or null.</summary>
    private async Task<string?> BlockedStatementAsync(int blockerSpid)
    {
        await using var monitor = new SqlConnection(SqlServer.ConnectionString);
        await monitor.OpenAsync(Token);

        await using var command = monitor.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) t.text
              FROM sys.dm_exec_requests AS r
             CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS t
             WHERE r.blocking_session_id = @spid;
            """;
        command.Parameters.AddWithValue("@spid", blockerSpid);

        return await command.ExecuteScalarAsync(Token) as string;
    }

    /// <summary>
    /// The write that has not committed. A settlement (<paramref name="settles"/>) is a <c>Succeeded</c>
    /// payment flagged as the order's settling one, as <c>CK_Payments_IsOrderSettlement</c> requires.
    /// </summary>
    private static async Task SetPaymentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int paymentId,
        PaymentStatus status,
        bool settles)
    {
        await using var command = new SqlCommand(
            "UPDATE sales.Payments SET Status = @status, IsOrderSettlement = @settles WHERE Id = @id",
            connection,
            transaction);
        command.Parameters.AddWithValue("@status", (byte)status);
        command.Parameters.AddWithValue("@settles", settles);
        command.Parameters.AddWithValue("@id", paymentId);

        Assert.Equal(1, await command.ExecuteNonQueryAsync(Token));
    }

    private async Task<int> OnlyPaymentIdAsync(int orderId)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == orderId)
            .Select(payment => payment.Id)
            .SingleAsync(Token);
    }

    /// <summary>SQL Server errors 2601 and 2627: a unique index or constraint refused a duplicate key.</summary>
    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && sql.Errors.Cast<SqlError>().Any(error => error.Number is 2601 or 2627))
            {
                return true;
            }
        }

        return false;
    }
}
