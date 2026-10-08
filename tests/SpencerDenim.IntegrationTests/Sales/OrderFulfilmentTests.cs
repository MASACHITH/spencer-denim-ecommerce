using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Doc 11 P9's fulfilment transitions, against a real SQL Server (ADM-09).
/// <para>
/// <b>Every assertion is on the rows, never on the return value alone.</b> The result says what the store
/// believes it did; <c>sales.Orders</c>, <c>sales.OrderStatusHistory</c> and <c>ops.AuditLogs</c> are what
/// it did. That distinction is the whole point of an integration test here: the thing that can go wrong is
/// a statement that affected the wrong number of rows, and no in-memory double can show it.
/// </para>
/// </summary>
public sealed class OrderFulfilmentTests(SqlServerDatabaseFixture sqlServer) : AdminOrderTestBase(sqlServer)
{
    [Fact]
    public async Task Starting_processing_moves_a_paid_order_and_writes_its_history()
    {
        var order = await PaidOrderAsync();

        var result = await StartProcessingAsync(order.OrderId, "Picking now.");

        var applied = Assert.IsType<OrderTransitionResult.Applied>(result);
        Assert.Equal(order.OrderNumber, applied.OrderNumber);
        Assert.Equal(OrderStatus.Processing, applied.Status);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Processing, row.Status);

        // Doc 08 §4.1 gives this transition no stock effect: the stock was committed at Paid, and a
        // transition that "tidied" the stock state would be writing money's worth of change for nothing.
        Assert.Equal(OrderStockState.Committed, row.StockState);
        Assert.Null(row.ShippedAtUtc);

        var history = row.StatusHistory.OrderBy(entry => entry.Id).Last();
        Assert.Equal(OrderStatus.Paid, history.FromStatus);
        Assert.Equal(OrderStatus.Processing, history.ToStatus);
        Assert.Equal(OrderStatusChangeSource.Staff, history.Source);
        Assert.Equal(7001, history.ChangedByUserId);
        Assert.Contains("Picking now.", history.Note!, StringComparison.Ordinal);

        await AssertAuditedAsync(order.PublicId, AuditActions.StatusChange, "processing");
    }

    /// <summary>
    /// <b>Orders never move backwards, and the refusal says where this one is.</b>
    /// </summary>
    /// <remarks>
    /// Doc 08 §4.1 and doc 11 P9 both state the rule, and the refusal carrying the <i>current</i> status is
    /// W3-03's <c>ShortStock</c> lesson: a screen told only "that could not be applied" went back to
    /// offering an action that could never succeed.
    /// </remarks>
    [Fact]
    public async Task A_transition_from_the_wrong_status_is_refused_and_names_where_the_order_is()
    {
        var order = await PaidOrderAsync();
        Assert.IsType<OrderTransitionResult.Applied>(await StartProcessingAsync(order.OrderId));

        // Paid -> Processing again: the order is already Processing.
        var again = await StartProcessingAsync(order.OrderId);

        var refused = Assert.IsType<OrderTransitionResult.Refused>(again);
        Assert.Equal(OrderTransitionRefusalCodes.WrongStatus, refused.Code);
        Assert.Equal(OrderStatus.Processing, refused.Status);
        Assert.Equal("Processing", refused.StatusLabel);
        Assert.Contains("already started processing", refused.Message, StringComparison.OrdinalIgnoreCase);

        // And nothing was written: a refusal has to mean "nothing happened".
        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Processing, row.Status);
        Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Processing);
    }

    /// <summary>
    /// <b>Doc 08 §4.1: a delivery order needs a courier and a tracking number before it can ship.</b>
    /// </summary>
    [Fact]
    public async Task Shipping_a_delivery_order_without_a_dispatch_is_refused()
    {
        var order = await PaidOrderAsync();
        Assert.IsType<OrderTransitionResult.Applied>(await StartProcessingAsync(order.OrderId));

        var result = await MarkShippedAsync(order.OrderId, null, null);

        var refused = Assert.IsType<OrderTransitionResult.Refused>(result);
        Assert.Equal(OrderTransitionRefusalCodes.DispatchDetailsRequired, refused.Code);
        Assert.Equal(OrderStatus.Processing, refused.Status);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Processing, row.Status);
        Assert.Null(row.ShippedAtUtc);
        Assert.Null(row.TrackingNumber);
    }

    /// <summary>
    /// Shipping writes the status, the time, the courier's <b>snapshotted name</b> and the tracking number
    /// in one statement.
    /// </summary>
    /// <remarks>
    /// <b>One statement matters.</b> A second UPDATE for the dispatch would leave an instant at which the
    /// order is Shipped with no tracking number on it — visible to this screen, and to the shopper's own
    /// order page, which is exactly where a customer would be told to follow a parcel that has no number.
    /// </remarks>
    [Fact]
    public async Task Shipping_records_the_courier_name_as_a_snapshot()
    {
        var order = await PaidOrderAsync();
        var courierId = await NewCourierAsync(name: "xunit-first-carrier");
        Assert.IsType<OrderTransitionResult.Applied>(await StartProcessingAsync(order.OrderId));

        var result = await MarkShippedAsync(order.OrderId, courierId, "TRACK-12345");

        Assert.Equal(OrderStatus.Shipped, Assert.IsType<OrderTransitionResult.Applied>(result).Status);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Shipped, row.Status);
        Assert.NotNull(row.ShippedAtUtc);
        Assert.Equal(courierId, row.CourierId);
        Assert.Equal("xunit-first-carrier", row.CourierName);
        Assert.Equal("TRACK-12345", row.TrackingNumber);

        // Doc 11 P9: "courier name is copied into the order". Renaming the courier must not rewrite the
        // dispatch note of an order that has already gone.
        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Couriers
                .Where(courier => courier.Id == courierId)
                .ExecuteUpdateAsync(
                    courier => courier.SetProperty(candidate => candidate.Name, "xunit-renamed-carrier"),
                    Token);
        }

        var after = await ReadOrderAsync(order.PublicId);
        Assert.Equal("xunit-first-carrier", after.CourierName);
    }

    /// <summary>
    /// A courier deactivated between the screen's read and the press is refused, with its own code.
    /// </summary>
    /// <remarks>
    /// The courier is read <b>inside</b> the transaction for this reason. A separate code from
    /// "dispatch details required" because the remedy differs: one is "fill the field in", the other is
    /// "that carrier has been retired, pick another".
    /// </remarks>
    [Fact]
    public async Task Shipping_with_a_deactivated_courier_is_refused()
    {
        var order = await PaidOrderAsync();
        var courierId = await NewCourierAsync(isActive: false);
        Assert.IsType<OrderTransitionResult.Applied>(await StartProcessingAsync(order.OrderId));

        var result = await MarkShippedAsync(order.OrderId, courierId, "TRACK-0002");

        var refused = Assert.IsType<OrderTransitionResult.Refused>(result);
        Assert.Equal(OrderTransitionRefusalCodes.CourierUnavailable, refused.Code);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Processing, row.Status);
        Assert.Null(row.CourierId);
    }

    [Fact]
    public async Task Shipping_with_a_courier_that_does_not_exist_is_refused()
    {
        var order = await PaidOrderAsync();
        Assert.IsType<OrderTransitionResult.Applied>(await StartProcessingAsync(order.OrderId));

        var result = await MarkShippedAsync(order.OrderId, 987654321, "TRACK-0003");

        Assert.Equal(
            OrderTransitionRefusalCodes.CourierUnavailable,
            Assert.IsType<OrderTransitionResult.Refused>(result).Code);
    }

    /// <summary>
    /// <b>Doc 11 P9's pickup variant: Ready for collection needs no courier and no tracking number.</b>
    /// </summary>
    [Fact]
    public async Task A_pickup_order_reaches_shipped_with_no_dispatch()
    {
        var order = await PaidOrderAsync();
        await MakePickupAsync(order.OrderId);
        Assert.IsType<OrderTransitionResult.Applied>(await StartProcessingAsync(order.OrderId));

        var result = await MarkShippedAsync(order.OrderId, null, null);

        Assert.Equal(OrderStatus.Shipped, Assert.IsType<OrderTransitionResult.Applied>(result).Status);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Shipped, row.Status);
        Assert.NotNull(row.ShippedAtUtc);
        Assert.Null(row.CourierId);
        Assert.Null(row.CourierName);
        Assert.Null(row.TrackingNumber);

        var history = row.StatusHistory.OrderBy(entry => entry.Id).Last();
        Assert.Contains("Ready for collection", history.Note!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A courier aimed at a pickup order is refused rather than written.
    /// </summary>
    /// <remarks>
    /// The store re-reads the delivery method itself rather than trusting the caller: the method a request
    /// implies and the order's actual method are two different facts, and the one that decides whether a
    /// courier is written has to be the order's.
    /// </remarks>
    [Fact]
    public async Task A_courier_on_a_pickup_order_is_refused()
    {
        var order = await PaidOrderAsync();
        await MakePickupAsync(order.OrderId);
        var courierId = await NewCourierAsync();
        Assert.IsType<OrderTransitionResult.Applied>(await StartProcessingAsync(order.OrderId));

        var result = await MarkShippedAsync(order.OrderId, courierId, "TRACK-0004");

        Assert.Equal(
            OrderTransitionRefusalCodes.DispatchDetailsNotAllowed,
            Assert.IsType<OrderTransitionResult.Refused>(result).Code);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Processing, row.Status);
        Assert.Null(row.CourierId);
    }

    /// <summary>
    /// <b>Correcting a dispatch on a pickup order is refused too, and that is the easy half to miss.</b>
    /// </summary>
    /// <remarks>
    /// A pickup order genuinely <i>is</i> in <c>Shipped</c> — doc 11 P9 shows it to staff as "Ready for
    /// collection" — so the dispatch correction's own status guard would be satisfied, and a courier would
    /// be written on to an order nobody is couriering. The detail screen would then draw a tracking link
    /// for a parcel sitting on a shelf.
    /// </remarks>
    [Fact]
    public async Task Correcting_a_dispatch_on_a_pickup_order_is_refused()
    {
        var order = await PaidOrderAsync();
        await MakePickupAsync(order.OrderId);
        var courierId = await NewCourierAsync();
        await ShipAsync(order.OrderId, pickup: true);

        var result = await UpdateDispatchAsync(order.OrderId, courierId, "TRACK-0005");

        Assert.Equal(
            OrderTransitionRefusalCodes.DispatchDetailsNotAllowed,
            Assert.IsType<OrderTransitionResult.Refused>(result).Code);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Shipped, row.Status);
        Assert.Null(row.CourierId);
        Assert.Null(row.TrackingNumber);
    }

    [Fact]
    public async Task Marking_delivered_records_the_time_and_the_staff_member()
    {
        var order = await PaidOrderAsync();
        await ShipAsync(order.OrderId);

        var result = await MarkDeliveredAsync(order.OrderId, "Courier confirmed by telephone.");

        Assert.Equal(OrderStatus.Delivered, Assert.IsType<OrderTransitionResult.Applied>(result).Status);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Delivered, row.Status);
        Assert.NotNull(row.DeliveredAtUtc);

        var history = row.StatusHistory.OrderBy(entry => entry.Id).Last();
        Assert.Equal(OrderStatus.Shipped, history.FromStatus);
        Assert.Equal(OrderStatus.Delivered, history.ToStatus);
        Assert.Equal(OrderStatusChangeSource.Staff, history.Source);
        Assert.Equal(7001, history.ChangedByUserId);
    }

    /// <summary>
    /// A pickup order is marked collected by staff, which doc 08 §4.1 G-18 makes the <b>only</b> way such
    /// an order ever reaches Delivered.
    /// </summary>
    [Fact]
    public async Task A_pickup_order_is_marked_collected_by_staff()
    {
        var order = await PaidOrderAsync();
        await MakePickupAsync(order.OrderId);
        await ShipAsync(order.OrderId, pickup: true);

        var result = await MarkDeliveredAsync(order.OrderId);

        Assert.Equal(OrderStatus.Delivered, Assert.IsType<OrderTransitionResult.Applied>(result).Status);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Delivered, row.Status);

        var history = row.StatusHistory.OrderBy(entry => entry.Id).Last();
        Assert.Contains("Collected by the customer", history.Note!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A correction moves the courier and the number and leaves the status alone, writing a history row
    /// whose two statuses are equal.
    /// </summary>
    /// <remarks>
    /// Doc 11 P9's "wrong tracking number entered": the status does not move because orders never move
    /// backwards, and the equal-statuses history row is <c>OrderStatusHistoryEntry</c>'s documented
    /// convention for "an event on this order that did not move it".
    /// </remarks>
    [Fact]
    public async Task Correcting_a_dispatch_keeps_the_status_and_writes_a_history_row()
    {
        var order = await PaidOrderAsync();
        var first = await ShipAsync(order.OrderId);
        var second = await NewCourierAsync(name: "xunit-second-carrier");

        var result = await UpdateDispatchAsync(order.OrderId, second, "TRACK-CORRECTED");

        var applied = Assert.IsType<OrderTransitionResult.Applied>(result);
        Assert.Equal(OrderStatus.Shipped, applied.Status);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Shipped, row.Status);
        Assert.Equal(second, row.CourierId);
        Assert.NotEqual(first, row.CourierId);
        Assert.Equal("xunit-second-carrier", row.CourierName);
        Assert.Equal("TRACK-CORRECTED", row.TrackingNumber);

        var history = row.StatusHistory.OrderBy(entry => entry.Id).Last();
        Assert.Equal(history.ToStatus, history.FromStatus);
        Assert.Equal(OrderStatus.Shipped, history.ToStatus);
        Assert.Contains("Dispatch corrected", history.Note!, StringComparison.Ordinal);

        // Doc 11 P9's exception: no second notice is sent, so the note has to say so — the customer is
        // holding a tracking link that does not work.
        Assert.Contains("No new notice", history.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Correcting_a_dispatch_before_the_order_ships_is_refused()
    {
        var order = await PaidOrderAsync();
        var courierId = await NewCourierAsync();

        var result = await UpdateDispatchAsync(order.OrderId, courierId, "TRACK-0006");

        Assert.Equal(
            OrderTransitionRefusalCodes.NotShipped,
            Assert.IsType<OrderTransitionResult.Refused>(result).Code);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Null(row.TrackingNumber);
    }

    /// <summary>
    /// <b>The internal note is writable in every status, including the terminal ones.</b>
    /// </summary>
    /// <remarks>
    /// Doc 08 §4.1 makes it the mechanism for correcting a mistake on an order that can never move
    /// backwards — so Expired and Refunded, which are precisely the statuses somebody needs to explain,
    /// must not be the ones where it is refused.
    /// </remarks>
    [Fact]
    public async Task The_internal_note_can_be_set_and_cleared_on_an_expired_order()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);
        Assert.True(await ExpireAsync(orderId));

        var set = await SetInternalNoteAsync(orderId, "Customer rang; transfer sent on the 9th.");

        var applied = Assert.IsType<OrderTransitionResult.Applied>(set);
        Assert.Equal(OrderStatus.Expired, applied.Status);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal("Customer rang; transfer sent on the 9th.", row.InternalNote);
        Assert.Equal(OrderStatus.Expired, row.Status);

        var noted = row.StatusHistory.OrderBy(entry => entry.Id).Last();
        Assert.Equal(noted.ToStatus, noted.FromStatus);
        Assert.Contains("Internal note", noted.Note!, StringComparison.Ordinal);

        var cleared = await SetInternalNoteAsync(orderId, null);
        Assert.IsType<OrderTransitionResult.Applied>(cleared);

        var after = await ReadOrderAsync(order.PublicId);
        Assert.Null(after.InternalNote);
        Assert.Contains(
            "Internal note cleared",
            after.StatusHistory.OrderBy(entry => entry.Id).Last().Note!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The note's text is not in the audit row.</b>
    /// </summary>
    /// <remarks>
    /// A judgement rather than a rule from doc 08 §3.8's redaction list, and it is asserted so the next
    /// reader does not have to take the comment's word for it: an internal note can carry a customer's
    /// reason for a refund or the content of a telephone call, and <c>ops.AuditLogs</c> is kept for three
    /// years and exportable. That it changed, by whom and when is the auditable fact.
    /// </remarks>
    [Fact]
    public async Task The_audit_row_for_a_note_does_not_carry_the_notes_text()
    {
        var order = await PaidOrderAsync();
        const string secret = "Card ending 4242, refunded by hand";

        Assert.IsType<OrderTransitionResult.Applied>(await SetInternalNoteAsync(order.OrderId, secret));

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.EntityType == nameof(Order)
                && log.EntityId == order.PublicId.ToString())
            .Select(log => log.AfterJson)
            .ToListAsync(Token);

        Assert.NotEmpty(rows);
        Assert.DoesNotContain(rows, json => json != null && json.Contains(secret, StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Two staff members pressing the same button at once: one wins and the other is told where the
    /// order went.</b>
    /// </summary>
    /// <remarks>
    /// This is doc 08 §5.2's argument, measured. The conditional UPDATE takes an update lock on the
    /// qualifying row; the second writer waits for it and then re-evaluates the predicate against the row
    /// as it now is, finds <c>Status = 2</c>, affects 0 rows and is refused. The assertion is on the
    /// <i>pair</i> — exactly one applied — rather than on which one, because either outcome is correct.
    /// </remarks>
    [Fact]
    public async Task Two_staff_starting_processing_at_once_produce_one_transition()
    {
        var order = await PaidOrderAsync();

        var both = await Task.WhenAll(
            StartProcessingAsync(order.OrderId),
            StartProcessingAsync(order.OrderId));

        Assert.Single(both, result => result is OrderTransitionResult.Applied);
        Assert.Single(both, result => result is OrderTransitionResult.Refused);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Processing, row.Status);

        // One history row, not two. A second would make the order's own timeline say it started processing
        // twice, which is the fact a customer would be read back.
        Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Processing);
    }

    /// <summary>
    /// The same race on a dispatch, which is the one that writes a courier and a tracking number.
    /// </summary>
    [Fact]
    public async Task Two_staff_shipping_at_once_write_one_dispatch()
    {
        var order = await PaidOrderAsync();
        var first = await NewCourierAsync(name: "xunit-race-a");
        var second = await NewCourierAsync(name: "xunit-race-b");
        Assert.IsType<OrderTransitionResult.Applied>(await StartProcessingAsync(order.OrderId));

        var both = await Task.WhenAll(
            MarkShippedAsync(order.OrderId, first, "TRACK-RACE-A"),
            MarkShippedAsync(order.OrderId, second, "TRACK-RACE-B"));

        Assert.Single(both, result => result is OrderTransitionResult.Applied);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Shipped, row.Status);

        // One of the two, and the courier, its snapshotted name and the number are all from the *same*
        // winner — which is what putting them in one statement with the status buys.
        Assert.True(row.CourierId == first || row.CourierId == second);
        Assert.Equal(
            row.CourierId == first ? "TRACK-RACE-A" : "TRACK-RACE-B",
            row.TrackingNumber);
        Assert.Equal(
            row.CourierId == first ? "xunit-race-a" : "xunit-race-b",
            row.CourierName);

        Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Shipped);
    }

    /// <summary>
    /// A transition aimed at an order that is not there answers <c>NotFound</c>, not a crash.
    /// </summary>
    [Fact]
    public async Task A_transition_aimed_at_no_order_is_not_found()
    {
        Assert.IsType<OrderTransitionResult.NotFound>(await StartProcessingAsync(987654321));
        Assert.IsType<OrderTransitionResult.NotFound>(await MarkDeliveredAsync(987654321));
        Assert.IsType<OrderTransitionResult.NotFound>(await SetInternalNoteAsync(987654321, "x"));
    }

    /// <summary>
    /// <see cref="IAdminOrderReader.FindTargetAsync"/> answers null for a token that names nothing, rather
    /// than a zeroed record.
    /// </summary>
    /// <remarks>
    /// <b>Asserted because the struct version of this is a defect this project has already paid for.</b>
    /// <c>BankTransferReviewStore.OrderSnapshot</c> records it in full: a projection to a value type
    /// cannot return null, so "no such row" arrived as <c>Status = PendingPayment</c> with every field
    /// zeroed, the null guard never fired, and a PayPal order was approved and paid by the bank flow.
    /// </remarks>
    [Fact]
    public async Task A_target_for_a_token_that_names_nothing_is_null()
    {
        Assert.Null(await TargetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_target_carries_the_orders_number_status_and_delivery_kind()
    {
        var order = await PaidOrderAsync();

        var target = await TargetAsync(order.PublicId);

        Assert.NotNull(target);
        Assert.Equal(order.OrderId, target.OrderId);
        Assert.Equal(order.OrderNumber, target.OrderNumber);
        Assert.Equal(OrderStatus.Paid, target.Status);
        Assert.False(target.IsPickup);

        await MakePickupAsync(order.OrderId);

        var pickup = await TargetAsync(order.PublicId);
        Assert.NotNull(pickup);
        Assert.True(pickup.IsPickup);
    }

    /// <summary>
    /// A note longer than the history column is clipped rather than thrown.
    /// </summary>
    /// <remarks>
    /// Doc 08 §3.5 makes <c>OrderStatusHistory.Note</c> <c>nvarchar(1000)</c>, and a transition that threw
    /// on its way to the database because a sentence was two characters too long would roll back a staff
    /// decision for a reason nobody would guess. The <i>prefix</i> is never the part lost, because it comes
    /// first.
    /// </remarks>
    [Fact]
    public async Task An_over_long_note_is_clipped_and_the_transition_still_commits()
    {
        var order = await PaidOrderAsync();

        var result = await StartProcessingAsync(order.OrderId, new string('n', 2_000));

        Assert.IsType<OrderTransitionResult.Applied>(result);

        var row = await ReadOrderAsync(order.PublicId);
        var history = row.StatusHistory.OrderBy(entry => entry.Id).Last();

        Assert.Equal(OrderStatusHistoryEntry.NoteMaxLength, history.Note!.Length);
        Assert.StartsWith("Processing started", history.Note, StringComparison.Ordinal);
    }

    /// <summary>Whether an audit row for this order records the act, inside the committed transaction.</summary>
    private async Task AssertAuditedAsync(Guid publicId, string action, string what)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.EntityType == nameof(Order)
                && log.EntityId == publicId.ToString()
                && log.Action == action)
            .Select(log => new { log.AfterJson, log.UserId, log.IpAddress })
            .ToListAsync(Token);

        Assert.Contains(
            rows,
            row => row.AfterJson != null && row.AfterJson.Contains(what, StringComparison.Ordinal));
        Assert.Contains(rows, row => row.UserId == 7001 && row.IpAddress == "203.0.113.9");
    }
}
