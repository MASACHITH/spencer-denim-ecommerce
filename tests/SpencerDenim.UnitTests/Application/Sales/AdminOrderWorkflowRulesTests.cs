using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Orders.Admin;
using SpencerDenim.Application.Sales.Orders.Contracts;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// The business rules of ADM-09 that live in code rather than in a database statement: which way the list
/// is sorted when nobody said, and what wording an order's status reads as.
/// </summary>
public sealed class AdminOrderWorkflowRulesTests
{
    /// <summary>
    /// <b>Doc 08 §4.1 G-09, as a test: <c>Cancelled</c> is never the System's.</b>
    /// </summary>
    /// <remarks>
    /// §4.1's own words for <c>PendingPayment → Cancelled</c> are "<c>Orders.Edit</c>; customer (own
    /// order). <b>Never the System</b> (G-09)" — a denied PayPal capture only shortens
    /// <c>PaymentDueAtUtc</c> and the order then reaches <b>Expired</b>, because doc 11 P10's rule is that
    /// Expired means nobody paid in time and Cancelled means a person decided. The reports rest on that
    /// distinction.
    /// <para>
    /// <b>This asserts the <i>shape</i> of the API rather than a behaviour</b>, which is the only place
    /// the rule can be made structural: <see cref="OrderCancelledBy"/> has two members and no
    /// <c>SystemJob</c>, so a future job has nothing to pass. A test of a cancel's <i>source</i> would
    /// pass against an enum that had grown a third member nobody had used yet.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_cancellation_can_only_be_a_customer_or_a_staff_member_and_never_the_system()
    {
        Assert.Equal(
            [OrderCancelledBy.Customer, OrderCancelledBy.Staff],
            Enum.GetValues<OrderCancelledBy>().OrderBy(member => member).ToArray());

        // And each one maps on to the history source of the same name, so the timeline says who. The
        // numeric values are asserted because OrderStatusHistory.Source is a persisted byte: a
        // renumbering would silently relabel every row ever written.
        Assert.Equal((int)OrderStatusChangeSource.Customer, (int)OrderCancelledBy.Customer);
        Assert.Equal((int)OrderStatusChangeSource.Staff, (int)OrderCancelledBy.Staff);
    }

    /// <summary>
    /// The refusal codes a client branches on are distinct, and none of them is a message.
    /// </summary>
    /// <remarks>
    /// Two codes with the same string would collapse two different remedies into one — "the order is
    /// paid, speak to us about a refund" and "a payment is being processed, try again shortly" — and the
    /// screens would then draw the wrong sentence for one of them. Snake-case lower is the convention
    /// every other refusal code in this project follows.
    /// </remarks>
    [Fact]
    public void The_cancel_refusal_codes_are_distinct_and_stable()
    {
        string[] codes =
        [
            CancelOrderRefusalCodes.WrongStatus,
            CancelOrderRefusalCodes.CaptureInProgress,
            CancelOrderRefusalCodes.PayPalPending,
            CancelOrderRefusalCodes.PaymentSettling,
        ];

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Matches("^[a-z][a-z_]*[a-z]$", code));
    }

    /// <summary>
    /// <b>Doc 11 P9 step 1, as a test.</b> "Opens <b>Orders</b>, filter <b>Paid</b> (oldest first)": the
    /// oldest paid order is the one a customer has been waiting longest for, and a newest-first list
    /// buries exactly the row that is about to cost a complaint.
    /// </summary>
    /// <remarks>
    /// The rule is on the server rather than on the screen, so a second client — a report, a mobile view —
    /// cannot forget it. This test is what would go red if somebody "simplified" the default to Newest for
    /// consistency with the other tabs.
    /// </remarks>
    [Fact]
    public void The_paid_filter_defaults_to_oldest_first()
    {
        Assert.Equal(
            AdminOrderSort.Oldest,
            GetAdminOrderListQueryHandler.DefaultSortFor(OrderStatus.Paid, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OrderStatus.PendingPayment)]
    [InlineData(OrderStatus.Processing)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Refunded)]
    public void Every_other_filter_defaults_to_newest_first(OrderStatus? status)
    {
        Assert.Equal(
            AdminOrderSort.Newest,
            GetAdminOrderListQueryHandler.DefaultSortFor(status, null));
    }

    /// <summary>
    /// An explicit request always wins, including on the Paid tab — so a staff member who wants the newest
    /// paid orders can have them, and the answer then says so.
    /// </summary>
    [Theory]
    [InlineData(AdminOrderSort.Newest)]
    [InlineData(AdminOrderSort.Oldest)]
    public void A_requested_sort_overrides_the_default(AdminOrderSort requested)
    {
        Assert.Equal(
            requested,
            GetAdminOrderListQueryHandler.DefaultSortFor(OrderStatus.Paid, requested));
        Assert.Equal(
            requested,
            GetAdminOrderListQueryHandler.DefaultSortFor(null, requested));
    }

    /// <summary>
    /// <b>Doc 11 P9's pickup variant, as a test.</b> <c>Shipped</c> "is shown to staff and customer as
    /// 'Ready for collection'", and a pickup order that has been handed over was collected rather than
    /// delivered.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the wording is rendered on the server: the admin list and the admin detail
    /// both show a status, and two copies of this conditional would be two chances for a pickup order to
    /// be described as shipped on one of them.
    /// </remarks>
    [Theory]
    [InlineData(OrderStatus.Shipped, "Ready for collection")]
    [InlineData(OrderStatus.Delivered, "Collected")]
    public void A_pickup_order_reads_differently_from_a_delivery_order(OrderStatus status, string expected)
    {
        Assert.Equal(expected, OrderStatusLabels.For(status, isPickup: true));
        Assert.NotEqual(expected, OrderStatusLabels.For(status, isPickup: false));
    }

    [Theory]
    [InlineData(OrderStatus.PendingPayment, "Awaiting payment")]
    [InlineData(OrderStatus.Paid, "Paid")]
    [InlineData(OrderStatus.Processing, "Processing")]
    [InlineData(OrderStatus.Shipped, "Shipped")]
    [InlineData(OrderStatus.Delivered, "Delivered")]
    [InlineData(OrderStatus.Cancelled, "Cancelled")]
    [InlineData(OrderStatus.Expired, "Expired")]
    [InlineData(OrderStatus.Refunded, "Refunded")]
    public void A_delivery_order_reads_as_doc_08_names_it(OrderStatus status, string expected)
    {
        Assert.Equal(expected, OrderStatusLabels.For(status, isPickup: false));
    }

    /// <summary>
    /// Every status has a label, for both kinds of order, and none of them is blank.
    /// </summary>
    /// <remarks>
    /// Written over the enum rather than over a list, so a status added by a later milestone fails here
    /// instead of rendering an empty pill on a live screen.
    /// </remarks>
    [Fact]
    public void Every_status_has_a_label_for_both_kinds_of_order()
    {
        foreach (var status in Enum.GetValues<OrderStatus>())
        {
            Assert.False(string.IsNullOrWhiteSpace(OrderStatusLabels.For(status, isPickup: false)));
            Assert.False(string.IsNullOrWhiteSpace(OrderStatusLabels.For(status, isPickup: true)));
        }
    }

    /// <summary>
    /// The three statuses a pickup order can be in before dispatch read the same either way.
    /// </summary>
    /// <remarks>
    /// <b>Asserted so the pickup branch cannot grow.</b> A well-meaning change that made "Paid" read as
    /// "Paid, to be collected" would put a second vocabulary on the screen for no gain, and the two
    /// statuses that genuinely differ are the two doc 11 P9 names.
    /// </remarks>
    [Theory]
    [InlineData(OrderStatus.PendingPayment)]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.Processing)]
    [InlineData(OrderStatus.Refunded)]
    public void Only_shipped_and_delivered_differ_for_a_pickup(OrderStatus status)
    {
        Assert.Equal(
            OrderStatusLabels.For(status, isPickup: false),
            OrderStatusLabels.For(status, isPickup: true));
    }

    /// <summary>
    /// The auto-deliver window is seeded with doc 08 §3.8's own figure, and the bounds refuse the one
    /// value that would be a defect.
    /// </summary>
    /// <remarks>
    /// <b>Zero is excluded deliberately.</b> "Mark it delivered the moment it ships" is exactly the false
    /// "Verified buyer" badge doc 08 §4.1 G-18 is about, so the minimum is one day rather than nought —
    /// and a staff member who wants same-day closure has the Mark as delivered button.
    /// </remarks>
    [Fact]
    public void The_auto_deliver_window_is_seven_days_and_cannot_be_zero()
    {
        Assert.Equal(7, SettingKeys.Orders.AutoDeliverAfterDaysDefault);
        Assert.Equal(1, SettingKeys.Orders.AutoDeliverAfterDaysMin);
        Assert.InRange(SettingKeys.Orders.AutoDeliverAfterDaysDefault, 1, SettingKeys.Orders.AutoDeliverAfterDaysMax);

        var definition = SettingCatalog.Find(SettingKeys.Orders.AutoDeliverAfterDays);

        Assert.NotNull(definition);
        Assert.Equal(SettingValueType.Int, definition.ValueType);
        Assert.Equal("7", definition.SeedValue);
    }

    /// <summary>
    /// <b>The four <c>Store.*</c> legal keys are seeded with nothing, and that is the point of them.</b>
    /// </summary>
    /// <remarks>
    /// This project has twice shipped invented client data as a Major finding. An invoice is a legal
    /// document: a seeded registered name or address would print as data on it, and nobody reading the
    /// printout could tell it from the real thing. The test is phrased over the whole catalogue so that a
    /// future key with a plausible-looking default fails here too — only a figure one of this project's
    /// own documents states may carry a seed value.
    /// </remarks>
    [Fact]
    public void Nothing_that_holds_a_client_fact_is_seeded_with_a_value()
    {
        var seeded = SettingCatalog.All
            .Where(definition => definition.SeedValue is not null)
            .Select(definition => definition.Key)
            .ToList();

        // The figures doc 08 §3.8 itself states - nothing a client knows. W4-01 added PayPal's defaults (the
        // environment, the buffer, the rate's age limit, the lock, the recovery window, the pending hold and
        // the payment window); its credentials, webhook id and exchange rate stay null.
        Assert.Equal(
            [
                SettingKeys.Orders.AutoDeliverAfterDays,
                SettingKeys.PayPal.Mode,
                SettingKeys.PayPal.FxBufferPercent,
                SettingKeys.PayPal.RateMaxAgeDays,
                SettingKeys.PayPal.CaptureLockHours,
                SettingKeys.PayPal.CaptureRecoveryMaxHours,
                SettingKeys.PayPal.PendingCaptureMaxDays,
                SettingKeys.PayPal.ExpiryMinutes,

                // Issue #193: doc 08 §3.8's own 90 and 7, the purge's retention periods. Not a client fact.
                SettingKeys.Traffic.RawRetentionDays,
                SettingKeys.Traffic.BotRetentionDays,
            ],
            seeded);

        foreach (var key in new[]
        {
            SettingKeys.PayPal.ClientId,
            SettingKeys.PayPal.ClientSecret,
            SettingKeys.PayPal.WebhookId,
            SettingKeys.PayPal.LkrPerUsd,
            SettingKeys.PayPal.RateUpdatedAtUtc,
        })
        {
            Assert.Null(SettingCatalog.Find(key)!.SeedValue);
        }

        foreach (var key in new[]
        {
            SettingKeys.Store.LegalName,
            SettingKeys.Store.Address,
            SettingKeys.Store.BRNumber,
            SettingKeys.Store.VatNumber,
        })
        {
            var definition = SettingCatalog.Find(key);

            Assert.NotNull(definition);
            Assert.Null(definition.SeedValue);
        }
    }

    /// <summary>
    /// An unset seller prints nothing, and <c>None</c> is the shape that makes that the default.
    /// </summary>
    [Fact]
    public void An_unset_seller_prints_nothing()
    {
        var none = InvoiceSellerDetails.None;

        Assert.Null(none.LegalName);
        Assert.Null(none.BRNumber);
        Assert.Null(none.VatNumber);
        Assert.Empty(none.AddressLines);
        Assert.False(none.IsComplete);
    }
}
