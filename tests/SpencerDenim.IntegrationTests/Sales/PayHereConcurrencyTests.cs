using System.Net;
using System.Text.RegularExpressions;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// PayHere notifications that arrive <b>together</b> for the same order (doc 08 s5.3 / s4.2 N6: lock the
/// order row before the payment row, everywhere).
/// <para>
/// <b>Why this class exists.</b> The tester fired a signed success and a signed failure at 20 orders at once
/// and counted 96 deadlock victims. Nothing failed from outside - EF re-runs a transaction that lost a
/// deadlock, so a request answers 200 and the row assertions mostly hold - and that is the point: a test
/// that checks only the final state is green on a design that deadlocks every other request. So these
/// record the victims as well (<see cref="DeadlockVictimRecorder"/>) and say which statement lost.
/// </para>
/// <para>
/// <b>Two shapes, because there are two different faults.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>One order at a time</b> (the first two tests): the pair for an order is released together, but orders
/// are taken in turn. Only transactions on the <i>same</i> order ever overlap, so a victim here can only be a
/// lock-order inversion between those transactions - PayHere's own fault - and the assertion is
/// <b>none at all</b>.
/// </description></item>
/// <item><description>
/// <b>Every order at once</b> (the third): this adds the cross-order interaction the tester measured, where
/// <c>OrderSettlementEffects.CloseOtherOpenAttemptsAsync</c> (groundwork) scans payment rows of other orders.
/// That statement is not PayHere's and is being fixed on the groundwork branch, so here the assertions are
/// that every order ends consistent once the retry sweep has run, and that <b>no statement PayHere owns</b>
/// is ever the victim. After the branches are stacked the total can be tightened to zero (see the journal).
/// </description></item>
/// </list>
/// </summary>
public sealed class PayHereConcurrencyTests(SqlServerDatabaseFixture sqlServer) : PayHereTestBase(sqlServer)
{
    private const int Orders = 20;

    [Fact]
    public async Task A_success_and_a_failure_for_the_same_order_arriving_together_end_consistent_and_deadlock_nobody()
    {
        await ConfigurePayHereAsync();

        var placed = await PlaceAsync(Orders);
        var successIds = placed.ToDictionary(order => order.PublicId, _ => NewPayHerePaymentId());

        foreach (var order in placed)
        {
            await FireTogetherAsync(
                NotifyForm(order.OrderReference, order.GrandTotal, "2", payHerePaymentId: successIds[order.PublicId]),
                NotifyForm(order.OrderReference, order.GrandTotal, "-2", payHerePaymentId: NewPayHerePaymentId()));
        }

        foreach (var order in placed)
        {
            await AssertSettledOnceAsync(order, successIds[order.PublicId]);
        }

        // Every order paid, so every order was given its own invoice number.
        var invoices = new List<string>();

        foreach (var order in placed)
        {
            invoices.Add((await ReadOrderAsync(order.PublicId)).InvoiceNumber!);
        }

        Assert.Equal(Orders, invoices.Distinct(StringComparer.Ordinal).Count());

        AssertNoDeadlockVictims();
    }

    /// <summary>
    /// A pending notice, a failure and a success for the same order together - the order a card declined
    /// after pending produces. Locks are taken order-then-payment in all three.
    /// </summary>
    [Fact]
    public async Task Pending_failed_and_success_together_for_the_same_order_end_consistent_and_deadlock_nobody()
    {
        await ConfigurePayHereAsync();

        var placed = await PlaceAsync(10);
        var successIds = placed.ToDictionary(order => order.PublicId, _ => NewPayHerePaymentId());

        foreach (var order in placed)
        {
            await FireTogetherAsync(
                NotifyForm(order.OrderReference, order.GrandTotal, "0", payHerePaymentId: NewPayHerePaymentId()),
                NotifyForm(order.OrderReference, order.GrandTotal, "-2", payHerePaymentId: NewPayHerePaymentId()),
                NotifyForm(order.OrderReference, order.GrandTotal, "2", payHerePaymentId: successIds[order.PublicId]));
        }

        foreach (var order in placed)
        {
            // Whatever the interleaving, the success is the last word about the money.
            var row = await ReadOrderAsync(order.PublicId);

            Assert.Equal(OrderStatus.Paid, row.Status);
            Assert.Single(row.Payments, payment => payment.IsOrderSettlement);
            Assert.DoesNotContain(
                row.Payments,
                payment => payment.Status is PaymentStatus.Initiated or PaymentStatus.Pending);
            Assert.Equal(successIds[order.PublicId], row.Payments.Single(payment => payment.IsOrderSettlement).ProviderReference);
        }

        AssertNoDeadlockVictims();
    }

    /// <summary>
    /// The rule itself, without relying on a race. Whether two transactions deadlock depends on timing, so the
    /// tests above can pass by luck on a bad design (with the order lock removed, the same-order pair test
    /// still passed). This one is true or false every time: for each notice that writes, driven one at a time,
    /// <b>the first statement that touches the order or the payment is the order-row lock</b> (doc 08
    /// s5.3 / N6) - taken before any payment row, which is the order a settlement takes them in.
    /// </summary>
    [Theory]
    [InlineData("0", "a pending notice")]
    [InlineData("-2", "a failure notice")]
    [InlineData("2", "a success notice (its reference write, before the settlement)")]
    public async Task The_first_statement_that_touches_the_order_or_the_payment_is_the_order_lock(string statusCode, string what)
    {
        await ConfigurePayHereAsync();

        var order = await PlacePayHereOrderAsync();

        Statements.Clear();

        using var response = await PostNotifyAsync(
            NotifyForm(order.OrderReference, order.GrandTotal, statusCode, payHerePaymentId: NewPayHerePaymentId()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var statements = Statements.Statements;

        var firstLock = IndexOf(statements, IsOrderLock);
        var firstWrite = IndexOf(statements, IsOrderOrPaymentWrite);

        Assert.True(firstLock >= 0, $"{what} never took the order row's update lock. Statements:{Dump(statements)}");
        Assert.True(firstWrite >= 0, $"{what} wrote nothing to the order or the payment. Statements:{Dump(statements)}");
        Assert.True(
            firstLock < firstWrite,
            $"{what} wrote to the order or the payment (statement {firstWrite}) before it locked the order "
            + $"row (statement {firstLock}). Statements:{Dump(statements)}");
        Assert.Equal(0, DeadlockVictims.Count);
    }

    private static bool IsOrderLock(string sql) =>
        sql.Contains("sales.Orders WITH (UPDLOCK, ROWLOCK)", StringComparison.Ordinal);

    /// <summary>An UPDATE or INSERT whose target is the order, the payment or the <c>UPDATE o</c> alias of it.</summary>
    private static bool IsOrderOrPaymentWrite(string sql) =>
        Regex.IsMatch(
            sql,
            @"^\s*(UPDATE\s+(sales\.(Orders|Payments)\b|o\b)|INSERT\s+INTO\s+sales\.Payments\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    private static int IndexOf(IReadOnlyList<string> statements, Func<string, bool> predicate)
    {
        for (var index = 0; index < statements.Count; index++)
        {
            if (predicate(statements[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static string Dump(IReadOnlyList<string> statements) =>
        string.Concat(statements.Select((sql, index) =>
        {
            var text = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return $"{Environment.NewLine}  [{index}] {(text.Length <= 160 ? text : text[..160])}";
        }));

    /// <summary>
    /// Forty notifications for twenty orders released in the same instant. Every order must end paid, once,
    /// consistent - after the retry sweep has finished anything a first delivery could not (a deadlock between
    /// two <i>settlements</i> is the groundwork's to fix, and the system's answer to it is the sweep) - and
    /// no statement PayHere owns may be the one SQL Server kills.
    /// </summary>
    [Fact]
    public async Task Every_order_at_once_ends_consistent_after_the_sweep_and_no_payhere_statement_is_a_victim()
    {
        await ConfigurePayHereAsync();

        var placed = await PlaceAsync(Orders);
        var successIds = placed.ToDictionary(order => order.PublicId, _ => NewPayHerePaymentId());
        var gate = new TaskCompletionSource();

        var requests = placed
            .SelectMany(order => new[]
            {
                NotifyForm(order.OrderReference, order.GrandTotal, "2", payHerePaymentId: successIds[order.PublicId]),
                NotifyForm(order.OrderReference, order.GrandTotal, "-2", payHerePaymentId: NewPayHerePaymentId()),
            })
            .Select(form => Task.Run(
                async () =>
                {
                    await gate.Task;

                    using var response = await PostNotifyAsync(form);
                    return response.StatusCode;
                },
                Token))
            .ToList();

        gate.SetResult();

        Assert.All(await Task.WhenAll(requests), status => Assert.Equal(HttpStatusCode.OK, status));

        // The answer is 200 once an event is stored, whether or not applying it survived; the sweep finishes
        // the rest, and this is the system doing what P8b step 5 says it does.
        var swept = await DrainRetrySweepAsync();

        foreach (var order in placed)
        {
            await AssertSettledOnceAsync(order, successIds[order.PublicId]);
        }

        var owned = DeadlockVictims.Victims.Where(IsPayHereStatement).ToList();

        // Said, not asserted: the total belongs to the groundwork until the branches are stacked.
        TestContext.Current.SendDiagnosticMessage(
            $"PayHere concurrency: {DeadlockVictims.Count} deadlock victim(s) in all, {owned.Count} PayHere-owned; "
            + $"the retry sweep finished {swept} event(s).");

        Assert.True(
            owned.Count == 0,
            $"{owned.Count} statement(s) PayHere owns were chosen as deadlock victims:{Environment.NewLine}"
            + string.Join(Environment.NewLine, owned));
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private async Task<List<PayHereOrder>> PlaceAsync(int count)
    {
        var placed = new List<PayHereOrder>();

        for (var index = 0; index < count; index++)
        {
            placed.Add(await PlacePayHereOrderAsync());
        }

        return placed;
    }

    /// <summary>Posts every form at the same instant and requires all of them to be answered 200.</summary>
    private async Task FireTogetherAsync(params Dictionary<string, string>[] forms)
    {
        var gate = new TaskCompletionSource();

        var requests = forms
            .Select(form => Task.Run(
                async () =>
                {
                    await gate.Task;

                    using var response = await PostNotifyAsync(form);
                    return response.StatusCode;
                },
                Token))
            .ToList();

        gate.SetResult();

        Assert.All(await Task.WhenAll(requests), status => Assert.Equal(HttpStatusCode.OK, status));
    }

    /// <summary>
    /// The statements PayHere's own store sends: the order-lock, the replacement-attempt insert, the failure
    /// and pending updates, and the reference write. Recognised by their text, which is deliberate - the
    /// recorder sees only SQL, and these fragments are the ones no other code writes.
    /// </summary>
    private static bool IsPayHereStatement(string commandText) =>
        commandText.Contains("DECLARE @locked int", StringComparison.Ordinal)
        || commandText.StartsWith("INSERT INTO sales.Payments (OrderId, ProviderCode,", StringComparison.Ordinal)
        || commandText.Contains("ProviderReference = ", StringComparison.Ordinal)
        || commandText.Contains("PaymentDueAtUtc = CASE", StringComparison.Ordinal);

    private void AssertNoDeadlockVictims() =>
        Assert.True(
            DeadlockVictims.Count == 0,
            $"{DeadlockVictims.Count} command(s) were chosen as deadlock victims:{Environment.NewLine}"
            + string.Join(Environment.NewLine, DeadlockVictims.Victims));

    /// <summary>
    /// One order, after a success and a failure raced: paid once, one settling payment, nothing left open,
    /// the PayHere payment ID of the <b>success</b> on the settling payment, one email, no banner.
    /// </summary>
    private async Task AssertSettledOnceAsync(PayHereOrder placed, string successId)
    {
        var order = await ReadOrderAsync(placed.PublicId);

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(OrderStockState.Committed, order.StockState);
        Assert.NotNull(order.InvoiceNumber);
        Assert.Single(order.StatusHistory, entry => entry.ToStatus == OrderStatus.Paid);

        var settling = Assert.Single(order.Payments, payment => payment.IsOrderSettlement);

        Assert.Equal(PaymentStatus.Succeeded, settling.Status);
        Assert.Equal(successId, settling.ProviderTransactionId);

        // The reference follows the money: a failure notice that raced the success must not leave the failed
        // attempt's PayHere ID on the payment staff will search for in the PayHere portal.
        Assert.Equal(successId, settling.ProviderReference);

        // Either the failure came second and changed nothing, or it came first, a fresh attempt was opened
        // for the customer, and the success then closed it (Appendix B1). Never an open payment on a paid order.
        Assert.DoesNotContain(
            order.Payments,
            payment => payment.Status is PaymentStatus.Initiated or PaymentStatus.Pending);
        Assert.InRange(order.Payments.Count, 1, 2);

        Assert.Single(await OutboxAsync(placed.PublicId, EmailTemplateCodes.PaymentConfirmed));
        Assert.Empty(await AlertsAsync(placed.PublicId));

        // Both notifications were stored and finished; neither is left for the sweep.
        var events = await EventsAsync(settling.Id);

        Assert.All(events, e => Assert.NotEqual(PaymentEventProcessingStatus.Failed, e.ProcessingStatus));
        Assert.All(events, e => Assert.NotEqual(PaymentEventProcessingStatus.Received, e.ProcessingStatus));
    }
}
