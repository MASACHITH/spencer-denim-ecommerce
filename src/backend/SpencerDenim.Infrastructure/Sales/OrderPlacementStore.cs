using System.Data;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Ops.Email.Contracts;
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
/// <para>
/// <b>It does now write the emails</b> (W3-06, doc 11 P6 step 9's "and the queued emails"):
/// <c>OrderPlaced</c> for every order, and the payment instructions when the provider has any to give.
/// Both are rows in <c>ops.EmailOutbox</c> added to this same unit of work, so they commit with the order
/// or not at all — see <see cref="QueueEmails"/>, which also records why an unset bank account queues no
/// instructions email.
/// </para>
/// </summary>
internal sealed class OrderPlacementStore(
    AppDbContext dbContext,
    ICheckoutReader reader,
    IOrderEmailOutbox emails) : IOrderPlacementStore
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

            // Doc 11 P6 step 9 sub-step 2, between the stock and the order: "takes a promo use (total
            // limit, then per-customer limit for this email)". Doc 08 §5.3's lock order puts it here —
            // after the stock rows, before anything else.
            //
            // **The refusal rolls the whole transaction back**, so the reservation above goes with it and
            // no order exists. It is not placed at the undiscounted total: the shopper agreed to a figure
            // that included the code, and charging more would be G-10 with extra steps. Doc 11 P3's own
            // exception says the loser "can place the order without it" — by pressing the button again,
            // having seen the new total.
            if (placement.Promo is { } promo)
            {
                var refusal = await OrderPromoUse.TakeAsync(
                    dbContext,
                    promo,
                    order.EmailNormalized,
                    cancellationToken);

                if (refusal is { } refused)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    DetachAll();

                    return OrderPlacementResult.PromoRejected(refused);
                }
            }

            dbContext.Orders.Add(order);

            // Doc 11 P6 step 9: the transaction saves "the order, the order lines …, the payment record,
            // the status history (source Customer) **and the queued emails**". Enlisted here and saved by
            // the SaveChangesAsync below, so the messages and the order commit together — the whole point
            // of the outbox pattern (doc 08 §3.8). A rollback takes the messages with it, and
            // `DetachAll()` on every exit stops a retried attempt re-adding them.
            //
            // **Deliberately after `Orders.Add` and before the save**, not after the commit: a shopper
            // whose order committed and whose confirmation was queued "just afterwards" by a process that
            // then died would have an order nobody ever told them about, which is exactly what this
            // pattern exists to make impossible.
            QueueEmails(placement, order);

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
    /// Doc 11 P6 step 10's two emails: <c>OrderPlaced</c> always, and the payment instructions when the
    /// provider had any to give.
    /// </summary>
    /// <remarks>
    /// <b>The second email is queued only when <c>placement.Instructions</c> is non-null, and that is the
    /// decision this method exists to make visible.</b> For a bank transfer the provider answers null
    /// exactly when the shop's <c>Bank.*</c> settings are unset (<c>BankTransferProvider.DescribeAsync</c>),
    /// which is the state this shop is in until the client sends their account details. An email headed
    /// "How to pay" with an empty account number is not a degraded email, it is a wrong one — a customer
    /// could transfer money nowhere — so nothing is queued and the shopper has the order link, where the
    /// page already says the details are being confirmed (W3-03). It is the same rule W3-04's invoice
    /// follows for the store's unset legal details: print nothing rather than something plausible.
    /// <para>
    /// <b>Doc 11 P6 step 10 also makes this per payment method</b> — "Bank transfer: … Sends Order placed
    /// and Bank transfer instructions. PayPal: … Sends Order placed" — and the condition above expresses
    /// that without naming a gateway: a provider that redirects rather than giving out account details
    /// has no instructions to post, so PayPal will answer with a <c>RedirectUrl</c> that belongs on a page
    /// and not in an email. <b>That is the one thing W4-01 must check here</b> rather than inherit: today
    /// null-or-not is a faithful reading of P6 step 10, and the day a second provider returns non-null
    /// instructions it will need a rule about which of them are worth emailing.
    /// </para>
    /// </remarks>
    private void QueueEmails(OrderPlacement placement, Order order)
    {
        var header = new OrderEmailHeader(
            placement.OrderNumber,
            order.PublicId,
            new OrderEmailAddressee(placement.Email, placement.FirstName, placement.LastName),
            placement.PlacedAtUtc);

        emails.EnlistOrderPlaced(new PlacedOrderEmailFacts(
            header,

            // The order's own line order (1-based, oldest first), not the variant-sorted copy the
            // reservation loop made: the email reads like the bag the shopper filled.
            placement.Lines
                .OrderBy(line => line.LineNumber)
                .Select(line => new OrderEmailLine(
                    line.ProductName,
                    line.ColourName,
                    line.SizeLabel,
                    line.Priced.Quantity,
                    line.Priced.LineTotal))
                .ToList(),
            new OrderEmailTotals(
                placement.Pricing.ItemsSubtotal,
                placement.Pricing.PromoDiscountAmount,
                placement.Pricing.DeliveryFeeAmount,
                placement.Pricing.DeliveryDiscountAmount,
                placement.Pricing.GrandTotal),
            new OrderEmailDelivery(
                placement.Delivery.DeliveryMethodName,
                placement.Delivery.DeliveryMethodCode == DeliveryMethod.PickupCode,
                placement.Delivery.PickupLocationName,
                AddressLines(placement.ShipAddress),
                placement.DeliveryDistrictName),
            placement.PaymentMethodName,
            placement.Payment.PaymentDueAtUtc));

        if (placement.Instructions is { } instructions)
        {
            emails.EnlistPaymentInstructions(header, instructions);
        }
    }

    /// <summary>
    /// The delivery address as lines, skipping every part the shopper did not give.
    /// </summary>
    /// <remarks>
    /// Empty for a pickup order, whose <c>Ship_*</c> columns are all null by design
    /// (<c>CK_Orders_ShipAddress</c>, and <see cref="BuildOrder"/>'s own note about not recording a parcel
    /// that is not being shipped). The phone number is <b>not</b> included: it is on the order for the
    /// courier and for staff, and printing it back to the customer in an email adds nothing while putting
    /// one more piece of personal data into a table that is backed up nightly.
    /// </remarks>
    private static List<string> AddressLines(AddressSnapshot address) =>
        new[]
        {
            address.RecipientName,
            address.Line1,
            address.Line2,
            address.City,
            address.PostalCode,
            address.Landmark,
        }
        .Where(part => !string.IsNullOrWhiteSpace(part))
        .Select(part => part!.Trim())
        .ToList();

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

            // ADM-13. Both null for an order placed without a code, which is most of them, and the
            // `PromoDiscountAmount` below is then zero because `OrderPricing` was given no charge — not
            // because anything here defaulted it. `PromoCodeText` is the stored, upper-cased code rather
            // than what the shopper typed: it is the snapshot the invoice and every order screen read, and
            // it has to be the code the shop issued (doc 08 §3.6 DB19).
            PromoCodeId = placement.Promo?.PromoCodeId,
            PromoCodeText = placement.Promo?.Code,
            PromoDiscountAmount = pricing.PromoDiscountAmount,
            DeliveryFeeAmount = pricing.DeliveryFeeAmount,
            DeliveryDiscountAmount = pricing.DeliveryDiscountAmount,
            TaxAmount = pricing.TaxAmount,
            GrandTotal = pricing.GrandTotal,
            PlacedAtUtc = placement.PlacedAtUtc,
            PaymentDueAtUtc = placement.Payment.PaymentDueAtUtc,
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

                // Which campaign priced the line (ADM-14), resolved live by `CheckoutReader` in the
                // repricing read this placement is built from — not re-resolved here, because doc 08 §6
                // says the order's prices are "copied into the order at placement and never recomputed",
                // and a second resolution could pick a different campaign if a boundary fell between the
                // two reads. Null when no campaign won.
                SaleCampaignId = line.Priced.SaleCampaignId,
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
    /// The open payment row doc 08 §4.1 lists as a placement side effect, in the state the chosen
    /// <c>IPaymentProvider</c> asked for.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately exactly one row.</b> Doc 11 P7's preconditions are "order PendingPayment with an
    /// open bank payment (Pending)", so an order placed without this row would be one the bank-transfer
    /// flow could not pick up — and <c>UX_Payments_OneOpen</c> means the flow cannot create it later
    /// without racing itself.
    /// <para>
    /// <b>Nothing here is provider-specific any more (PAY-03).</b> It was: the status was the literal
    /// <c>Pending</c> and the due window was a bank-transfer constant, with a note saying
    /// <c>IPaymentProvider</c> would replace them. It has. Every value a provider decides arrives on
    /// <see cref="OrderPlacement.Payment"/>, so PayPal's <c>Initiated</c> row with a USD
    /// <c>ProviderAmount</c> at a frozen rate is written by this same code — which is what "the next
    /// gateway plugs in without touching checkout" has to mean to be worth claiming.
    /// </para>
    /// <para>
    /// <b><c>Amount</c> and <c>Currency</c> are <i>not</i> the provider's to decide</b>, and that is the
    /// line between the two. Doc 08 §3.5 defines <c>Amount</c> as the order's <c>GrandTotal</c> in LKR
    /// outright; a provider that could change it could take a different sum from the one
    /// <c>CK_Orders_Totals</c> checked and the shopper agreed to (G-10). What a provider may add is a
    /// <i>second</i> figure in its own currency — <c>ProviderAmount</c> / <c>ProviderCurrency</c> — which
    /// is additional information about the same debt rather than a replacement for it.
    /// </para>
    /// </remarks>
    private static Payment BuildPayment(OrderPlacement placement) =>
        new()
        {
            ProviderCode = placement.PaymentProviderCode,
            Status = placement.Payment.Status,

            // 0 until something actually settles the order. CK_Payments_IsOrderSettlement allows 1 only
            // for statuses 3, 8 and 9, so neither an Initiated nor a Pending row could claim it anyway.
            IsOrderSettlement = false,
            Amount = placement.Pricing.GrandTotal,
            Currency = SalesCurrency.Lkr,
            ProviderCurrency = placement.Payment.ProviderCurrency,
            ProviderAmount = placement.Payment.ProviderAmount,
            ExchangeRate = placement.Payment.ExchangeRate,
            FxBufferPercent = placement.Payment.FxBufferPercent,
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
