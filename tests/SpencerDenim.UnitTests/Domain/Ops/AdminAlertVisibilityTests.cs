using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.UnitTests.Domain.Ops;

/// <summary>
/// Doc 08 §3.8 <b>R10</b>: who sees an admin alert, and who may acknowledge it.
/// <para>
/// <b>This is the security rule of the whole feature, so it is pinned type by type rather than by
/// sampling.</b> A table of alerts includes <c>SensitiveSettingsChanged</c> and <c>BreakGlassUsed</c> —
/// "a bank setting was changed" and "somebody used the recovery CLI" — so a rule that was one type too
/// wide would show a Store Manager the security events the Super Admin accounts exist to keep separate.
/// </para>
/// <para>
/// <b>For <c>EmailDeliveryFailed</c> the answer is Super Admins only</b>, and doc 11 §7 G-37 says so in
/// as many words: "the visibility rule in 08 §3.8 R10 gives no other role a path to it". Whether a Store
/// Manager should see it too is still open with the Architect and would need a doc 08 change. The test
/// below is written to fail if somebody widens it without that change.
/// </para>
/// </summary>
public sealed class AdminAlertVisibilityTests
{
    private static readonly string[] None = [];

    private static readonly string[] OrdersApprove = [Permissions.Orders.Approve];

    private static readonly string[] OrdersRefund = [Permissions.Orders.Refund];

    /// <summary>A Store Manager's grid, as doc 08 §9 describes it: every Module.Action, no GrantsAll.</summary>
    private static readonly string[] StoreManagerish =
    [
        Permissions.Orders.View,
        Permissions.Orders.Edit,
        Permissions.Settings.View,
        Permissions.Settings.Edit,
        Permissions.Reports.View,
        Permissions.Users.View,
        Permissions.AuditLog.View,
    ];

    [Fact]
    public void A_grantsAll_holder_sees_every_type()
    {
        foreach (var type in AdminAlertTypes.All)
        {
            Assert.True(AdminAlertVisibility.CanSee(type, grantsAllPermissions: true, None));
        }

        Assert.Equal(AdminAlertTypes.All, AdminAlertVisibility.VisibleTypes(true, None));
    }

    /// <summary>
    /// An <c>Orders.Approve</c> or <c>Orders.Refund</c> holder sees exactly the two payment types, and
    /// R10 names no third.
    /// </summary>
    [Theory]
    [MemberData(nameof(OrderStaffPermissionSets))]
    public void Order_staff_see_the_two_payment_types_and_no_others(string[] permissions)
    {
        Assert.True(AdminAlertVisibility.CanSee(AdminAlertTypes.PaymentException, false, permissions));
        Assert.True(AdminAlertVisibility.CanSee(AdminAlertTypes.CaptureRecoveryFailed, false, permissions));

        Assert.False(AdminAlertVisibility.CanSee(AdminAlertTypes.EmailDeliveryFailed, false, permissions));
        Assert.False(AdminAlertVisibility.CanSee(AdminAlertTypes.SensitiveSettingsChanged, false, permissions));
        Assert.False(AdminAlertVisibility.CanSee(AdminAlertTypes.BreakGlassUsed, false, permissions));
        Assert.False(AdminAlertVisibility.CanSee(AdminAlertTypes.ReconciliationDrift, false, permissions));
        Assert.False(AdminAlertVisibility.CanSee(AdminAlertTypes.DashboardRefund, false, permissions));

        Assert.Equal(
            [AdminAlertTypes.PaymentException, AdminAlertTypes.CaptureRecoveryFailed],
            AdminAlertVisibility.VisibleTypes(false, permissions));
    }

    public static TheoryData<string[]> OrderStaffPermissionSets()
    {
        var data = new TheoryData<string[]>();
        data.Add(OrdersApprove);
        data.Add(OrdersRefund);
        data.Add([.. OrdersApprove, .. OrdersRefund]);
        return data;
    }

    /// <summary>
    /// <b>A dead email is a Super Admin's, and only a Super Admin's</b> (doc 11 §7 G-37).
    /// </summary>
    /// <remarks>
    /// Written as its own test rather than as one clause above, because it is the open question: G-37
    /// records "whether the Store Manager should see it too" as still open with the Architect. Widening
    /// the rule without that decision makes this test red, which is the point of it.
    /// </remarks>
    [Fact]
    public void A_dead_email_is_visible_to_grantsAll_holders_and_to_nobody_else()
    {
        Assert.True(AdminAlertVisibility.CanSee(AdminAlertTypes.EmailDeliveryFailed, true, None));

        Assert.False(AdminAlertVisibility.CanSee(AdminAlertTypes.EmailDeliveryFailed, false, StoreManagerish));
        Assert.False(AdminAlertVisibility.CanSee(AdminAlertTypes.EmailDeliveryFailed, false, OrdersApprove));
        Assert.False(AdminAlertVisibility.CanSee(AdminAlertTypes.EmailDeliveryFailed, false, OrdersRefund));
        Assert.False(AdminAlertVisibility.CanSee(AdminAlertTypes.EmailDeliveryFailed, false, None));
    }

    /// <summary>
    /// Everybody else sees nothing at all — which is why the endpoint answers an empty list rather than a
    /// refusal for a staff member with a session and no alert audience.
    /// </summary>
    [Fact]
    public void Everybody_else_sees_nothing()
    {
        Assert.Empty(AdminAlertVisibility.VisibleTypes(false, StoreManagerish));
        Assert.Empty(AdminAlertVisibility.VisibleTypes(false, None));

        foreach (var type in AdminAlertTypes.All)
        {
            Assert.False(AdminAlertVisibility.CanSee(type, false, StoreManagerish));
        }
    }

    /// <summary>
    /// <see cref="Permissions.Orders.View"/> alone is <b>not</b> an alert audience.
    /// </summary>
    /// <remarks>
    /// R10 names Approve and Refund, and doc 11 §4.3's table agrees. It matters: doc 08 §3.1 never grants
    /// Order Staff <c>Orders.Refund</c>, so a rule keyed on "can see orders" would hand the payment
    /// exceptions to anybody who can read the order list.
    /// </remarks>
    [Fact]
    public void Being_able_to_read_orders_is_not_an_alert_audience()
    {
        Assert.False(AdminAlertVisibility.CanSee(
            AdminAlertTypes.PaymentException,
            false,
            [Permissions.Orders.View, Permissions.Orders.Edit]));
    }

    /// <summary>
    /// An unknown alert type is visible to a GrantsAll holder and to nobody else.
    /// </summary>
    /// <remarks>
    /// There is no check constraint on <c>AlertType</c> (doc 08 §3.8 marks a CK on <c>Severity</c> only),
    /// so a row carrying a type this build has never heard of is reachable — from a later migration, or
    /// from a task that adds a type without adding it to <see cref="AdminAlertTypes"/>. It must default
    /// <i>closed</i> for everybody except the holder R10 gives everything to, because the alternative is
    /// a new alert type whose first reader is whoever happens to hold <c>Orders.Approve</c>.
    /// </remarks>
    [Fact]
    public void An_unknown_alert_type_defaults_to_super_admins_only()
    {
        Assert.True(AdminAlertVisibility.CanSee("SomethingLaterTasksInvent", true, None));
        Assert.False(AdminAlertVisibility.CanSee("SomethingLaterTasksInvent", false, OrdersApprove));
    }

    /// <summary>
    /// The two lists this rule is written from are the documents' own.
    /// </summary>
    [Fact]
    public void The_rule_is_written_from_doc_08_R10s_two_lists()
    {
        Assert.Equal(
            [AdminAlertTypes.PaymentException, AdminAlertTypes.CaptureRecoveryFailed],
            AdminAlertVisibility.OrderStaffTypes);

        Assert.Equal(
            [Permissions.Orders.Approve, Permissions.Orders.Refund],
            AdminAlertVisibility.OrderStaffPermissions);

        // Doc 08 §3.8's AlertType list, in the document's own order. A type added to the catalogue without
        // a visibility decision makes this red, which is the hand-off R10 needs.
        Assert.Equal(
            [
                "SensitiveSettingsChanged",
                "PaymentException",
                "CaptureRecoveryFailed",
                "ReconciliationDrift",
                "BreakGlassUsed",
                "DashboardRefund",
                "EmailDeliveryFailed",
            ],
            AdminAlertTypes.All);
    }
}
