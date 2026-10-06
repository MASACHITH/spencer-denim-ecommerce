using System.Data;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales;

/// <summary>
/// Doc 08 §5.4 / doc 11 P6 step 9: the one transaction that reserves the stock, writes the order and
/// empties the bag.
/// <para>
/// <b>The reservation is a conditional UPDATE and not a read-then-write, and that is the whole defence
/// against overselling</b> (doc 08 §5.2):
/// </para>
/// <code>
/// UPDATE inventory.StockLevels
///    SET Reserved = Reserved + @q, UpdatedAtUtc = @now
///  WHERE VariantId = @v AND OnHand - Reserved &gt;= @q;   -- 0 rows ⇒ insufficient stock
/// </code>
/// <para>
/// The check and the set happen under one row lock. Two shoppers racing for the last unit do not both
/// read "1 available": the second waits for the first's lock, re-evaluates <c>OnHand - Reserved &gt;= 1</c>
/// against the row as it now is, matches nothing, and is refused. There is no window between the read and
/// the write for the race to live in, no retry storm, and no concurrency exception to handle —
/// <c>CK_StockLevels_ReservedLeOnHand</c> is the backstop that makes an oversell impossible to commit even
/// if this statement were wrong.
/// </para>
/// <para>
/// <b>Lock ordering (doc 08 §5.3).</b> The lines are reserved in ascending <c>VariantId</c> order, because
/// every other stock path — deduct on paid, release on expiry, admin bulk adjust — does the same, and two
/// paths taking the same two rows in opposite orders is a deadlock. Placement has no existing order row to
/// claim first, so stock is the first thing it touches.
/// </para>
/// <para>
/// <b>What this does not write, and why that is reported rather than invented.</b> Doc 08 §5.1 says every
/// stock change writes an <c>inventory.InventoryMovements</c> row. <b>That table does not exist</b>: doc 08
/// §3.4 attributes it to ADM-12/ADM-19 and W1-05 has not been built, so there is nothing to insert into and
/// this task does not create another task's ledger. The reservation itself is unaffected, and so is
/// reconciliation — doc 08 §5.9 compares <c>StockLevels.Reserved</c> with <c>Σ OrderLines.Quantity</c> over
/// orders in <c>StockState = 0</c>, which reads the order lines and not the ledger. What is lost is the
/// audit trail of individual movements, and it is in the task report as owed by W1-05.
/// </para>
/// </summary>
internal sealed class OrderPlacementStore(AppDbContext dbContext, ICheckoutReader reader) : IOrderPlacementStore
{

    public async Task<OrderPlacementResult> PlaceAsync(
        OrderPlacement placement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(placement);

        if (placement.Lines.Count == 0)
        {
            throw new ArgumentException("An order needs at least one line.", nameof(placement));
        }

        // Doc 08 §5.3: EF's SQL Server retry strategy already classifies 1205 (deadlock victim) as
        // transient, so a deadlocked placement re-runs the whole unit of work. Safe because every step
        // inside is conditional — a replay reserves again from the rolled-back state, and a replay after a
        // commit whose acknowledgement was lost hits UX_Orders_CheckoutIdempotencyKey and reports a
        // duplicate rather than placing a second order.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            // Doc 08 §5.3: ascending VariantId. The bag cannot hold two lines for one variant
            // (UX_CartItems_Cart_Variant), so there is nothing to merge first, but the sort is what makes
            // the lock order the same as every other stock path's.
            var ordered = placement.Lines.OrderBy(line => line.Priced.VariantId).ToList();

            var reserved = new List<int>(ordered.Count);

            foreach (var line in ordered)
            {
                var affected = await ReserveAsync(
                    line.Priced.VariantId,
                    line.Priced.Quantity,
                    placement.PlacedAtUtc,
                    cancellationToken);

                if (affected == 1)
                {
                    reserved.Add(line.Priced.VariantId);
                    continue;
                }

                // Doc 08 §5.4 step 3: any 0-row result rolls the whole thing back. Not a short order, not
                // a dropped line — the shopper is told which items are short and how many are left.
                await transaction.RollbackAsync(cancellationToken);

                // Read after the rollback, so the figures are what a shopper would see now rather than
                // what they were when the attempt began — and so our own released reservations are not
                // counted against them.
                var available = await reader.AvailableStockAsync(
                    placement.Lines.Select(candidate => candidate.Priced.VariantId).ToList(),
                    cancellationToken);

                var short_ = placement.Lines
                    .Where(candidate =>
                        available.GetValueOrDefault(candidate.Priced.VariantId) < candidate.Priced.Quantity)
                    .ToDictionary(
                        candidate => candidate.Priced.VariantId,
                        candidate => available.GetValueOrDefault(candidate.Priced.VariantId));

                // The line that failed is always reported, even if a concurrent restock has since made it
                // look fine: it is the reason the order was refused, and a shopper told "nothing is wrong"
                // after being refused would simply press the button again.
                short_.TryAdd(line.Priced.VariantId, available.GetValueOrDefault(line.Priced.VariantId));

                return OrderPlacementResult.OutOfStock(short_);
            }

            var order = BuildOrder(placement);

            dbContext.Orders.Add(order);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (SqlUniqueViolation.Matches(exception))
            {
                // Two submissions of the same checkout in flight at once.
                // UX_Orders_CheckoutIdempotencyKey rejected this one, so the reservation above is released
                // by the rollback and the caller returns the order that won. This is the race the pre-check
                // in the use case cannot see, and the index is what makes it safe rather than lucky.
                //
                // UX_Orders_OrderNumber can also land here: two placements that were handed the same number
                // would be a bug in the allocator, and reporting it as a duplicate submission would hide
                // it. It cannot happen — the allocator's single UPDATE … OUTPUT makes it impossible — and
                // if it ever did, the distinction below is what would make it visible.
                await transaction.RollbackAsync(cancellationToken);
                DetachAll();

                if (IsCheckoutKeyViolation(exception))
                {
                    return OrderPlacementResult.DuplicateSubmission;
                }

                throw;
            }
            catch
            {
                DetachAll();
                throw;
            }

            // Doc 08 §1.6 (N12): cart deletes are set-based, never a tracked Remove(). The *items* go and
            // the cart row stays, so the guest's cookie keeps working and the header badge reads 0 rather
            // than the browser being handed a fresh token it did not ask for.
            //
            // **Doc 08 and doc 11 are silent on whether placement empties the bag, so this is a decision**
            // (it is in the task report). Leaving the lines would mean a shopper returning to the bag sees
            // the items they have just bought, with the badge still counting them, and the obvious next
            // action — press Checkout again — would place a second order for the same goods. The lines are
            // not data the order needs: sales.OrderLines is a complete snapshot of them.
            await dbContext.CartItems
                .Where(item => item.CartId == placement.CartId)
                .ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            DetachAll();

            return OrderPlacementResult.Placed(order.PublicId);
        });
    }

    /// <summary>
    /// Doc 08 §5.2's statement, verbatim. Returns the row count: 1 is a reservation, 0 is insufficient
    /// stock (or no stock row at all, which is the same answer).
    /// </summary>
    private async Task<int> ReserveAsync(
        int variantId,
        short quantity,
        DateTime now,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE inventory.StockLevels
               SET Reserved = Reserved + {quantity}, UpdatedAtUtc = {now}
             WHERE VariantId = {variantId} AND OnHand - Reserved >= {quantity};
            """,
            cancellationToken);

    /// <summary>
    /// The order, its lines, its first status-history row and its payment row, as doc 08 §4.1's
    /// "— → PendingPayment" side-effect list gives them.
    /// </summary>
    private static Order BuildOrder(OrderPlacement placement)
    {
        var pricing = placement.Pricing;

        var order = new Order
        {
            OrderNumber = placement.OrderNumber,

            // Generated here rather than left to the column's `newid()` default, so the caller gets the
            // address to redirect to without a second read. A v4 GUID is 122 random bits, which is what
            // doc 08 §1.3 means by an unguessable public token — and it is the only credential a guest's
            // order link can carry.
            PublicId = Guid.NewGuid(),
            CheckoutIdempotencyKey = placement.CheckoutIdempotencyKey,
            CustomerUserId = placement.CustomerUserId,

            // Doc 08 §4.1: the one state a placed order starts in, with its stock held.
            Status = OrderStatus.PendingPayment,
            StockState = OrderStockState.Reserved,
            PaymentProviderCode = placement.PaymentProviderCode,
            Email = placement.Email,

            // Doc 08 §3.5: "the upper-cased form, indexed", because the promo per-customer limit and the
            // guest-order-linking flow both match on email and the database collation must not be the thing
            // deciding whether two addresses are the same.
            EmailNormalized = placement.Email.ToUpperInvariant(),
            Phone = placement.Phone,
            FirstName = placement.FirstName,
            LastName = placement.LastName,
            DeliveryDistrictId = placement.DeliveryDistrictId,
            DeliveryDistrictName = placement.DeliveryDistrictName,
            DeliveryZone = placement.DeliveryZone,
            ShipAddress = placement.ShipAddress,
            DeliveryMethodId = placement.DeliveryMethodId,
            PickupLocationId = placement.PickupLocationId,
            Delivery = placement.Delivery,
            Currency = SalesCurrency.Lkr,

            // Every money column from the one pricing result the use case compared against the shopper's
            // screen. CK_Orders_Totals checks four equalities between them; nothing here recomputes
            // anything, so there is no second arithmetic for the constraint to catch out.
            ItemsOriginalTotal = pricing.ItemsOriginalTotal,
            ItemsListTotal = pricing.ItemsListTotal,
            ItemsSubtotal = pricing.ItemsSubtotal,
            MarkdownDiscountTotal = pricing.MarkdownDiscountTotal,
            CampaignDiscountTotal = pricing.CampaignDiscountTotal,

            // No promo code: marketing.PromoCodes does not exist (ADM-13, W3-05). Zero is the right value
            // here rather than a placeholder — nothing was discounted.
            PromoCodeId = null,
            PromoCodeText = null,
            PromoDiscountAmount = pricing.PromoDiscountAmount,
            DeliveryFeeAmount = pricing.DeliveryFeeAmount,
            DeliveryDiscountAmount = pricing.DeliveryDiscountAmount,
            TaxAmount = pricing.TaxAmount,
            GrandTotal = pricing.GrandTotal,
            PlacedAtUtc = placement.PlacedAtUtc,
            PaymentDueAtUtc = placement.PaymentDueAtUtc,
            CustomerNote = placement.CustomerNote,
            PlacedIpAddress = placement.PlacedIpAddress,
        };

        foreach (var line in placement.Lines)
        {
            order.Lines.Add(new OrderLine
            {
                LineNumber = line.LineNumber,
                ProductId = line.ProductId,
                VariantId = line.VariantId,
                DepartmentId = line.DepartmentId,
                CategoryId = line.CategoryId,
                ProductName = line.ProductName,
                ProductCode = line.ProductCode,
                Sku = line.Sku,
                ColourName = line.ColourName,
                SizeLabel = line.SizeLabel,
                ThumbnailStorageKey = line.ThumbnailStorageKey,
                Quantity = line.Priced.Quantity,
                UnitOriginalPrice = line.Priced.UnitOriginalPrice,
                UnitListPrice = line.Priced.UnitListPrice,
                UnitPrice = line.Priced.UnitPrice,

                // Sale campaigns are ADM-14 (W3-05); with no campaign table nothing moved the price, so
                // there is no campaign to attribute the line to.
                SaleCampaignId = null,
                LineSubtotal = line.Priced.LineSubtotal,
                PromoDiscountAllocated = line.Priced.PromoDiscountAllocated,
                LineTotal = line.Priced.LineTotal,
            });
        }

        order.StatusHistory.Add(new OrderStatusHistoryEntry
        {
            // Null, not PendingPayment: doc 08 §3.5 makes FromStatus nullable precisely so that the first
            // row can say the order came from nowhere. Writing the same status in both columns would read
            // as a transition that did not happen.
            FromStatus = null,
            ToStatus = OrderStatus.PendingPayment,

            // Doc 11 P6: "History row, source Customer." A guest is still the customer; `ChangedByUserId`
            // stays null because there is no account behind it.
            Source = OrderStatusChangeSource.Customer,
            ChangedByUserId = placement.CustomerUserId,
            OccurredAtUtc = placement.PlacedAtUtc,
        });

        order.Payments.Add(BuildPayment(placement));

        return order;
    }

    /// <summary>
    /// The open payment row doc 08 §4.1 lists as a placement side effect: bank transfer starts
    /// <c>Pending</c>.
    /// </summary>
    /// <remarks>
    /// <b>This is the boundary with W3-03's steps 2 and 3, and it is deliberately exactly one row.</b>
    /// Doc 11 P7's preconditions are "order PendingPayment with an open bank payment (Pending)", so an
    /// order placed without this row would be one the bank-transfer flow could not pick up — and
    /// <c>UX_Payments_OneOpen</c> means the flow cannot create it later without racing itself. Everything
    /// *after* this row is the next task: no bank details, no reference to quote, no slip upload, no
    /// approval, no rejection, no expiry.
    /// <para>
    /// It is also the only provider-specific line in the placement path, which is where PAY-03's
    /// <c>IPaymentProvider</c> will replace it: the initial status and the due window are the two things a
    /// provider decides. PayPal's branch (<c>Initiated</c>, a USD conversion, a redirect) is W4-01's, and
    /// the use case refuses any provider but bank transfer rather than guessing at it.
    /// </para>
    /// </remarks>
    private static Payment BuildPayment(OrderPlacement placement) =>
        new()
        {
            ProviderCode = placement.PaymentProviderCode,
            Status = PaymentStatus.Pending,

            // 0 until something actually settles the order. CK_Payments_IsOrderSettlement allows 1 only
            // for statuses 3, 8 and 9, so a Pending row could not claim it anyway.
            IsOrderSettlement = false,
            Amount = placement.Pricing.GrandTotal,
            Currency = SalesCurrency.Lkr,
            CreatedAtUtc = placement.PlacedAtUtc,
        };

    /// <summary>
    /// Whether the violation was the checkout key's index specifically, rather than any unique index on
    /// the order.
    /// </summary>
    /// <remarks>
    /// The index name is matched in the message because SQL Server reports 2601/2627 with the constraint or
    /// index name and no structured field for it. That is a string match on a server message, which is
    /// fragile — so it fails <i>closed</i>: anything it does not recognise is rethrown rather than being
    /// reported as a harmless duplicate submission. A missed duplicate surfaces as a 500 the tester can
    /// see; a misclassified one would tell a shopper their order was already placed when it was not.
    /// </remarks>
    private static bool IsCheckoutKeyViolation(Exception exception) =>
        Messages(exception).Any(message =>
            message.Contains("UX_Orders_CheckoutIdempotencyKey", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Messages(Exception? exception)
    {
        while (exception is not null)
        {
            yield return exception.Message;
            exception = exception.InnerException;
        }
    }


    /// <summary>
    /// Detaches the order graph after the transaction, successful or not.
    /// <para>
    /// After a failure it is what stops the execution strategy's retry from re-sending the same INSERTs on
    /// top of a rolled-back transaction (the trap <c>CartStore.InsertCartAsync</c> records). After a
    /// success it matters for a different reason: the context is scoped to the request, the API re-reads the
    /// order to build the confirmation, and a tracked graph would let that read answer from the change
    /// tracker instead of the database — which would make the success page a copy of what we meant to write
    /// rather than a reading of what was written.
    /// </para>
    /// </summary>
    private void DetachAll() => dbContext.ChangeTracker.Clear();
}
