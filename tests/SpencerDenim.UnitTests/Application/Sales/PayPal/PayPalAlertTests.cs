using System.Text.Json;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Application.Settings;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.UnitTests.Application.Sales.PayPal;

/// <summary>
/// The <c>CaptureRecoveryFailed</c> alert (doc 08 §4.2 step 5, R10): a capture PayPal could not be reached for
/// within the recovery window. Pure, so the whole payload is asserted - including what it must not contain.
/// </summary>
public sealed class CaptureRecoveryFailedAlertTests
{
    private static readonly Guid OrderPublicId = Guid.Parse("3f2b8c1e-5d4a-4e7b-9c0d-1a2b3c4d5e6f");
    private static readonly DateTime Now = new(2026, 10, 10, 6, 30, 0, DateTimeKind.Utc);

    private static CaptureRecoveryFailedFacts Facts() => new(481, OrderPublicId, "CW-2026-000123", 24);

    [Fact]
    public void It_is_a_warning_CaptureRecoveryFailed_about_the_order()
    {
        var alert = CaptureRecoveryFailedAlert.For(Facts(), Now);

        // Doc 11 §4.3: Warning; and the type is one of the two R10 shows to Orders.Approve / Orders.Refund holders.
        Assert.Equal(AdminAlertTypes.CaptureRecoveryFailed, alert.AlertType);
        Assert.Equal(AdminAlertSeverity.Warning, alert.Severity);
        Assert.Contains(alert.AlertType, AdminAlertVisibility.OrderStaffTypes);

        Assert.Equal(AdminAlert.OrderEntityType, alert.RelatedEntityType);
        Assert.Equal("3f2b8c1e-5d4a-4e7b-9c0d-1a2b3c4d5e6f", alert.RelatedEntityId);
        Assert.Equal(Now, alert.CreatedAtUtc);
        Assert.Contains("CW-2026-000123", alert.Title, StringComparison.Ordinal);
        Assert.Contains("24 hours", alert.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void The_details_hold_ids_and_the_reason_and_nothing_about_the_customer()
    {
        var alert = CaptureRecoveryFailedAlert.For(Facts(), Now);

        using var details = JsonDocument.Parse(alert.DetailsJson!);
        var root = details.RootElement;

        Assert.Equal(481, root.GetProperty("paymentId").GetInt32());
        Assert.Equal("PayPal", root.GetProperty("provider").GetString());
        Assert.Equal("RECOVERY_TIMEOUT", root.GetProperty("reason").GetString());
        Assert.Equal("CW-2026-000123", root.GetProperty("orderNumber").GetString());
        Assert.Equal(24, root.GetProperty("recoveryHours").GetInt32());

        // Doc 08 §3.8: "keys, entity IDs and amounts" - and §3.1 step 8's PDPA test searches this table.
        Assert.DoesNotContain("@", alert.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("email", alert.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_long_order_number_cannot_overflow_the_title_column()
    {
        var alert = CaptureRecoveryFailedAlert.For(
            new CaptureRecoveryFailedFacts(1, OrderPublicId, new string('X', 400), 24),
            Now);

        Assert.True(alert.Title.Length <= AdminAlert.TitleMaxLength);
    }
}

/// <summary>
/// The <c>SensitiveSettingsChanged</c> alert (doc 08 §3.8 DB2): key names and a staff user's id, never a value.
/// </summary>
public sealed class SensitiveSettingsChangedAlertTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 6, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void It_is_a_critical_alert_for_the_super_admins()
    {
        var alert = SensitiveSettingsChangedAlert.For(
            "PayPal payment settings",
            ["Payments.PayPal.ClientSecret", "Payments.PayPal.Mode"],
            actingUserId: 7,
            Now);

        Assert.Equal(AdminAlertTypes.SensitiveSettingsChanged, alert.AlertType);
        Assert.Equal(AdminAlertSeverity.Critical, alert.Severity);
        Assert.Equal(Now, alert.CreatedAtUtc);
        Assert.Equal("PayPal payment settings were changed (2 settings)", alert.Title);

        // R10: only GrantsAll sees it.
        Assert.False(AdminAlertVisibility.CanSee(alert.AlertType, grantsAllPermissions: false, ["Orders.Approve", "Orders.Refund"]));
        Assert.True(AdminAlertVisibility.CanSee(alert.AlertType, grantsAllPermissions: true, []));
    }

    [Fact]
    public void The_details_are_the_key_names_and_the_user_id_in_a_fixed_order()
    {
        var alert = SensitiveSettingsChangedAlert.For(
            "PayPal payment settings",
            ["Payments.PayPal.ClientSecret"],
            actingUserId: 7,
            Now);

        Assert.Equal(
            """{"keys":["Payments.PayPal.ClientSecret"],"changedByUserId":7}""",
            alert.DetailsJson);
        Assert.Equal("PayPal payment settings were changed (1 setting)", alert.Title);
    }

    [Fact]
    public void A_job_with_no_user_leaves_the_user_out()
    {
        var alert = SensitiveSettingsChangedAlert.For("PayPal payment settings", ["Payments.PayPal.Mode"], null, Now);

        Assert.Equal("""{"keys":["Payments.PayPal.Mode"]}""", alert.DetailsJson);
    }

    [Fact]
    public void It_relates_to_no_order_and_carries_no_value()
    {
        var alert = SensitiveSettingsChangedAlert.For("PayPal payment settings", ["Payments.PayPal.ClientSecret"], 7, Now);

        Assert.Null(alert.RelatedEntityType);
        Assert.Null(alert.RelatedEntityId);
        Assert.Null(alert.AuditLogId);
    }

    [Fact]
    public void A_missing_name_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => SensitiveSettingsChangedAlert.For(" ", ["k"], 1, Now));
    }
}

/// <summary>
/// When a failed payment event is tried again and when the job stops (doc 08 §3.5, §3.8 <c>RetryPaymentEvents</c>).
/// </summary>
public sealed class PaymentEventRetryPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 6, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(5, 240)]
    [InlineData(6, 720)]
    [InlineData(9, 720)]
    public void The_gap_grows_with_each_failure(int attempts, int minutes)
    {
        Assert.Equal(Now.AddMinutes(minutes), PaymentEventRetryPolicy.NextAttemptAtUtc(attempts, Now));
    }

    [Fact]
    public void After_ten_attempts_the_job_stops_and_a_person_has_to_look()
    {
        Assert.NotNull(PaymentEventRetryPolicy.NextAttemptAtUtc(PaymentEventRetryPolicy.MaxAttempts - 1, Now));
        Assert.Null(PaymentEventRetryPolicy.NextAttemptAtUtc(PaymentEventRetryPolicy.MaxAttempts, Now));
        Assert.Null(PaymentEventRetryPolicy.NextAttemptAtUtc(25, Now));
    }

    [Fact]
    public void A_nonsense_count_still_gives_the_first_gap()
    {
        Assert.Equal(Now.AddMinutes(1), PaymentEventRetryPolicy.NextAttemptAtUtc(0, Now));
        Assert.Equal(Now.AddMinutes(1), PaymentEventRetryPolicy.NextAttemptAtUtc(-4, Now));
    }

    /// <summary>
    /// The schedule covers roughly three days, which is how long PayPal itself keeps redelivering - so a
    /// failure that outlasts it is one for a person, not for another retry.
    /// </summary>
    [Fact]
    public void The_whole_schedule_spans_about_three_days()
    {
        var total = TimeSpan.Zero;

        for (var attempt = 1; attempt < PaymentEventRetryPolicy.MaxAttempts; attempt++)
        {
            total += PaymentEventRetryPolicy.NextAttemptAtUtc(attempt, Now)!.Value - Now;
        }

        Assert.InRange(total.TotalHours, 48, 96);
    }
}
