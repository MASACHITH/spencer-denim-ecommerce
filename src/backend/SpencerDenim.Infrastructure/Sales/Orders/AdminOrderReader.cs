using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Orders;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Orders.Contracts;
using SpencerDenim.Domain.Media;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Orders;

/// <summary>
/// Every read the admin orders screen needs (ADM-09, doc 11 P9).
/// <para>
/// <b>Which index each query uses is written on the query</b>, because doc 08 §7 and DB28 both turn on it
/// and because #103's review flagged the filtered indexes as the ones most likely to be silently unmatched
/// once a screen was written. <c>AdminOrderQueryPlanTests</c> asserts the two that matter.
/// </para>
/// </summary>
internal sealed class AdminOrderReader(AppDbContext dbContext, IClock clock) : IAdminOrderReader
{
    public async Task<AdminOrderListPage> ReadListAsync(
        string? search,
        OrderStatus? status,
        AdminOrderSort sort,
        int page,
        int pageSize,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var term = AdminOrderSearch.Classify(search);

        var matching = Filter(dbContext.Orders.AsNoTracking(), term, status);

        var total = await matching.CountAsync(cancellationToken);

        // Sorted on PlacedAtUtc, which is the key of both IX_Orders_Status_Placed (when a status is
        // filtered) and IX_Orders_Placed (when one is not). Id breaks the tie so paging is stable: two
        // orders placed in the same millisecond would otherwise be able to swap pages between requests and
        // one of them would be invisible.
        var ordered = sort == AdminOrderSort.Oldest
            ? matching.OrderBy(order => order.PlacedAtUtc).ThenBy(order => order.Id)
            : matching.OrderByDescending(order => order.PlacedAtUtc).ThenByDescending(order => order.Id);

        var rows = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(order => new
            {
                order.PublicId,
                order.OrderNumber,
                order.Status,
                order.FirstName,
                order.LastName,
                order.Email,
                order.GrandTotal,
                order.Currency,
                order.PaymentProviderCode,
                order.Delivery.DeliveryMethodCode,
                order.Delivery.DeliveryMethodName,
                order.CourierName,
                order.TrackingNumber,
                order.InvoiceNumber,
                order.PlacedAtUtc,
                order.PaymentDueAtUtc,
                order.PaidAtUtc,
            })
            .ToListAsync(cancellationToken);

        return new AdminOrderListPage(
            rows
                .Select(row =>
                {
                    var isPickup = IsPickup(row.DeliveryMethodCode);

                    return new AdminOrderListRow(
                        row.PublicId,
                        row.OrderNumber,
                        row.Status.ToString(),
                        OrderStatusLabels.For(row.Status, isPickup),
                        FullName(row.FirstName, row.LastName),
                        row.Email,
                        row.GrandTotal,
                        row.Currency,
                        row.PaymentProviderCode,
                        row.DeliveryMethodName,
                        isPickup,
                        row.CourierName,
                        row.TrackingNumber,
                        row.InvoiceNumber,
                        row.PlacedAtUtc,
                        row.PaidAtUtc,

                        // Only while the order is still awaiting payment. Negative rather than clamped once
                        // the deadline has passed, deliberately: "overdue by eleven minutes" and "due now"
                        // are different facts. Null for every other status, because an order that is paid,
                        // expired or cancelled has no deadline left to run out — and a countdown on one
                        // would be a number a staff member could act on wrongly.
                        row.Status == OrderStatus.PendingPayment
                            ? (long)Math.Round((row.PaymentDueAtUtc - nowUtc).TotalSeconds)
                            : null);
                })
                .ToList(),
            page,
            pageSize,
            total,
            sort,
            await ReadStatusCountsAsync(cancellationToken));
    }

    /// <summary>
    /// How many orders sit in each status, over the whole table.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not scoped to the search term or the status filter</b> — see
    /// <see cref="AdminOrderListPage.StatusCounts"/>. A <c>GROUP BY Status</c> is served by
    /// <c>IX_Orders_Status_Placed</c> as an index scan of its leading column, so it costs one scan of the
    /// narrowest index on the table rather than a scan of the table.
    /// <para>
    /// The names are the dictionary's keys rather than the numbers, for the reason
    /// <see cref="AdminOrderListRow.Status"/> gives: a client that branched on 3 would break the day a
    /// member was added, and a screen that rendered 3 would be unreadable.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, int>> ReadStatusCountsAsync(
        CancellationToken cancellationToken)
    {
        var counts = await dbContext.Orders
            .AsNoTracking()
            .GroupBy(order => order.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        // Every status, including the ones with no orders: a tab whose count disappeared when it reached
        // zero would be a tab a staff member could not tell from a tab that was never there.
        return Enum.GetValues<OrderStatus>()
            .ToDictionary(
                value => value.ToString(),
                value => counts.FirstOrDefault(row => row.Status == value)?.Count ?? 0,
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Narrows the list to the search term and the status.
    /// </summary>
    /// <remarks>
    /// <b>One predicate per classification, never all of them at once.</b> A single
    /// "match the term against six columns" <c>WHERE</c> would be a guaranteed table scan with six
    /// <c>LIKE '%…%'</c> in it and would also match a name against an order number, so a customer called
    /// "2026" would return half the year. <see cref="AdminOrderSearch"/> decides which column the term is
    /// about, and this method writes exactly that predicate.
    /// <para>
    /// <b>Which index each one can use:</b>
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// a document-number prefix seeks <c>UX_Orders_OrderNumber</c> and <c>UX_Orders_InvoiceNumber</c>
    /// (<c>StartsWith</c> translates to <c>LIKE @p + '%'</c>, which is seekable);
    /// </description></item>
    /// <item><description>
    /// the digits-only form adds an <c>EndsWith</c>, which <b>cannot</b> seek — <c>IX_Orders_Placed</c>
    /// includes <c>OrderNumber</c>, so it is a scan of that index rather than of the table. The trade is
    /// deliberate and is written up in <see cref="AdminOrderSearch"/>: failing to find the order the
    /// customer is holding is the one outcome this screen exists to prevent;
    /// </description></item>
    /// <item><description>
    /// an email prefix seeks <c>IX_Orders_EmailNormalized</c>;
    /// </description></item>
    /// <item><description>
    /// a telephone suffix scans <c>IX_Orders_Phone</c>, which is the narrow index on that column.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>The name match is case-insensitive because the database's collation is, and that is deliberate
    /// rather than accidental.</b> Every database this project creates is
    /// <c>Latin1_General_100_CI_AS_SC</c> — doc 08 §1.1's DB23, a week-0 decision;
    /// <c>SqlServerDatabaseFixture</c> creates every test database with it and
    /// <c>CollationSpikeTests</c> reads it back off <c>sys.columns</c> — so
    /// <c>CHARINDEX</c> over these columns ignores case without being asked. Forcing it with
    /// <c>UPPER(FirstName + ' ' + LastName)</c> was the first version and is strictly worse: it makes the
    /// predicate non-sargable against any future index on the name, it is one more expression per row, and
    /// it would not actually protect against a column-level collation change — the <i>term</i> would still
    /// be compared under the column's collation. A database with a case-sensitive collation would change
    /// this search's behaviour, and <c>CollationSpikeTests</c> is what catches that.
    /// </para>
    /// <para>
    /// The name is matched with <c>Contains</c> on the <i>joined</i> name, because a caller may give a
    /// surname that is the second word, or a full name that spans both columns.
    /// </para>
    /// </remarks>
    private static IQueryable<Order> Filter(
        IQueryable<Order> orders,
        AdminOrderSearchTerm? term,
        OrderStatus? status)
    {
        if (status is { } wanted)
        {
            orders = orders.Where(order => order.Status == wanted);
        }

        if (term is null)
        {
            return orders;
        }

        switch (term.Kind)
        {
            case AdminOrderSearchKind.Email:
                var email = term.Value;
                return orders.Where(order => order.EmailNormalized.StartsWith(email));

            case AdminOrderSearchKind.Phone:
                var phone = term.Value;
                return orders.Where(order => order.Phone != null && order.Phone.EndsWith(phone));

            case AdminOrderSearchKind.NumberOrName:
            default:
                var text = term.Value;
                var suffix = term.SequenceSuffix;

                return suffix is null
                    ? orders.Where(order =>
                        order.OrderNumber.StartsWith(text)
                        || (order.InvoiceNumber != null && order.InvoiceNumber.StartsWith(text))
                        || (order.FirstName + " " + (order.LastName ?? string.Empty)).Contains(text))
                    : orders.Where(order =>
                        order.OrderNumber.StartsWith(text)
                        || order.OrderNumber.EndsWith(suffix)
                        || (order.InvoiceNumber != null
                            && (order.InvoiceNumber.StartsWith(text) || order.InvoiceNumber.EndsWith(suffix))));
        }
    }

    public async Task<AdminOrderDetailDto?> ReadDetailAsync(
        Guid orderPublicId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // One query for the order row, then one each for the collections. Not a single query with Include:
        // an order with ten lines, four payments and nine history rows would come back as a 360-row
        // cartesian product, which is the shape EF's own split-query guidance exists for — and these are
        // projections to contracts, so there is no graph to stitch either way.
        var order = await dbContext.Orders
            .AsNoTracking()
            .Where(candidate => candidate.PublicId == orderPublicId)
            .Select(candidate => new OrderHead(
                candidate.Id,
                candidate.PublicId,
                candidate.OrderNumber,
                candidate.Status,
                candidate.StockState,
                candidate.FirstName,
                candidate.LastName,
                candidate.Email,
                candidate.Phone,
                candidate.CustomerUserId,
                candidate.AnonymisedAtUtc,
                candidate.Delivery.DeliveryMethodCode,
                candidate.Delivery.DeliveryMethodName,
                candidate.Delivery.PickupLocationName,
                candidate.Delivery.EstimatedDaysMin,
                candidate.Delivery.EstimatedDaysMax,
                candidate.DeliveryDistrictName,
                candidate.DeliveryZone,
                candidate.ShipAddress.RecipientName,
                candidate.ShipAddress.Phone,
                candidate.ShipAddress.Line1,
                candidate.ShipAddress.Line2,
                candidate.ShipAddress.City,
                candidate.ShipAddress.PostalCode,
                candidate.ShipAddress.Landmark,
                candidate.Currency,
                candidate.ItemsOriginalTotal,
                candidate.ItemsListTotal,
                candidate.ItemsSubtotal,
                candidate.MarkdownDiscountTotal,
                candidate.CampaignDiscountTotal,
                candidate.PromoCodeText,
                candidate.PromoDiscountAmount,
                candidate.DeliveryFeeAmount,
                candidate.DeliveryDiscountAmount,
                candidate.GrandTotal,
                candidate.RefundedAmount,
                candidate.CourierId,
                candidate.CourierName,
                candidate.TrackingNumber,
                candidate.InvoiceNumber,
                candidate.InvoiceIssuedAtUtc,
                candidate.CustomerNote,
                candidate.InternalNote,
                candidate.CancelReason,
                candidate.PlacedAtUtc,
                candidate.PaymentDueAtUtc,
                candidate.PaidAtUtc,
                candidate.ShippedAtUtc,
                candidate.DeliveredAtUtc,
                candidate.ClosedAtUtc,
                candidate.PaymentProviderCode,
                candidate.CaptureLockedUntilUtc))
            .FirstOrDefaultAsync(cancellationToken);

        if (order is null)
        {
            return null;
        }

        var isPickup = IsPickup(order.DeliveryMethodCode);

        var lines = await ReadLinesAsync(order.Id, cancellationToken);
        var payments = await ReadPaymentsAsync(order.Id, cancellationToken);
        var refunds = await ReadRefundsAsync(order.Id, cancellationToken);
        var history = await ReadHistoryAsync(order.Id, isPickup, cancellationToken);

        return new AdminOrderDetailDto(
            order.PublicId,
            order.OrderNumber,
            order.Status.ToString(),
            OrderStatusLabels.For(order.Status, isPickup),
            order.StockState.ToString(),
            new AdminOrderCustomerDto(
                FullName(order.FirstName, order.LastName),
                order.Email,
                order.Phone,
                order.CustomerUserId is not null,
                order.AnonymisedAtUtc is not null),
            new AdminOrderDeliveryDto(
                order.DeliveryMethodCode,
                order.DeliveryMethodName,
                isPickup,
                order.PickupLocationName,
                order.DeliveryDistrictName,
                order.DeliveryZone.ToString(),
                order.EstimatedDaysMin,
                order.EstimatedDaysMax,
                order.RecipientName,
                order.RecipientPhone,
                order.Line1,
                order.Line2,
                order.City,
                order.PostalCode,
                order.Landmark),
            new AdminOrderMoneyDto(
                order.Currency,
                order.ItemsOriginalTotal,
                order.ItemsListTotal,
                order.ItemsSubtotal,
                order.MarkdownDiscountTotal,
                order.CampaignDiscountTotal,
                order.PromoCodeText,
                order.PromoDiscountAmount,
                order.DeliveryFeeAmount,
                order.DeliveryDiscountAmount,
                order.GrandTotal,
                order.RefundedAmount),
            lines,
            payments,
            refunds,
            history,
            await ReadDispatchAsync(order, cancellationToken),
            await ReadActionsAsync(order, isPickup, payments, cancellationToken),
            order.CustomerNote,
            order.InternalNote,
            order.CancelReason,
            order.InvoiceNumber,
            order.InvoiceIssuedAtUtc,
            order.PlacedAtUtc,
            order.PaymentDueAtUtc,
            order.Status == OrderStatus.PendingPayment
                ? (long)Math.Round((order.PaymentDueAtUtc - nowUtc).TotalSeconds)
                : null,
            order.PaidAtUtc,
            order.ShippedAtUtc,
            order.DeliveredAtUtc,
            order.ClosedAtUtc);
    }

    private async Task<IReadOnlyList<AdminOrderLineDto>> ReadLinesAsync(
        int orderId,
        CancellationToken cancellationToken) =>
        await dbContext.OrderLines
            .AsNoTracking()
            .Where(line => line.OrderId == orderId)
            .OrderBy(line => line.LineNumber)
            .Select(line => new AdminOrderLineDto(
                line.LineNumber,
                line.Sku,
                line.ProductName,
                line.ProductCode,
                line.ColourName,
                line.SizeLabel,
                line.Quantity,
                line.UnitOriginalPrice,
                line.UnitListPrice,
                line.UnitPrice,
                line.LineSubtotal,
                line.PromoDiscountAllocated,
                line.LineTotal,
                line.RefundedQuantity))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Every payment attempt, newest first.
    /// </summary>
    /// <remarks>
    /// <b>Every one of them, not only the settlement.</b> Several rows per order are normal (doc 08 §3.5):
    /// a rejected transfer closes its row and a resubmission opens a new one. A customer who says "I sent
    /// it twice" is asking about this list, and showing only the live row would make a rejection — and the
    /// reason a staff member wrote for it — invisible on the one screen it is needed.
    /// <para>
    /// <b>The slip survives the decision.</b> A soft-deleted asset is one the customer replaced and is not
    /// shown, but an approved payment keeps its slip: the evidence outlives the decision, which is the rule
    /// <c>IBankTransferReader.FindSlipAsync</c> already follows.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<AdminOrderPaymentDto>> ReadPaymentsAsync(
        int orderId,
        CancellationToken cancellationToken) =>
        await dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == orderId)
            .OrderByDescending(payment => payment.Id)
            .Select(payment => new AdminOrderPaymentDto(
                payment.ProviderCode,
                payment.Status.ToString(),
                payment.IsOrderSettlement,
                payment.Amount,
                payment.Currency,

                // The foreign figure, which is the one that is allowed to differ from LKR (doc 08 §3.5).
                // Null for every payment this build can create; the screen renders it only when set.
                payment.ProviderAmount,
                payment.ProviderCurrency,
                payment.PayerReference,
                payment.ProofAsset != null && !payment.ProofAsset.IsDeleted
                    && payment.ProofAsset.Status == MediaAssetStatus.Ready
                    ? payment.ProofAsset.PublicId
                    : null,
                payment.ProofAsset != null && !payment.ProofAsset.IsDeleted
                    ? payment.ProofAsset.ContentType
                    : null,
                payment.ProofAsset != null && !payment.ProofAsset.IsDeleted
                    ? payment.ProofAsset.OriginalFileName
                    : null,
                payment.FailureCode,
                payment.FailureMessage,

                // The approver's address, from identity.Users. A left join rather than a required one
                // because doc 08 §1.5 gives these user columns no FK — users are anonymised, never
                // hard-deleted — so the row may name an id whose user row has since been overwritten.
                dbContext.Users
                    .Where(user => user.Id == payment.ApprovedByUserId)
                    .Select(user => user.Email)
                    .FirstOrDefault(),
                payment.ApprovedAtUtc,
                payment.ApprovalNote,
                payment.CreatedAtUtc,
                payment.CompletedAtUtc))
            .ToListAsync(cancellationToken);

    private async Task<IReadOnlyList<AdminOrderRefundDto>> ReadRefundsAsync(
        int orderId,
        CancellationToken cancellationToken) =>
        await dbContext.Refunds
            .AsNoTracking()
            .Where(refund => refund.OrderId == orderId)
            .OrderByDescending(refund => refund.Id)
            .Select(refund => new AdminOrderRefundDto(
                refund.Amount,
                refund.Currency,
                refund.ProviderAmount,
                refund.Method.ToString(),
                refund.Status.ToString(),
                refund.Reason,
                refund.RestockItems,
                dbContext.Users
                    .Where(user => user.Id == refund.CreatedByUserId)
                    .Select(user => user.Email)
                    .FirstOrDefault(),
                refund.CreatedAtUtc,
                refund.CompletedAtUtc))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The order's own timeline, oldest first.
    /// </summary>
    /// <remarks>
    /// Oldest first because it is read as a story rather than scanned as a queue, and because the first row
    /// — placement, whose <c>FromStatus</c> is null — is the one that gives the rest their context.
    /// </remarks>
    private async Task<IReadOnlyList<AdminOrderHistoryDto>> ReadHistoryAsync(
        int orderId,
        bool isPickup,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.OrderStatusHistory
            .AsNoTracking()
            .Where(entry => entry.OrderId == orderId)
            .OrderBy(entry => entry.OccurredAtUtc)
            .ThenBy(entry => entry.Id)
            .Select(entry => new
            {
                entry.FromStatus,
                entry.ToStatus,
                entry.Source,
                entry.Note,
                entry.OccurredAtUtc,
                ChangedByUserEmail = dbContext.Users
                    .Where(user => user.Id == entry.ChangedByUserId)
                    .Select(user => user.Email)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new AdminOrderHistoryDto(
                row.FromStatus?.ToString(),
                row.ToStatus.ToString(),
                row.FromStatus is { } from ? OrderStatusLabels.For(from, isPickup) : null,
                OrderStatusLabels.For(row.ToStatus, isPickup),
                row.Source.ToString(),
                row.ChangedByUserEmail,
                row.Note,
                row.OccurredAtUtc))
            .ToList();
    }

    /// <summary>
    /// The dispatch, with the tracking link built from the courier's <b>current</b> template.
    /// </summary>
    /// <remarks>
    /// Doc 08 §3.5 is explicit: the order keeps the courier's name and the tracking number, never a built
    /// URL, and "editing a template changes the link on older orders too". So the template is read live and
    /// the <i>name</i> is the order's snapshot — a renamed courier does not rewrite a past dispatch note,
    /// and a deleted one leaves the number as text rather than a broken link.
    /// </remarks>
    private async Task<AdminOrderDispatchDto?> ReadDispatchAsync(
        OrderHead order,
        CancellationToken cancellationToken)
    {
        if (order.ShippedAtUtc is not { } shippedAt
            || order.CourierName is null
            || order.TrackingNumber is null)
        {
            return null;
        }

        var template = order.CourierId is { } courierId
            ? await dbContext.Couriers
                .AsNoTracking()
                .Where(courier => courier.Id == courierId)
                .Select(courier => courier.TrackingUrlTemplate)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return new AdminOrderDispatchDto(
            // **The order's own column, with no cleverness on top of it.** The first version wrote
            // `template is null && order.CourierId is null ? null : order.CourierId`, which is `order
            // .CourierId` with extra words and a comment claiming it handled a deleted courier — it did
            // not, and could not: `FK_Orders_Couriers_CourierId` is `NoAction`, so the row cannot be
            // deleted while an order points at it. A null here is an order that never had a courier,
            // or one whose column was cleared deliberately. A wrong comment is worse than none.
            order.CourierId,
            order.CourierName,
            order.TrackingNumber,
            BuildTrackingUrl(template, order.TrackingNumber),
            shippedAt);
    }

    /// <summary>
    /// The courier's template with the placeholder replaced, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <b>The number is URL-escaped before it goes in.</b> The template is a URL and the number lands in
    /// its query string or path; a number containing <c>&amp;</c> or <c>#</c> would otherwise silently
    /// truncate the link or add a parameter the courier did not expect. Tracking numbers are validated to
    /// printable ASCII on the way in, which is not the same as "safe in a URL".
    /// </remarks>
    private static string? BuildTrackingUrl(string? template, string trackingNumber) =>
        string.IsNullOrWhiteSpace(template)
            ? null
            : template.Replace(
                Courier.TrackingNumberPlaceholder,
                Uri.EscapeDataString(trackingNumber),
                StringComparison.Ordinal);

    /// <summary>
    /// Which actions this order can take right now (doc 08 §4.1, doc 11 P9).
    /// </summary>
    /// <remarks>
    /// <b>A convenience for the screen and never the boundary</b> — every flag is re-decided inside the
    /// transition's own conditional UPDATE. What it buys is a screen that offers the action that will work,
    /// and a sentence rather than a dead control for the two cases where the shop has not been set up yet.
    /// </remarks>
    private async Task<AdminOrderActionsDto> ReadActionsAsync(
        OrderHead order,
        bool isPickup,
        IReadOnlyList<AdminOrderPaymentDto> payments,
        CancellationToken cancellationToken)
    {
        // "Is there anything to pick from" — not "how many are there", so a shop with four hundred
        // couriers does not pay for a count. AnyAsync over IsActive is a seek on the table's own small
        // clustered index; there is no index on IsActive and there should not be, for a table with a
        // handful of rows in it.
        var couriersConfigured = await dbContext.Couriers
            .AsNoTracking()
            .AnyAsync(courier => courier.IsActive, cancellationToken);

        var canStartProcessing = order.Status == OrderStatus.Paid;
        var canMarkDelivered = order.Status == OrderStatus.Shipped;
        var canEditDispatch = !isPickup
            && order.Status is OrderStatus.Shipped or OrderStatus.Delivered
            && couriersConfigured;

        var readyForCollection = isPickup && order.Status == OrderStatus.Processing;
        var canMarkShipped = !isPickup && order.Status == OrderStatus.Processing && couriersConfigured;

        // Doc 08 §4.1's `Expired / Cancelled -> Paid`, and issue #124's whole point: the bank queue cannot
        // reach an expired order, so this screen is the route. The order must also still have a bank
        // payment in a state that could settle it — an order whose transfer was never submitted has no
        // money to accept, and offering the button would send a staff member into a refusal.
        //
        // **`Cancelled` is in the payment list, and leaving it out was a real gap.** Cancelling an unpaid
        // order closes its open payment to `Cancelled` (doc 08 §4.3, W3-04b), so after a cancel the only
        // bank row on the order is in a status this flag used to ignore — and doc 08 §4.1's
        // `Cancelled → Paid` edge then had no button anywhere, which meant a shop could cancel an order
        // and be unable to accept the transfer that arrived the next day.
        //
        // The widening is a read model catching up with a transaction that was already right:
        // `BankTransferReviewStore.AcceptLateAsync` claims on `Status IN (5,6) AND StockState = 2` and
        // then takes the latest payment in `Pending`, `Expired` **or `Cancelled`** — W3-03 put `Cancelled`
        // in that list deliberately, with a comment saying it was latent until a customer cancel existed.
        // So this changes no money path, and the flag and the claim now accept exactly the same orders:
        // `A_cancelled_order_offers_accept_late_payment` asserts both ends on one row.
        var canAcceptLatePayment =
            order.Status is OrderStatus.Expired or OrderStatus.Cancelled
            && order.PaymentProviderCode == PaymentProviderCodes.BankTransfer
            && payments.Any(payment =>
                payment.ProviderCode == PaymentProviderCodes.BankTransfer
                && payment.Status is nameof(PaymentStatus.Expired)
                    or nameof(PaymentStatus.Pending)
                    or nameof(PaymentStatus.Cancelled));

        // Doc 11 P10 flow A and step 2's guards, for the Orders.Edit holder this screen is for. The four
        // the transaction claims on, read here so the screen offers the action that will work — and the
        // PayPal PENDING rule (doc 08 §4.1 R1), which hands that one case to Orders.Approve alone and so
        // is not this screen's to offer. Nothing in this build can produce a PayPal payment, so that
        // clause is unreachable; it is here because leaving it out would make the *flag* wrong the day
        // W4-01 lands, while the transaction stayed right.
        var canCancel =
            order.Status == OrderStatus.PendingPayment
            && order.StockState == OrderStockState.Reserved
            && (order.CaptureLockedUntilUtc is null || order.CaptureLockedUntilUtc < clock.UtcNow.UtcDateTime)
            && !payments.Any(payment => payment.IsOrderSettlement)
            && !payments.Any(payment =>
                payment.ProviderCode == PaymentProviderCodes.PayPal
                && payment.Status == nameof(PaymentStatus.Pending));

        return new AdminOrderActionsDto(
            canStartProcessing,
            canMarkShipped,
            readyForCollection,
            canMarkDelivered,
            canEditDispatch,
            canAcceptLatePayment,
            canCancel,
            couriersConfigured,
            BlockedReason(order.Status, isPickup, couriersConfigured));
    }

    /// <summary>
    /// Why the obvious next action is unavailable, in a sentence a staff member can act on.
    /// </summary>
    /// <remarks>
    /// <b>It exists because "offer a disabled control with no explanation" is the failure mode this task
    /// was told to avoid.</b> With no courier configured a delivery order genuinely cannot ship — which is
    /// correct, because inventing a courier would be worse — so the screen has to say what to go and do.
    /// </remarks>
    private static string? BlockedReason(OrderStatus status, bool isPickup, bool couriersConfigured) =>
        status == OrderStatus.Processing && !isPickup && !couriersConfigured
            ? "This order cannot be marked as shipped until a courier has been added. Add one under "
              + "Settings → Couriers, with the tracking address its website uses."
            : null;

    public async Task<OrderInvoiceDto?> ReadInvoiceAsync(
        Guid orderPublicId,
        CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .Where(candidate =>
                candidate.PublicId == orderPublicId

                // An invoice exists from the moment the order is paid and not before (doc 11 P9). The
                // filter is in the query so "no such order" and "that order has no invoice" are one
                // answer: the token never expires (G-41), so the response must not become an oracle about
                // which orders exist and which are paid.
                && candidate.InvoiceNumber != null)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.PublicId,
                candidate.OrderNumber,
                InvoiceNumber = candidate.InvoiceNumber!,
                candidate.InvoiceIssuedAtUtc,
                candidate.FirstName,
                candidate.LastName,
                candidate.Email,
                candidate.Phone,
                candidate.CustomerUserId,
                candidate.AnonymisedAtUtc,
                candidate.Delivery.DeliveryMethodCode,
                candidate.Delivery.DeliveryMethodName,
                candidate.Delivery.PickupLocationName,
                candidate.Delivery.EstimatedDaysMin,
                candidate.Delivery.EstimatedDaysMax,
                candidate.DeliveryDistrictName,
                candidate.DeliveryZone,
                candidate.ShipAddress,
                candidate.Currency,
                candidate.ItemsOriginalTotal,
                candidate.ItemsListTotal,
                candidate.ItemsSubtotal,
                candidate.MarkdownDiscountTotal,
                candidate.CampaignDiscountTotal,
                candidate.PromoCodeText,
                candidate.PromoDiscountAmount,
                candidate.DeliveryFeeAmount,
                candidate.DeliveryDiscountAmount,
                candidate.GrandTotal,
                candidate.RefundedAmount,
                candidate.PlacedAtUtc,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (order is null)
        {
            return null;
        }

        // The settling payment, which is the one that says how the order was actually paid. A rejected
        // attempt must never name the method on the document.
        var settlement = await dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == order.Id && payment.IsOrderSettlement)
            .Select(payment => new { payment.ProviderCode, payment.PayerReference })
            .FirstOrDefaultAsync(cancellationToken);

        return new OrderInvoiceDto(
            order.InvoiceNumber,

            // The issue time, falling back to the payment time. Both are written in the same transaction
            // (doc 08 §4.1), so they are the same instant for every order this build has paid; the
            // fallback covers a row from before the column existed rather than a disagreement.
            order.InvoiceIssuedAtUtc ?? order.PlacedAtUtc,
            order.OrderNumber,
            order.PublicId,
            order.PlacedAtUtc,

            // Filled in by the use case from ops.Settings. None here rather than null, so a renderer that
            // somehow reached this value prints nothing rather than throwing.
            InvoiceSellerDetails.None,
            new AdminOrderCustomerDto(
                FullName(order.FirstName, order.LastName),
                order.Email,
                order.Phone,
                order.CustomerUserId is not null,
                order.AnonymisedAtUtc is not null),
            new AdminOrderDeliveryDto(
                order.DeliveryMethodCode,
                order.DeliveryMethodName,
                IsPickup(order.DeliveryMethodCode),
                order.PickupLocationName,
                order.DeliveryDistrictName,
                order.DeliveryZone.ToString(),
                order.EstimatedDaysMin,
                order.EstimatedDaysMax,
                order.ShipAddress.RecipientName,
                order.ShipAddress.Phone,
                order.ShipAddress.Line1,
                order.ShipAddress.Line2,
                order.ShipAddress.City,
                order.ShipAddress.PostalCode,
                order.ShipAddress.Landmark),
            new AdminOrderMoneyDto(
                order.Currency,
                order.ItemsOriginalTotal,
                order.ItemsListTotal,
                order.ItemsSubtotal,
                order.MarkdownDiscountTotal,
                order.CampaignDiscountTotal,
                order.PromoCodeText,
                order.PromoDiscountAmount,
                order.DeliveryFeeAmount,
                order.DeliveryDiscountAmount,
                order.GrandTotal,
                order.RefundedAmount),
            await ReadLinesAsync(order.Id, cancellationToken),
            PaymentMethodLabel(settlement?.ProviderCode),
            settlement?.PayerReference);
    }

    /// <summary>
    /// How the order was paid, in words.
    /// </summary>
    /// <remarks>
    /// <b>Unknown falls back to the code rather than to a guess.</b> An invoice that said "Bank transfer"
    /// about a payment it could not identify would be a statement about money that nobody checked; the raw
    /// code is ugly and true. The <see langword="null"/> case is an order whose settlement row is missing,
    /// which cannot happen for a paid order (doc 08 §4.1 writes both in one transaction) and is therefore
    /// worth printing as the plain fact rather than silently.
    /// </remarks>
    private static string PaymentMethodLabel(string? providerCode) => providerCode switch
    {
        null => "Not recorded",
        PaymentProviderCodes.BankTransfer => "Bank transfer",
        PaymentProviderCodes.PayPal => "PayPal",
        _ => providerCode,
    };

    public async Task<IReadOnlyList<AdminCourierDto>> ReadCouriersAsync(
        CancellationToken cancellationToken) =>
        await dbContext.Couriers
            .AsNoTracking()

            // Active first, then the client's own order, then by name so the list is stable when two
            // couriers share a sort order. A staff member picking a carrier reads the top of this list.
            .OrderByDescending(courier => courier.IsActive)
            .ThenBy(courier => courier.SortOrder)
            .ThenBy(courier => courier.Name)
            .Select(courier => new AdminCourierDto(
                courier.Id,
                courier.Name,
                courier.TrackingUrlTemplate,
                courier.Phone,
                courier.IsActive,
                courier.SortOrder,

                // IX_Orders_CourierId makes this a seek per courier. It counts every order ever shipped
                // with them, which is the figure that answers "is anything of ours out with them" before a
                // staff member deactivates one.
                dbContext.Orders.Count(order => order.CourierId == courier.Id)))
            .ToListAsync(cancellationToken);

    public async Task<AdminOrderTarget?> FindTargetAsync(
        Guid orderPublicId,
        CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .Where(candidate => candidate.PublicId == orderPublicId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.OrderNumber,
                candidate.Status,
                candidate.Delivery.DeliveryMethodCode,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return order is null
            ? null
            : new AdminOrderTarget(
                order.Id,
                order.OrderNumber,
                order.Status,
                IsPickup(order.DeliveryMethodCode));
    }

    /// <summary>
    /// Whether a delivery-method code is store pickup.
    /// </summary>
    /// <remarks>
    /// <c>Ordinal</c>, because the code is a <c>varchar(20)</c> machine value and not text a person types.
    /// The column's collation would make a bare comparison case-insensitive, which is not a property this
    /// comparison should borrow from the database.
    /// </remarks>
    private static bool IsPickup(string deliveryMethodCode) =>
        string.Equals(deliveryMethodCode, DeliveryMethod.PickupCode, StringComparison.Ordinal);

    /// <summary>
    /// First and last name joined, or just the first when there is no last.
    /// </summary>
    /// <remarks>
    /// The same rule <c>BankTransferReader</c> uses, so the two screens show one customer's name the same
    /// way. A trailing space on a one-name order would be invisible on screen and wrong in a search.
    /// </remarks>
    private static string FullName(string firstName, string? lastName) =>
        string.IsNullOrWhiteSpace(lastName) ? firstName : $"{firstName} {lastName}";

    /// <summary>
    /// The order row, as one projection.
    /// </summary>
    /// <remarks>
    /// <b>A record class and deliberately not a <c>record struct</c>.</b>
    /// <c>FirstOrDefaultAsync</c> over a projection to a value type cannot return null — it returns
    /// <c>default</c> — so a missing order would come back with every field zeroed and
    /// <c>Status = PendingPayment</c>, which is the defect <c>BankTransferReviewStore.OrderSnapshot</c>
    /// records in full: a PayPal order was approved and paid by the bank flow because a struct made "no
    /// such row" unrepresentable.
    /// </remarks>
    private sealed record OrderHead(
        int Id,
        Guid PublicId,
        string OrderNumber,
        OrderStatus Status,
        OrderStockState StockState,
        string FirstName,
        string? LastName,
        string Email,
        string? Phone,
        int? CustomerUserId,
        DateTime? AnonymisedAtUtc,
        string DeliveryMethodCode,
        string DeliveryMethodName,
        string? PickupLocationName,
        byte EstimatedDaysMin,
        byte EstimatedDaysMax,
        string DeliveryDistrictName,
        DeliveryZone DeliveryZone,
        string? RecipientName,
        string? RecipientPhone,
        string? Line1,
        string? Line2,
        string? City,
        string? PostalCode,
        string? Landmark,
        string Currency,
        decimal ItemsOriginalTotal,
        decimal ItemsListTotal,
        decimal ItemsSubtotal,
        decimal MarkdownDiscountTotal,
        decimal CampaignDiscountTotal,
        string? PromoCodeText,
        decimal PromoDiscountAmount,
        decimal DeliveryFeeAmount,
        decimal DeliveryDiscountAmount,
        decimal GrandTotal,
        decimal RefundedAmount,
        int? CourierId,
        string? CourierName,
        string? TrackingNumber,
        string? InvoiceNumber,
        DateTime? InvoiceIssuedAtUtc,
        string? CustomerNote,
        string? InternalNote,
        string? CancelReason,
        DateTime PlacedAtUtc,
        DateTime PaymentDueAtUtc,
        DateTime? PaidAtUtc,
        DateTime? ShippedAtUtc,
        DateTime? DeliveredAtUtc,
        DateTime? ClosedAtUtc,
        string PaymentProviderCode,

        // Doc 11 P10 step 2's capture-lock guard, for AdminOrderActions.CanCancel. Nothing in this build
        // sets it (it is PayPal's, W4-01), so it is null on every order today — read rather than assumed
        // null, because the flag has to be right the day it is not.
        DateTime? CaptureLockedUntilUtc);
}
