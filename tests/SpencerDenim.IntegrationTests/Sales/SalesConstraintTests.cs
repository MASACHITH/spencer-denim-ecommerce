using Microsoft.EntityFrameworkCore;
using SpencerDenim.Domain.Sales;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Every check constraint and every unique index W3-03 adds, proved by making the database refuse the row it
/// exists to stop.
/// <para>
/// <b>A constraint read out of <c>sys.check_constraints</c> proves only that text was written down.</b> These
/// tests insert the bad row and fail if it lands; each then inserts the corrected row, so a pass cannot come
/// from the insert being impossible for some unrelated reason.
/// </para>
/// </summary>
public sealed class SalesConstraintTests(SqlServerDatabaseFixture sqlServer) : SalesSchemaTestBase(sqlServer)
{
    [Fact]
    public async Task An_order_status_outside_the_state_machine_is_refused()
    {
        var reference = await ReferenceAsync();

        await Writer.AssertRejectedAsync(
            "sales.Orders",
            SalesRowWriter.With(ValidOrder(reference), "Status", (byte)8),
            "CK_Orders_Status",
            Token);
    }

    [Fact]
    public async Task An_order_stock_state_outside_the_four_doc_08_defines_is_refused()
    {
        var reference = await ReferenceAsync();

        await Writer.AssertRejectedAsync(
            "sales.Orders",
            SalesRowWriter.With(ValidOrder(reference), "StockState", (byte)4),
            "CK_Orders_StockState",
            Token);
    }

    /// <summary>
    /// Doc 08 §1.4: the LKR column may only ever say LKR. The USD figure lives in
    /// <c>Payments.ProviderAmount</c> / <c>ProviderCurrency</c>, and this is what stops the two being confused.
    /// </summary>
    [Fact]
    public async Task An_order_in_a_currency_other_than_rupees_is_refused()
    {
        var reference = await ReferenceAsync();

        await Writer.AssertRejectedAsync(
            "sales.Orders",
            SalesRowWriter.With(ValidOrder(reference), "Currency", SalesCurrency.Usd),
            "CK_Orders_Currency",
            Token);
    }

    [Fact]
    public async Task A_fourth_delivery_zone_is_refused()
    {
        var reference = await ReferenceAsync();

        await Writer.AssertRejectedAsync(
            "sales.Orders",
            SalesRowWriter.With(ValidOrder(reference), "DeliveryZone", (byte)4),
            "CK_Orders_DeliveryZone",
            Token);
    }

    /// <summary>
    /// Owner decision 2026-10-21 (independent-tester pass, attack 1): the application never builds a row that
    /// mixes a foreign country with a Sri Lankan district, because <c>PlaceOrderCommandHandler.ResolveDestinationAsync</c>
    /// ignores any <c>districtId</c> a client sends once the country resolves overseas (see
    /// <c>InternationalCheckoutTests.An_overseas_quote_ignores_a_district_sent_with_it</c> and the mass-assignment
    /// placement test below). This is the belt behind that braces: even a write that bypasses the application
    /// entirely — a bad migration, a hand run script, a future bug in that resolver — cannot land a row naming a
    /// foreign country alongside a Sri Lankan district, because <c>CK_Orders_Destination</c> refuses it at the
    /// database regardless of what wrote the row.
    /// </summary>
    [Fact]
    public async Task An_order_naming_a_foreign_country_with_a_Sri_Lankan_district_is_refused_by_the_database()
    {
        var reference = await ReferenceAsync();

        var row = ValidOrder(reference);
        row["DeliveryCountryCode"] = "FR";
        row["DeliveryCountryName"] = "France";

        // DeliveryDistrictId/Name/DeliveryZone are left exactly as ValidOrder's Sri Lankan defaults — the
        // shape a mass-assignment attempt (country overseas, district still populated) would produce.
        await Writer.AssertRejectedAsync("sales.Orders", row, "CK_Orders_Destination", Token);
    }

    /// <summary>
    /// The converse of the test above: Sri Lanka with no district at all. Doc 08's branch requires LK to
    /// <i>always</i> carry its three district columns, so a row that drops them (whatever wrote it) must be
    /// refused exactly as the foreign-country-with-a-district shape is.
    /// </summary>
    [Fact]
    public async Task A_Sri_Lankan_order_with_no_district_at_all_is_refused_by_the_database()
    {
        var reference = await ReferenceAsync();

        var row = ValidOrder(reference);
        row["DeliveryDistrictId"] = null;
        row["DeliveryDistrictName"] = null;
        row["DeliveryZone"] = null;

        await Writer.AssertRejectedAsync("sales.Orders", row, "CK_Orders_Destination", Token);
    }

    /// <summary>
    /// The one overseas shape <c>CK_Orders_Destination</c> actually allows: a foreign country, no district at
    /// all, and the International method code. Proved alongside the two refusals above so a reader cannot
    /// mistake "the constraint rejects everything with DeliveryCountryCode set" for the real, narrower rule.
    /// </summary>
    [Fact]
    public async Task An_overseas_order_with_no_district_and_the_international_method_is_accepted()
    {
        var reference = await ReferenceAsync();

        var row = ValidOrder(reference);
        row["DeliveryCountryCode"] = "FR";
        row["DeliveryCountryName"] = "France";
        row["DeliveryDistrictId"] = null;
        row["DeliveryDistrictName"] = null;
        row["DeliveryZone"] = null;
        row["DeliveryMethodCode"] = DeliveryMethod.InternationalCode;
        row["Ship_RecipientName"] = "Amelie Laurent";
        row["Ship_Phone"] = "+33 1 23 45 67 89";
        row["Ship_Line1"] = "10 Rue Example";
        row["Ship_City"] = "Paris";

        var orderId = await Writer.InsertAsync("sales.Orders", row, Token);

        Assert.True(orderId > 0);
    }

    /// <summary>
    /// Doc 08 §3.5 (DB11): a non-pickup order must have a recipient, a phone, a line 1 and a city. The same
    /// row with the method code <c>PICKUP</c> and a pickup location lands, which is what shows the constraint
    /// is the branch and not a blanket NOT NULL.
    /// </summary>
    [Fact]
    public async Task A_delivered_order_with_no_ship_to_address_is_refused_but_a_pickup_order_is_not()
    {
        var reference = await ReferenceAsync();

        await Writer.AssertRejectedAsync(
            "sales.Orders",
            SalesRowWriter.With(ValidOrder(reference), "Ship_RecipientName", null),
            "CK_Orders_ShipAddress",
            Token);

        var pickup = ValidOrder(reference);
        pickup["Ship_RecipientName"] = null;
        pickup["Ship_Phone"] = null;
        pickup["Ship_Line1"] = null;
        pickup["Ship_City"] = null;
        pickup["DeliveryMethodId"] = reference.PickupDeliveryMethodId;
        pickup["DeliveryMethodCode"] = DeliveryMethod.PickupCode;
        pickup["PickupLocationId"] = reference.PickupLocationId;

        var orderId = await Writer.InsertAsync("sales.Orders", pickup, Token);

        Assert.True(orderId > 0);
    }

    /// <summary>Doc 08 §3.5: a PICKUP order must say which shop.</summary>
    [Fact]
    public async Task A_pickup_order_with_no_pickup_location_is_refused()
    {
        var reference = await ReferenceAsync();

        var row = ValidOrder(reference);
        row["Ship_RecipientName"] = null;
        row["Ship_Phone"] = null;
        row["Ship_Line1"] = null;
        row["Ship_City"] = null;
        row["DeliveryMethodId"] = reference.PickupDeliveryMethodId;
        row["DeliveryMethodCode"] = DeliveryMethod.PickupCode;
        row["PickupLocationId"] = null;

        await Writer.AssertRejectedAsync("sales.Orders", row, "CK_Orders_Pickup", Token);
    }

    /// <summary>
    /// Each of the five clauses of <c>CK_Orders_Totals</c>, one at a time. Doc 08 §3.5 states them as one
    /// constraint, so one wrong figure anywhere in the money must be refused — this is the only place in the
    /// system where "the parts add up" is provable after the fact.
    /// </summary>
    [Theory]
    [InlineData("MarkdownDiscountTotal", 199)]
    [InlineData("CampaignDiscountTotal", 99)]
    [InlineData("GrandTotal", 1251)]
    public async Task An_order_whose_money_does_not_add_up_is_refused(string column, int value)
    {
        var reference = await ReferenceAsync();

        await Writer.AssertRejectedAsync(
            "sales.Orders",
            SalesRowWriter.With(ValidOrder(reference), column, (decimal)value),
            "CK_Orders_Totals",
            Token);
    }

    [Fact]
    public async Task An_order_with_a_negative_grand_total_is_refused()
    {
        var reference = await ReferenceAsync();

        // Consistent with every other clause, so only "GrandTotal >= 0" can reject it:
        // 900 - 1300 + 350 - 0 = -50.
        var row = ValidOrder(reference);
        row["PromoDiscountAmount"] = 1300m;
        row["GrandTotal"] = -50m;

        await Writer.AssertRejectedAsync("sales.Orders", row, "CK_Orders_Totals", Token);
    }

    [Fact]
    public async Task An_order_refunded_for_more_than_it_was_worth_is_refused()
    {
        var reference = await ReferenceAsync();

        await Writer.AssertRejectedAsync(
            "sales.Orders",
            SalesRowWriter.With(ValidOrder(reference), "RefundedAmount", 1250.01m),
            "CK_Orders_Totals",
            Token);
    }

    [Fact]
    public async Task An_invoice_seller_snapshot_that_is_not_json_is_refused()
    {
        var reference = await ReferenceAsync();

        await Writer.AssertRejectedAsync(
            "sales.Orders",
            SalesRowWriter.With(ValidOrder(reference), "InvoiceSellerSnapshotJson", "not json"),
            "CK_Orders_InvoiceSellerSnapshotJson",
            Token);
    }

    /// <summary>
    /// Doc 08 §3.5: <c>CK Quantity &gt; 0</c>. The subtotal and total are zeroed with it, so
    /// <c>CK_OrderLines_LineSubtotal</c> still holds and the quantity constraint is the <i>only</i> thing that
    /// can refuse the row — otherwise this would pass while <c>CK_OrderLines_Quantity</c> did not exist.
    /// </summary>
    [Fact]
    public async Task An_order_line_for_no_items_is_refused()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        var row = ValidLine(reference, orderId);
        row["Quantity"] = (short)0;
        row["LineSubtotal"] = 0m;
        row["LineTotal"] = 0m;

        await Writer.AssertRejectedAsync("sales.OrderLines", row, "CK_OrderLines_Quantity", Token);
    }

    /// <summary>Doc 08 §3.5: <c>RefundedQuantity ≤ Quantity</c> — more cannot come back than went out.</summary>
    [Theory]
    [InlineData((short)4)]
    [InlineData((short)-1)]
    public async Task An_order_line_refunded_for_an_impossible_quantity_is_refused(short refunded)
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        await Writer.AssertRejectedAsync(
            "sales.OrderLines",
            SalesRowWriter.With(ValidLine(reference, orderId), "RefundedQuantity", refunded),
            "CK_OrderLines_RefundedQuantity",
            Token);
    }

    /// <summary>
    /// Doc 08 §3.5's price ladder. A list price above the original would make the order's
    /// <c>MarkdownDiscountTotal</c> negative while <c>CK_Orders_Totals</c> still held, so the ladder is where
    /// that has to be caught.
    /// </summary>
    [Fact]
    public async Task An_order_line_whose_prices_run_the_wrong_way_is_refused()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        await Writer.AssertRejectedAsync(
            "sales.OrderLines",
            SalesRowWriter.With(ValidLine(reference, orderId), "UnitListPrice", 500m),
            "CK_OrderLines_PriceLadder",
            Token);
    }

    [Fact]
    public async Task An_order_line_subtotal_that_is_not_price_times_quantity_is_refused()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        var row = ValidLine(reference, orderId);
        row["LineSubtotal"] = 899m;
        row["LineTotal"] = 899m;

        await Writer.AssertRejectedAsync("sales.OrderLines", row, "CK_OrderLines_LineSubtotal", Token);
    }

    [Fact]
    public async Task An_order_line_total_that_ignores_its_promo_allocation_is_refused()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        await Writer.AssertRejectedAsync(
            "sales.OrderLines",
            SalesRowWriter.With(ValidLine(reference, orderId), "PromoDiscountAllocated", 100m),
            "CK_OrderLines_LineTotal",
            Token);
    }

    /// <summary>DB17's floor: a promo allocation bigger than the line would be a line the shop pays for.</summary>
    [Fact]
    public async Task An_order_line_the_shop_would_pay_for_is_refused()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        var row = ValidLine(reference, orderId);
        row["PromoDiscountAllocated"] = 1000m;
        row["LineTotal"] = -100m;

        await Writer.AssertRejectedAsync("sales.OrderLines", row, "CK_OrderLines_LineTotal", Token);
    }

    [Fact]
    public async Task A_negative_promo_allocation_is_refused()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        var row = ValidLine(reference, orderId);
        row["PromoDiscountAllocated"] = -10m;
        row["LineTotal"] = 910m;

        await Writer.AssertRejectedAsync(
            "sales.OrderLines",
            row,
            "CK_OrderLines_PromoDiscountAllocated",
            Token);
    }

    [Theory]
    [InlineData("ToStatus", (byte)8, "CK_OrderStatusHistory_ToStatus")]
    [InlineData("FromStatus", (byte)9, "CK_OrderStatusHistory_FromStatus")]
    [InlineData("Source", (byte)5, "CK_OrderStatusHistory_Source")]
    public async Task A_history_row_outside_doc_08s_ranges_is_refused(string column, byte value, string constraint)
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        await Writer.AssertRejectedAsync(
            "sales.OrderStatusHistory",
            SalesRowWriter.With(SalesRowWriter.StatusHistoryRow(orderId), column, value),
            constraint,
            Token);
    }

    [Fact]
    public async Task A_payment_status_outside_the_state_machine_is_refused()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        await Writer.AssertRejectedAsync(
            "sales.Payments",
            SalesRowWriter.With(
                SalesRowWriter.PaymentRow(orderId, reference.PaymentProviderCode),
                "Status",
                (byte)10),
            "CK_Payments_Status",
            Token);
    }

    /// <summary>
    /// Doc 08 §3.5: only money that has arrived — Succeeded, PartiallyRefunded or Refunded — may be the
    /// order's settlement. A Pending bank transfer flagged as settling the order would make §4.2's
    /// <c>NOT EXISTS</c> guard reject the real payment when it came.
    /// </summary>
    [Fact]
    public async Task A_payment_that_has_not_been_received_cannot_be_the_settlement()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        await Writer.AssertRejectedAsync(
            "sales.Payments",
            SalesRowWriter.PaymentRow(
                orderId,
                reference.PaymentProviderCode,
                PaymentStatus.Pending,
                isOrderSettlement: true),
            "CK_Payments_IsOrderSettlement",
            Token);

        // The same flag on a Succeeded row is fine, so the CK is about the status and not about the flag.
        var paymentId = await Writer.InsertAsync(
            "sales.Payments",
            SalesRowWriter.PaymentRow(
                orderId,
                reference.PaymentProviderCode,
                PaymentStatus.Succeeded,
                isOrderSettlement: true),
            Token);

        Assert.True(paymentId > 0);
    }

    [Fact]
    public async Task A_payment_currency_other_than_rupees_is_refused()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        await Writer.AssertRejectedAsync(
            "sales.Payments",
            SalesRowWriter.With(
                SalesRowWriter.PaymentRow(orderId, reference.PaymentProviderCode),
                "Currency",
                SalesCurrency.Usd),
            "CK_Payments_Currency",
            Token);
    }

    /// <summary>
    /// And <c>ProviderCurrency</c> carries no such constraint, because PayPal is charged in USD (D1). Asserted
    /// positively: a CK accidentally copied onto that column would break PayPal in a way nothing else catches.
    /// </summary>
    [Fact]
    public async Task A_payment_may_record_a_provider_currency_of_dollars()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        var row = SalesRowWriter.PaymentRow(orderId, reference.PaymentProviderCode);
        row["ProviderCurrency"] = SalesCurrency.Usd;
        row["ProviderAmount"] = 4.20m;
        row["ExchangeRate"] = 305.123456m;
        row["FxBufferPercent"] = 3.00m;

        var paymentId = await Writer.InsertAsync("sales.Payments", row, Token);

        Assert.True(paymentId > 0);
    }

    [Fact]
    public async Task A_negative_refunded_amount_on_a_payment_is_refused()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        await Writer.AssertRejectedAsync(
            "sales.Payments",
            SalesRowWriter.With(
                SalesRowWriter.PaymentRow(orderId, reference.PaymentProviderCode),
                "RefundedAmount",
                -1m),
            "CK_Payments_RefundedAmount",
            Token);
    }

    [Theory]
    [InlineData("EventSource", (byte)4, "CK_PaymentEvents_EventSource")]
    [InlineData("ProcessingStatus", (byte)4, "CK_PaymentEvents_ProcessingStatus")]
    [InlineData("AttemptCount", (short)-1, "CK_PaymentEvents_AttemptCount")]
    public async Task A_payment_event_outside_doc_08s_ranges_is_refused(
        string column,
        object value,
        string constraint)
    {
        var reference = await ReferenceAsync();

        await Writer.AssertRejectedAsync(
            "sales.PaymentEvents",
            SalesRowWriter.With(
                SalesRowWriter.PaymentEventRow(reference.PaymentProviderCode),
                column,
                value),
            constraint,
            Token);
    }

    [Fact]
    public async Task A_payment_event_payload_that_is_not_json_is_refused()
    {
        var reference = await ReferenceAsync();

        await Writer.AssertRejectedAsync(
            "sales.PaymentEvents",
            SalesRowWriter.With(
                SalesRowWriter.PaymentEventRow(reference.PaymentProviderCode),
                "PayloadJson",
                "<xml/>"),
            "CK_PaymentEvents_PayloadJson",
            Token);
    }

    [Theory]
    [InlineData("Amount", "CK_Refunds_Amount")]
    [InlineData("ProviderAmount", "CK_Refunds_ProviderAmount")]
    public async Task A_refund_of_nothing_is_refused(string column, string constraint)
    {
        var reference = await ReferenceAsync();
        var (orderId, paymentId) = await SettledOrderAsync(reference);

        await Writer.AssertRejectedAsync(
            "sales.Refunds",
            SalesRowWriter.With(SalesRowWriter.RefundRow(orderId, paymentId), column, 0m),
            constraint,
            Token);
    }

    [Theory]
    [InlineData("Method", (byte)4, "CK_Refunds_Method")]
    [InlineData("Status", (byte)3, "CK_Refunds_Status")]
    public async Task A_refund_outside_doc_08s_ranges_is_refused(string column, byte value, string constraint)
    {
        var reference = await ReferenceAsync();
        var (orderId, paymentId) = await SettledOrderAsync(reference);

        await Writer.AssertRejectedAsync(
            "sales.Refunds",
            SalesRowWriter.With(SalesRowWriter.RefundRow(orderId, paymentId), column, value),
            constraint,
            Token);
    }

    [Fact]
    public async Task A_refund_in_a_currency_other_than_rupees_is_refused()
    {
        var reference = await ReferenceAsync();
        var (orderId, paymentId) = await SettledOrderAsync(reference);

        await Writer.AssertRejectedAsync(
            "sales.Refunds",
            SalesRowWriter.With(SalesRowWriter.RefundRow(orderId, paymentId), "Currency", SalesCurrency.Usd),
            "CK_Refunds_Currency",
            Token);
    }

    /// <summary>
    /// G-23: a refund made in the PayPal dashboard has no staff user behind it, so the row must persist with
    /// <c>CreatedByUserId = NULL</c>. A <c>NOT NULL</c> here would make that refund impossible to record.
    /// </summary>
    [Fact]
    public async Task A_refund_with_no_user_behind_it_is_accepted()
    {
        var reference = await ReferenceAsync();
        var (orderId, paymentId) = await SettledOrderAsync(reference);

        var row = SalesRowWriter.RefundRow(orderId, paymentId);
        row["Method"] = (byte)RefundMethod.ManualOther;
        row["CreatedByUserId"] = null;
        row["ProviderRefundId"] = SalesRowWriter.Unique("REF-");

        var refundId = await Writer.InsertAsync("sales.Refunds", row, Token);

        Assert.True(refundId > 0);
    }

    [Theory]
    [InlineData("DeliveryZone", (byte)4, "CK_DeliveryMethodZoneRates_DeliveryZone")]
    [InlineData("EstimatedDaysMin", (byte)9, "CK_DeliveryMethodZoneRates_EstimatedDays")]
    public async Task A_zone_rate_outside_doc_08s_ranges_is_refused(string column, byte value, string constraint)
    {
        var methodId = await Writer.InsertAsync(
            "sales.DeliveryMethods",
            SalesRowWriter.DeliveryMethodRow(),
            Token);

        await Writer.AssertRejectedAsync(
            "sales.DeliveryMethodZoneRates",
            SalesRowWriter.With(SalesRowWriter.ZoneRateRow(methodId, DeliveryZone.Colombo), column, value),
            constraint,
            Token);
    }

    [Theory]
    [InlineData("Fee", "CK_DeliveryMethodZoneRates_Fee")]
    [InlineData("FreeAboveAmount", "CK_DeliveryMethodZoneRates_FreeAboveAmount")]
    public async Task A_negative_delivery_amount_is_refused(string column, string constraint)
    {
        var methodId = await Writer.InsertAsync(
            "sales.DeliveryMethods",
            SalesRowWriter.DeliveryMethodRow(),
            Token);

        await Writer.AssertRejectedAsync(
            "sales.DeliveryMethodZoneRates",
            SalesRowWriter.With(SalesRowWriter.ZoneRateRow(methodId, DeliveryZone.Colombo), column, -1m),
            constraint,
            Token);
    }

    /// <summary>
    /// Doc 08 §3.5: the template must contain <c>{trackingNumber}</c>. Without the check, every customer of
    /// that courier would be sent to its home page instead of their parcel.
    /// </summary>
    [Fact]
    public async Task A_tracking_template_with_no_placeholder_is_refused()
    {
        await Writer.AssertRejectedAsync(
            "sales.Couriers",
            SalesRowWriter.With(SalesRowWriter.CourierRow(), "TrackingUrlTemplate", "https://track.example.com/"),
            "CK_Couriers_TrackingUrlTemplate",
            Token);

        // And a courier with no online tracking at all is still allowed.
        var courierId = await Writer.InsertAsync(
            "sales.Couriers",
            SalesRowWriter.With(SalesRowWriter.CourierRow(), "TrackingUrlTemplate", null),
            Token);

        Assert.True(courierId > 0);
    }

    [Fact]
    public async Task A_number_series_that_starts_below_one_is_refused()
    {
        await Writer.AssertRejectedAsync(
            "sales.NumberSeries",
            SalesRowWriter.With(SalesRowWriter.NumberSeriesRow(), "NextValue", 0),
            "CK_NumberSeries_NextValue",
            Token);
    }

    /// <summary>
    /// <c>UX_Payments_OneOpen</c>, the index doc 08 §3.5 describes most carefully. One open attempt per order;
    /// a second is refused whichever of the two open statuses either row is in, because the filter covers both
    /// together — the rule is "at most one Initiated <i>or</i> Pending", not one of each.
    /// </summary>
    [Theory]
    [InlineData(PaymentStatus.Initiated, PaymentStatus.Initiated)]
    [InlineData(PaymentStatus.Initiated, PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Initiated)]
    public async Task An_order_may_have_only_one_open_payment(PaymentStatus first, PaymentStatus second)
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        var firstId = await Writer.InsertAsync(
            "sales.Payments",
            SalesRowWriter.PaymentRow(orderId, reference.PaymentProviderCode, first),
            Token);

        Assert.True(firstId > 0);

        await Writer.AssertRejectedAsync(
            "sales.Payments",
            SalesRowWriter.PaymentRow(orderId, reference.PaymentProviderCode, second),
            "UX_Payments_OneOpen",
            Token);
    }

    /// <summary>
    /// The other half of N1, and the reason the index is filtered the way it is: <c>Capturing</c> is outside it,
    /// so a capture can be in flight while the order still has an open attempt. If this ever fails, capture
    /// recovery and customer retry are both broken — and nothing in the happy path would notice.
    /// </summary>
    [Fact]
    public async Task A_capturing_payment_does_not_collide_with_an_open_one()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        await Writer.InsertAsync(
            "sales.Payments",
            SalesRowWriter.PaymentRow(orderId, reference.PaymentProviderCode, PaymentStatus.Pending),
            Token);

        var capturingId = await Writer.InsertAsync(
            "sales.Payments",
            SalesRowWriter.PaymentRow(orderId, reference.PaymentProviderCode, PaymentStatus.Capturing),
            Token);

        // And two of them, because the index must not serialise recovery either.
        var secondCapturingId = await Writer.InsertAsync(
            "sales.Payments",
            SalesRowWriter.PaymentRow(orderId, reference.PaymentProviderCode, PaymentStatus.Capturing),
            Token);

        Assert.True(capturingId > 0);
        Assert.True(secondCapturingId > 0);
    }

    /// <summary>
    /// <c>UX_Payments_OneSettlement</c>: one settling payment per order, and a duplicate capture may still be
    /// stored as status 7 outside the index — which is the whole reason doc 08 replaced the old
    /// "one succeeded per order" index with this one.
    /// </summary>
    [Fact]
    public async Task An_order_may_have_only_one_settlement_payment_but_any_number_of_exceptions()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        await Writer.InsertAsync(
            "sales.Payments",
            SalesRowWriter.PaymentRow(
                orderId,
                reference.PaymentProviderCode,
                PaymentStatus.Succeeded,
                isOrderSettlement: true),
            Token);

        await Writer.AssertRejectedAsync(
            "sales.Payments",
            SalesRowWriter.PaymentRow(
                orderId,
                reference.PaymentProviderCode,
                PaymentStatus.Succeeded,
                isOrderSettlement: true),
            "UX_Payments_OneSettlement",
            Token);

        var firstException = await Writer.InsertAsync(
            "sales.Payments",
            SalesRowWriter.PaymentRow(
                orderId,
                reference.PaymentProviderCode,
                PaymentStatus.CapturedRequiresRefund),
            Token);

        var secondException = await Writer.InsertAsync(
            "sales.Payments",
            SalesRowWriter.PaymentRow(
                orderId,
                reference.PaymentProviderCode,
                PaymentStatus.CapturedRequiresRefund),
            Token);

        Assert.True(firstException > 0);
        Assert.True(secondException > 0);
    }

    [Theory]
    [InlineData("ProviderReference", "UX_Payments_Provider_Reference")]
    [InlineData("ProviderTransactionId", "UX_Payments_Provider_Txn")]
    public async Task One_provider_identifier_cannot_back_two_payments(string column, string index)
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);
        var value = SalesRowWriter.Unique("PP-");

        await Writer.InsertAsync(
            "sales.Payments",
            SalesRowWriter.With(
                SalesRowWriter.PaymentRow(orderId, reference.PaymentProviderCode, PaymentStatus.Succeeded),
                column,
                value),
            Token);

        await Writer.AssertRejectedAsync(
            "sales.Payments",
            SalesRowWriter.With(
                SalesRowWriter.PaymentRow(orderId, reference.PaymentProviderCode, PaymentStatus.Succeeded),
                column,
                value),
            index,
            Token);

        // NULLs are outside the filter, so any number of payments may have no provider identifier yet.
        var firstNull = await Writer.InsertAsync(
            "sales.Payments",
            SalesRowWriter.PaymentRow(orderId, reference.PaymentProviderCode, PaymentStatus.Succeeded),
            Token);

        var secondNull = await Writer.InsertAsync(
            "sales.Payments",
            SalesRowWriter.PaymentRow(orderId, reference.PaymentProviderCode, PaymentStatus.Succeeded),
            Token);

        Assert.True(firstNull > 0);
        Assert.True(secondNull > 0);
    }

    [Fact]
    public async Task A_provider_event_cannot_be_recorded_twice()
    {
        var reference = await ReferenceAsync();
        var row = SalesRowWriter.PaymentEventRow(reference.PaymentProviderCode);

        await Writer.InsertWithoutIdentityAsync("sales.PaymentEvents", row, Token);

        // A redelivery: a different event type and payload, the same idempotency key. Doc 08 DB13 relies on
        // this insert failing to recognise an event it has already processed.
        var redelivery = SalesRowWriter.With(
            SalesRowWriter.PaymentEventRow(reference.PaymentProviderCode),
            "IdempotencyKey",
            row["IdempotencyKey"]);

        await Writer.AssertRejectedAsync(
            "sales.PaymentEvents",
            redelivery,
            "UX_PaymentEvents_IdempotencyKey",
            Token);
    }

    [Fact]
    public async Task A_refund_cannot_be_recorded_twice_for_one_provider_refund_id()
    {
        var reference = await ReferenceAsync();
        var (orderId, paymentId) = await SettledOrderAsync(reference);
        var providerRefundId = SalesRowWriter.Unique("REF-");

        await Writer.InsertAsync(
            "sales.Refunds",
            SalesRowWriter.With(
                SalesRowWriter.RefundRow(orderId, paymentId),
                "ProviderRefundId",
                providerRefundId),
            Token);

        await Writer.AssertRejectedAsync(
            "sales.Refunds",
            SalesRowWriter.With(
                SalesRowWriter.RefundRow(orderId, paymentId),
                "ProviderRefundId",
                providerRefundId),
            "UX_Refunds_ProviderRefundId",
            Token);
    }

    [Fact]
    public async Task An_order_cannot_have_two_lines_with_the_same_number()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        await Writer.InsertAsync("sales.OrderLines", ValidLine(reference, orderId), Token);

        await Writer.AssertRejectedAsync(
            "sales.OrderLines",
            ValidLine(reference, orderId),
            "UX_OrderLines_Order_Line",
            Token);
    }

    /// <summary>
    /// <c>UX_Orders_*</c>. The idempotency key is the one that matters most: without it, a resubmitted checkout
    /// is a second order holding a second stock reservation.
    /// </summary>
    [Theory]
    [InlineData("OrderNumber", "UX_Orders_OrderNumber")]
    [InlineData("PublicId", "UX_Orders_PublicId")]
    [InlineData("CheckoutIdempotencyKey", "UX_Orders_CheckoutIdempotencyKey")]
    public async Task An_order_identifier_cannot_be_reused(string column, string index)
    {
        var reference = await ReferenceAsync();

        var first = ValidOrder(reference);
        await Writer.InsertAsync("sales.Orders", first, Token);

        await Writer.AssertRejectedAsync(
            "sales.Orders",
            SalesRowWriter.With(ValidOrder(reference), column, first[column]),
            index,
            Token);
    }

    /// <summary>
    /// <c>UX_Orders_InvoiceNumber</c> is filtered, and both halves matter: the number must be unique once
    /// issued — it is a gapless legal series — and every unpaid order holds a NULL, so an unfiltered unique
    /// index would allow exactly one unpaid order in the shop.
    /// </summary>
    [Fact]
    public async Task An_invoice_number_is_unique_but_unissued_ones_do_not_collide()
    {
        var reference = await ReferenceAsync();
        var invoiceNumber = SalesRowWriter.Unique("INV-");

        await Writer.InsertAsync(
            "sales.Orders",
            SalesRowWriter.With(ValidOrder(reference), "InvoiceNumber", invoiceNumber),
            Token);

        await Writer.AssertRejectedAsync(
            "sales.Orders",
            SalesRowWriter.With(ValidOrder(reference), "InvoiceNumber", invoiceNumber),
            "UX_Orders_InvoiceNumber",
            Token);

        var firstUnpaid = await Writer.InsertAsync("sales.Orders", ValidOrder(reference), Token);
        var secondUnpaid = await Writer.InsertAsync("sales.Orders", ValidOrder(reference), Token);

        Assert.True(firstUnpaid > 0);
        Assert.True(secondUnpaid > 0);
    }

    [Fact]
    public async Task Two_delivery_methods_cannot_share_a_code()
    {
        var first = SalesRowWriter.DeliveryMethodRow();
        await Writer.InsertAsync("sales.DeliveryMethods", first, Token);

        await Writer.AssertRejectedAsync(
            "sales.DeliveryMethods",
            SalesRowWriter.With(SalesRowWriter.DeliveryMethodRow(), "Code", first["Code"]),
            "UX_DeliveryMethods_Code",
            Token);
    }

    [Fact]
    public async Task Two_couriers_cannot_share_a_name()
    {
        var first = SalesRowWriter.CourierRow();
        await Writer.InsertAsync("sales.Couriers", first, Token);

        await Writer.AssertRejectedAsync(
            "sales.Couriers",
            SalesRowWriter.With(SalesRowWriter.CourierRow(), "Name", first["Name"]),
            "UX_Couriers_Name",
            Token);
    }

    /// <summary>
    /// The composite key of <c>DeliveryMethodZoneRates</c>. Doc 08 §3.5 gives "no row" the meaning
    /// "unavailable", so two rows for one (method, zone) would be two answers to a question with one answer.
    /// </summary>
    [Fact]
    public async Task One_method_cannot_have_two_rates_for_one_zone()
    {
        var methodId = 0;

        try
        {
            methodId = await Writer.InsertAsync(
                "sales.DeliveryMethods",
                SalesRowWriter.DeliveryMethodRow(),
                Token);

            await Writer.InsertWithoutIdentityAsync(
                "sales.DeliveryMethodZoneRates",
                SalesRowWriter.ZoneRateRow(methodId, DeliveryZone.Western),
                Token);

            await Writer.AssertRejectedAsync(
                "sales.DeliveryMethodZoneRates",
                SalesRowWriter.ZoneRateRow(methodId, DeliveryZone.Western),
                "PK_DeliveryMethodZoneRates",
                Token);
        }
        finally
        {
            if (methodId != 0)
            {
                await DbContext.Database.ExecuteSqlRawAsync(
                    "DELETE FROM sales.DeliveryMethodZoneRates WHERE DeliveryMethodId = {0}; DELETE FROM sales.DeliveryMethods WHERE Id = {0};",
                    [methodId], Token);
            }
        }
    }

    private static Dictionary<string, object?> ValidOrder(SalesReferenceData reference) =>
        SalesRowWriter.OrderRow(
            reference.PaymentProviderCode,
            reference.StandardDeliveryMethodId,
            DeliveryMethod.StandardCode);

    private static Dictionary<string, object?> ValidLine(SalesReferenceData reference, int orderId) =>
        SalesRowWriter.OrderLineRow(
            orderId,
            reference.ProductId,
            reference.VariantId,
            reference.DepartmentId,
            reference.CategoryId);

    /// <summary>An order with a settled payment, for the refund tests.</summary>
    private async Task<(int OrderId, int PaymentId)> SettledOrderAsync(SalesReferenceData reference)
    {
        var orderId = await InsertValidOrderAsync(reference);

        var paymentId = await Writer.InsertAsync(
            "sales.Payments",
            SalesRowWriter.PaymentRow(
                orderId,
                reference.PaymentProviderCode,
                PaymentStatus.Succeeded,
                isOrderSettlement: true),
            Token);

        return (orderId, paymentId);
    }
}
