using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Ops.Email.Contracts;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// The Paid effects of doc 08 §4.1 (and the stock rules of §5.5 and §5.7), written once for every path that
/// pays an order: a staff member's Approve and Accept-late (<see cref="BankTransferReviewStore"/>) and a
/// provider's confirmed payment (<c>OrderSettlementStore</c>).
/// <para>
/// <b>Every statement here was moved out of <c>BankTransferReviewStore</c> unchanged</b> — same SQL, same
/// guards, same order of columns, same comments — and what changed is only where the values come from: the
/// order id and the instant are parameters instead of fields of a <c>BankTransferDecision</c>, so a caller
/// with no staff member behind it can use them. Nothing here opens a transaction. <b>The caller owns the
/// transaction</b> and the lock order (doc 08 §5.3, §4.2 N6: the order row, then the payment row, then the
/// stock rows in ascending <c>VariantId</c>, then the number series — the order these methods are meant to
/// be called in), which is why this is a class used by two stores and not a port of its own: a port would
/// invite a caller to wrap each method in a transaction of its own and break exactly the atomicity the
/// callers exist to provide.
/// </para>
/// <para>
/// <b>What is not written, and why it is not quietly skipped.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>No <c>inventory.InventoryMovements</c> row.</b> The table does not exist (#108, W1-05/ADM-12), so
/// doc 08 §5.1's <c>Sale</c> movement cannot be written for a deduction and neither could checkout's
/// <c>Reserve</c>. The stock figures themselves are exact and doc 08 §5.9's reconciliation is unaffected
/// (it compares <c>StockLevels.Reserved</c> with the order lines of orders in <c>StockState = 0</c>, and
/// a paid order leaves that set). What is lost is the per-movement trail: after a payment there is
/// no row anywhere saying "two of variant 470 moved from reserved to sold at 14:03". The order's status
/// history and <c>PaidAtUtc</c> record that the order was paid, and the quantities are on the order lines,
/// so the fact is reconstructible for an order — it is the <i>variant's</i> timeline that has the hole.
/// </description></item>
/// <item><description>
/// <b>No promo use restored on a late acceptance.</b> Doc 08 §4.1 restores <c>UsageCount + 1</c> ignoring
/// the limits. <c>marketing.PromoCodes</c> does not exist (ADM-13 / W3-05) and <c>Orders.PromoCodeId</c> is
/// therefore null on every order ever placed, so there is nothing to restore and no branch that could run.
/// </description></item>
/// <item><description>
/// <b>No <c>InvoiceSellerSnapshotJson</c>.</b> Doc 08 §4.1 wants the store's legal name, address and
/// BR/VAT number frozen on to the order at issue. Those are the <c>Store.LegalName</c>,
/// <c>Store.Address</c>, <c>Store.BRNumber</c> and <c>Store.VatNumber</c> settings, and <b>this build
/// seeds none of them</b> — the client has not supplied them and inventing a registration number would be
/// worse than leaving the column null. The invoice <i>number</i>, which is what doc 02 PAY-01 and doc 11
/// P7 name, is issued and gapless. Nothing renders an invoice document yet (G-34), so the snapshot has no
/// reader to disappoint; it is owed to ADM-22 together with the four keys.
/// </description></item>
/// </list>
/// </summary>
internal sealed class OrderSettlementEffects(
    AppDbContext dbContext,
    INumberSeriesAllocator numbers,
    IOrderEmailOutbox emails)
{
    /// <summary>
    /// Doc 08 §4.1 "PendingPayment -> Paid": the claim on the order row. Returns the number of rows it
    /// affected — 1 if this caller won, 0 if the order was not in the state the claim needs.
    /// </summary>
    /// <param name="orderId">The order.</param>
    /// <param name="paymentProviderCode">
    /// The provider the order must belong to. Pinned in the statement, not only in the caller's read — see
    /// the comment on the statement.
    /// </param>
    /// <param name="nowUtc">The instant of the transaction.</param>
    /// <param name="updatedByUserId">The staff member, or null when the System pays it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<int> ClaimPendingOrderPaidAsync(
        int orderId,
        string paymentProviderCode,
        DateTime nowUtc,
        int? updatedByUserId,
        CancellationToken cancellationToken) =>
        // Doc 08 §4.1 "PendingPayment -> Paid": Status = 0 AND StockState = 0, plus §4.2 step
        // 3.2's NOT EXISTS so a second settlement is impossible even if the payment update below
        // were wrong.
        //
        // **PaymentProviderCode is in the claim as well as in ReadOrderAsync, and that is not
        // belt and braces — it is the lesson from a defect this task's own test found.** The
        // first version relied on the read alone, the read's projection could not express "no
        // such row" (see OrderSnapshot), and a PayPal order was approved and paid by the bank
        // flow. A guard that lives in the atomic statement cannot be defeated by a bug in a
        // read.
        //
        // **The literal 1 in the NOT EXISTS stays a literal.** PaymentConfiguration's remarks
        // record the measurement: "IsOrderSettlement = 1" implies UX_Payments_OneSettlement's own
        // filter, so DB28 lets the optimiser seek that index; "IsOrderSettlement = @flag" does not
        // imply it and falls back to a clustered scan of sales.Payments. Interpolating a C# value
        // here would make it a parameter and undo that.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Orders
               SET Status = {(byte)OrderStatus.Paid},
                   StockState = {(byte)OrderStockState.Committed},
                   PaidAtUtc = {nowUtc},
                   ClosedAtUtc = NULL,
                   CaptureLockedUntilUtc = NULL,
                   UpdatedAtUtc = {nowUtc},
                   UpdatedByUserId = {updatedByUserId}
             WHERE Id = {orderId}
               AND PaymentProviderCode = {paymentProviderCode}
               AND Status = {(byte)OrderStatus.PendingPayment}
               AND StockState = {(byte)OrderStockState.Reserved}
               AND NOT EXISTS (
                     SELECT 1 FROM sales.Payments s
                      WHERE s.OrderId = {orderId} AND s.IsOrderSettlement = 1);
            """,
            cancellationToken);

    /// <summary>
    /// Doc 08 §5.5 step 2: deduct every line under the order's claim, sorted by <c>VariantId</c>.
    /// </summary>
    /// <remarks>
    /// <b>A row count other than 1 throws, and that is doc 08 §5.5's instruction rather than this code's
    /// choice</b> ("otherwise throw, roll back, alert; it indicates drift"). It is not a refusal a staff
    /// member can act on: the order's reservation was made at placement and the guard
    /// <c>Reserved &gt;= q AND OnHand &gt;= q</c> can only fail if the reservation has been lost, which
    /// means <c>StockLevels</c> and the order lines disagree. Telling the approver "out of stock" would be
    /// wrong — the stock was theirs — and silently approving would commit an oversell.
    /// <para>
    /// <b>No alert row</b>, because <c>ops.AdminAlerts</c> did not exist when this was written. The exception
    /// names the variant and reaches the log through the endpoint's 500, and doc 08 §5.9's nightly
    /// reconciliation is the other half of the signal. (The table exists now, and a <c>ReconciliationDrift</c>
    /// alert is that nightly job's to raise; a payment path that throws is already loud.)
    /// </para>
    /// <para>
    /// Quantities are summed per variant before the loop. Two lines for one variant cannot happen today
    /// (<c>UX_CartItems_Cart_Variant</c> stops the bag holding two), but doc 08 §5.3 asks for duplicates
    /// to be merged first and one statement per variant is also what makes the row count mean what it says.
    /// </para>
    /// </remarks>
    public async Task DeductStockAsync(int orderId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        foreach (var line in await ReadLinesAsync(orderId, cancellationToken))
        {
            var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE inventory.StockLevels
                   SET OnHand = OnHand - {line.Quantity},
                       Reserved = Reserved - {line.Quantity},
                       UpdatedAtUtc = {nowUtc}
                 WHERE VariantId = {line.VariantId}
                   AND Reserved >= {line.Quantity}
                   AND OnHand >= {line.Quantity};
                """,
                cancellationToken);

            if (affected != 1)
            {
                throw new InvalidOperationException(
                    $"Order {orderId} could not deduct {line.Quantity} of variant "
                    + $"{line.VariantId} on approval: the reservation made at placement is no longer there. "
                    + "Stock levels and the order lines have drifted (doc 08 §5.5, §5.9).");
            }
        }
    }

    /// <summary>
    /// Doc 08 §5.7, the late-acceptance half: take the goods from <c>OnHand</c> again, competing with live
    /// shoppers, and refuse the whole acceptance naming <b>every</b> short line.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when every line was re-acquired, or every short line to answer with.
    /// </returns>
    /// <remarks>
    /// <b>Every short line, not the first.</b> Doc 11 P7 step 8 says acceptance "is refused with the list
    /// of short lines", and a staff member told about one of three missing items will press the button
    /// again and be told about the next.
    /// <para>
    /// <b>The figures are corrected for this attempt's own deductions, which is why no rollback is needed
    /// to measure them.</b> <c>OrderPlacementStore</c> reads availability <i>after</i> rolling back, for
    /// exactly this reason: a transaction sees its own uncommitted writes, so a straight read here would
    /// report every already-deducted line as shorter than it is and could invent a shortfall that does not
    /// exist. Rolling back from inside is not an option — the caller's transaction owns its end — so the
    /// arithmetic is done instead: each line we have already deducted gets its quantity added back, which
    /// reproduces the figure a shopper would see. It is exact rather than approximate, because the only
    /// thing this attempt did to <c>Available</c> was subtract those quantities from <c>OnHand</c>.
    /// </para>
    /// <para>
    /// <b>No <c>Reserved</c> anywhere.</b> The reservation was given back when the order expired or was
    /// cancelled, so a late acceptance takes from <c>OnHand</c> against what is available <i>now</i> —
    /// which is doc 08 §5.7's statement and the reason this can fail at all.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ShortStockLine>?> ReacquireStockAsync(
        int orderId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var lines = await ReadLinesAsync(orderId, cancellationToken);
        var deducted = new Dictionary<int, int>();

        foreach (var line in lines)
        {
            var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE inventory.StockLevels
                   SET OnHand = OnHand - {line.Quantity},
                       UpdatedAtUtc = {nowUtc}
                 WHERE VariantId = {line.VariantId}
                   AND OnHand - Reserved >= {line.Quantity};
                """,
                cancellationToken);

            if (affected == 1)
            {
                deducted[line.VariantId] = line.Quantity;
                continue;
            }

            var variantIds = lines.Select(candidate => candidate.VariantId).ToList();

            var rows = await dbContext.StockLevels
                .AsNoTracking()
                .Where(level => variantIds.Contains(level.VariantId))
                .Select(level => new { level.VariantId, level.Available })
                .ToListAsync(cancellationToken);

            var available = rows.ToDictionary(
                row => row.VariantId,

                // Add back what this attempt took, so the figure is what the line could have had. Floored
                // at zero because a drifted row could read negative and a negative "available" is not a
                // fact anybody can act on.
                row => Math.Max(0, row.Available + deducted.GetValueOrDefault(row.VariantId)));

            return lines
                .Where(candidate =>
                    candidate.VariantId == line.VariantId

                    // The line that actually failed is always reported, even if a concurrent restock has
                    // since made it look sufficient: it is the reason the acceptance was refused, and a
                    // staff member told "nothing is wrong" after a refusal will simply press again.
                    || available.GetValueOrDefault(candidate.VariantId) < candidate.Quantity)
                .Select(candidate => new ShortStockLine(
                    candidate.VariantId,
                    candidate.Sku,
                    candidate.ProductName,
                    candidate.ColourName,
                    candidate.SizeLabel,
                    candidate.Quantity,
                    available.GetValueOrDefault(candidate.VariantId)))
                .ToList();
        }

        return null;
    }

    /// <summary>
    /// A gapless invoice number, allocated <b>inside</b> the caller's transaction, and written on to the
    /// order.
    /// </summary>
    /// <remarks>
    /// <b>This is the opposite trade to the order number, and the shared primitive is what makes it
    /// cheap.</b> <c>INumberSeriesAllocator.AllocateAsync</c> enlists in the ambient transaction, so the
    /// series row stays locked until this decision commits and a decision that rolls back gives its number
    /// back — which is what "gapless" means. Checkout's <c>AllocateOrderNumberAsync</c> deliberately
    /// throws if a transaction is already open and must not be used here; order numbers are allowed gaps
    /// precisely so that concurrent checkouts do not queue on the series row, and an invoice series is
    /// allowed to queue because approvals are a staff member clicking a button.
    /// <para>
    /// It is allocated <i>after</i> the order claim rather than before, so a decision that lost the race
    /// never touches the series at all. Either order is gapless — a rollback restores <c>NextValue</c>
    /// — but holding that row for a transaction we already know will fail is pointless contention.
    /// </para>
    /// <para>
    /// The year is the <b>Colombo</b> year (doc 08 §3.5), the same rule as the order series, so a payment
    /// approved at 03:00 UTC on 1 January belongs to the year the shop was in.
    /// </para>
    /// </remarks>
    public async Task<string> IssueInvoiceAsync(
        int orderId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var year = ColomboTime.YearOf(new DateTimeOffset(nowUtc, TimeSpan.Zero));

        var sequence = await numbers.AllocateAsync(
            DocumentNumber.InvoiceSeriesKey(year),
            DocumentNumber.InvoiceNumberPrefix,
            cancellationToken);

        var invoiceNumber = DocumentNumber.Format(DocumentNumber.InvoiceNumberPrefix, year, sequence);

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Orders
               SET InvoiceNumber = {invoiceNumber},
                   InvoiceIssuedAtUtc = {nowUtc}
             WHERE Id = {orderId};
            """,
            cancellationToken);

        return invoiceNumber;
    }

    /// <summary>
    /// The order's history row (doc 08 §3.5), added through EF so it joins the same unit of work as the
    /// audit row — and, for a System payment, the same <c>SaveChangesAsync</c> as the email.
    /// </summary>
    /// <param name="orderId">The order.</param>
    /// <param name="from">The status it came from.</param>
    /// <param name="to">The status it went to.</param>
    /// <param name="source">
    /// Who is recorded as having done it. A staff decision is <see cref="OrderStatusChangeSource.Staff"/>
    /// (doc 11 P7 step 6: not <c>SystemJob</c>, which is the expiry job's); a signature-verified provider
    /// event is <see cref="OrderStatusChangeSource.PaymentProvider"/>, which doc 08 §4 reserves for
    /// exactly that.
    /// </param>
    /// <param name="changedByUserId">The staff member, or null when nobody acted.</param>
    /// <param name="note">Already clipped to the column by the caller.</param>
    /// <param name="nowUtc">The instant of the transaction.</param>
    public void RecordStatusChange(
        int orderId,
        OrderStatus from,
        OrderStatus to,
        OrderStatusChangeSource source,
        int? changedByUserId,
        string note,
        DateTime nowUtc) =>
        dbContext.OrderStatusHistory.Add(new OrderStatusHistoryEntry
        {
            OrderId = orderId,
            FromStatus = from,
            ToStatus = to,
            Source = source,
            ChangedByUserId = changedByUserId,
            Note = note,
            OccurredAtUtc = nowUtc,
        });

    /// <summary>
    /// Doc 11 §4.1's <c>PaymentConfirmed</c>: "Payment received, amount, invoice number, what happens
    /// next".
    /// </summary>
    /// <remarks>
    /// <b>The amount is the order's <c>GrandTotal</c>, not the payment row's.</b> They are the same
    /// figure by construction — doc 08 §3.5 defines <c>Payments.Amount</c> as the order's grand total in
    /// LKR, and <c>OrderPlacementStore</c> writes it from the one pricing result <c>CK_Orders_Totals</c>
    /// checked — and the order is the record of what the customer agreed to, which is what a receipt
    /// should repeat back. (There is no USD line: a converted payment's provider amount is the payment's
    /// business, and the receipt is in the shop's currency.)
    /// </remarks>
    public void EnlistPaymentConfirmed(
        OrderSnapshot snapshot,
        string invoiceNumber,
        DateTime nowUtc,
        bool wasLatePayment) =>
        emails.EnlistPaymentConfirmed(new PaymentConfirmedEmailFacts(
            new OrderEmailHeader(
                snapshot.OrderNumber,
                snapshot.PublicId,
                new OrderEmailAddressee(snapshot.Email, snapshot.FirstName, snapshot.LastName),
                nowUtc),
            snapshot.GrandTotal,
            invoiceNumber,
            wasLatePayment));

    /// <summary>
    /// Doc 08 Appendix B1: when a payment pays the order, any <b>other</b> attempt still open
    /// (Initiated or Pending) is closed in the same transaction — <c>Cancelled</c>, the status doc 08 §4.3
    /// gives an abandoned attempt. **Run on every settlement, unconditionally.**
    /// </summary>
    /// <remarks>
    /// "Newer" in the note and "other" here are the same set: an open attempt older than the paying one
    /// cannot exist, because the paying one would have been blocked from opening beside it.
    /// <para>
    /// <b>It is not gated on the paying payment's status, and the first fix gated it.</b> That version skipped
    /// the statement for a payment read as Initiated or Pending (<c>UX_Payments_OneOpen</c> allows only one open
    /// row, so such a payment has nothing beside it) — but the status was read <i>before</i> the order claim,
    /// with no lock, and a retry flow locks the order first. Measured by the tester: payment A read as
    /// Initiated, the settlement blocked on the order row, the other transaction cancelled A and opened a newer
    /// attempt B and committed, and the settlement then paid A and left B Initiated. A read taken before the
    /// lock is not a fact about the state the lock protects; the hint below is what makes the statement cheap
    /// and safe, so it simply runs.
    /// </para>
    /// <para>
    /// Statuses 0 and 1 only — <c>Capturing</c> (2) is deliberately left alone. A capture in flight is
    /// money the shop does not yet know about, and cancelling its row would hide it; it resolves through
    /// its own apply, and becomes an exception payment when it finds the order paid.
    /// </para>
    /// <para>
    /// <b>The index hint is not tuning, it is what makes this statement safe to run while this transaction
    /// holds a row lock — a deadlock the tester measured.</b> This transaction has already updated its own
    /// payment row, so it holds an exclusive lock on it. Without the hint the optimiser, on a small table,
    /// answers <c>OrderId = @o AND Id &lt;&gt; @p</c> with a range seek on the clustered key
    /// (<c>Id &lt; @p OR Id &gt; @p</c>) — in effect a scan of <c>sales.Payments</c> — and two settlements of
    /// <i>different</i> orders each scan into the row the other has locked. Measured: two transactions that
    /// each update their own payment and then run this statement deadlocked 40 times in 40 without the
    /// hint and 0 in 40 with it; twelve settlements in parallel produced 16 to 31 deadlocks per run and one
    /// call that ran out of its three retries. <c>IX_Payments_OrderId</c> confines the statement to the rows
    /// of its own order, whose lock order the order claim already serialises. (Doc 08 §1.6 DB7 is why the
    /// index exists: its filtered siblings are not matched by <c>OrderId = @o</c> alone.)
    /// </para>
    /// </remarks>
    public async Task<int> CloseOtherOpenAttemptsAsync(
        int orderId,
        int exceptPaymentId,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE p
               SET Status = {(byte)PaymentStatus.Cancelled},
                   CompletedAtUtc = {nowUtc},
                   UpdatedAtUtc = {nowUtc}
              FROM sales.Payments AS p WITH (INDEX(IX_Payments_OrderId))
             WHERE p.OrderId = {orderId}
               AND p.Id <> {exceptPaymentId}
               AND p.Status IN ({(byte)PaymentStatus.Initiated}, {(byte)PaymentStatus.Pending});
            """,
            cancellationToken);

    /// <summary>
    /// The order's lines, merged per variant and sorted ascending — doc 08 §5.3's lock order, which every
    /// stock path in the system shares so that two of them cannot deadlock.
    /// </summary>
    private async Task<IReadOnlyList<StockLine>> ReadLinesAsync(
        int orderId,
        CancellationToken cancellationToken)
    {
        var lines = await dbContext.OrderLines
            .AsNoTracking()
            .Where(line => line.OrderId == orderId)
            .Select(line => new
            {
                line.VariantId,
                line.Quantity,
                line.Sku,
                line.ProductName,
                line.ColourName,
                line.SizeLabel,
            })
            .ToListAsync(cancellationToken);

        // Grouped by variant, because two lines of one order can name the same variant and the stock
        // statements have to move its row once. The description comes from the first of them: they are
        // placement-time snapshots of the same variant, so they agree.
        return lines
            .GroupBy(line => line.VariantId)
            .Select(group => new StockLine(
                group.Key,
                group.Sum(line => line.Quantity),
                group.First().Sku,
                group.First().ProductName,
                group.First().ColourName,
                group.First().SizeLabel))
            .OrderBy(line => line.VariantId)
            .ToList();
    }

    private readonly record struct StockLine(
        int VariantId,
        int Quantity,
        string Sku,
        string ProductName,
        string ColourName,
        string SizeLabel);
}
