namespace SpencerDenim.Domain.Ops;

/// <summary>
/// <c>ops.AdminAlerts.Severity</c> (doc 08 §3.8): "1 Info, 2 Warning, 3 Critical. CK".
/// <para>
/// <b>The numbers are the document's and the enum is one-based because of it.</b> A zero value would
/// break the check constraint on the first insert, which is the right direction for a mistake to fail,
/// but it also means <c>default(AdminAlertSeverity)</c> is not a valid severity — so a severity is always
/// chosen explicitly by whoever raises the alert, which is doc 11 §4.3's table and not this type's
/// business.
/// </para>
/// </summary>
public enum AdminAlertSeverity : byte
{
    /// <summary>Something a Super Admin should know. No alert type in doc 11 §4.3 uses it yet.</summary>
    Info = 1,

    /// <summary>
    /// Needs attention today. Doc 11 §4.3 gives this to <c>PaymentException</c>,
    /// <c>CaptureRecoveryFailed</c>, <c>ReconciliationDrift</c>, <c>DashboardRefund</c> and
    /// <c>EmailDeliveryFailed</c>.
    /// </summary>
    Warning = 2,

    /// <summary>
    /// A security event. Doc 11 §4.3 gives this to <c>SensitiveSettingsChanged</c> and
    /// <c>BreakGlassUsed</c>.
    /// </summary>
    Critical = 3,
}
