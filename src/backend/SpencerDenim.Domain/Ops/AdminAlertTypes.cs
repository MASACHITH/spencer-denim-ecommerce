namespace SpencerDenim.Domain.Ops;

/// <summary>
/// The <c>ops.AdminAlerts.AlertType</c> vocabulary (doc 08 §3.8, <c>varchar(40)</c>).
/// <para>
/// <b>All seven are declared and exactly one is written.</b> Only
/// <see cref="EmailDeliveryFailed"/> has a producer in this build (W4-02a, doc 11 §7 G-37); the other six
/// belong to the settings task (<see cref="SensitiveSettingsChanged"/>), the PayPal tasks
/// (<see cref="PaymentException"/>, <see cref="CaptureRecoveryFailed"/>,
/// <see cref="DashboardRefund"/>), the reconciliation job (<see cref="ReconciliationDrift"/>) and the
/// break-glass CLI (<see cref="BreakGlassUsed"/>). They are declared now so those tasks reuse these
/// exact strings instead of inventing new ones — the argument <c>AuditActions</c> already makes for the
/// audit vocabulary — and because <see cref="AdminAlertVisibility"/>'s rule has to be total over the
/// whole set, not over the one type that happens to exist.
/// </para>
/// <para>
/// <b>There is no check constraint on the column.</b> Doc 08 §3.8 marks a CK on <c>Severity</c> and not
/// on <c>AlertType</c>, and this list is the catalogue of valid values the same way
/// <c>EmailTemplateCodes</c> is for a template code.
/// </para>
/// </summary>
public static class AdminAlertTypes
{
    /// <summary><c>varchar(40)</c>, doc 08 §3.8.</summary>
    public const int MaxLength = 40;

    /// <summary>
    /// A bank, payments, SMTP, security or retention setting changed (doc 08 §3.8 DB2/N2, doc 11 P14).
    /// Critical, Super Admins only.
    /// </summary>
    public const string SensitiveSettingsChanged = "SensitiveSettingsChanged";

    /// <summary>
    /// A PayPal capture on a non-payable order, a duplicate capture or an amount mismatch (doc 08 §4.2,
    /// doc 11 P8/P10). Warning, and one of the two types <c>Orders.Approve</c> and
    /// <c>Orders.Refund</c> holders also see (R10).
    /// </summary>
    public const string PaymentException = "PaymentException";

    /// <summary>
    /// A capture stuck for <c>CaptureRecoveryMaxHours</c> with PayPal unreachable (doc 08 §4.2, doc 11
    /// P8). Warning, and the second type R10 widens beyond GrantsAll.
    /// </summary>
    public const string CaptureRecoveryFailed = "CaptureRecoveryFailed";

    /// <summary>
    /// A nightly stock or promo-usage mismatch that persists (doc 08 §5.9, doc 11 P2/P3). Warning,
    /// Super Admins only.
    /// </summary>
    public const string ReconciliationDrift = "ReconciliationDrift";

    /// <summary>
    /// The break-glass recovery CLI was used (doc 08 §3.1, doc 11 P14 step 16). Critical, Super Admins
    /// only.
    /// </summary>
    public const string BreakGlassUsed = "BreakGlassUsed";

    /// <summary>
    /// A refund was made in the PayPal dashboard rather than here, so stock may need adjusting by hand
    /// (doc 08 §4.3, doc 11 §7 G-23). Warning, Super Admins only.
    /// </summary>
    public const string DashboardRefund = "DashboardRefund";

    /// <summary>
    /// One or more <c>ops.EmailOutbox</c> rows reached status 4 <c>Dead</c> (doc 11 §4, §7 G-37).
    /// Warning, Super Admins only — <b>the one type this build raises</b>.
    /// </summary>
    public const string EmailDeliveryFailed = "EmailDeliveryFailed";

    /// <summary>Doc 08 §3.8's whole list, in the document's own order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        SensitiveSettingsChanged,
        PaymentException,
        CaptureRecoveryFailed,
        ReconciliationDrift,
        BreakGlassUsed,
        DashboardRefund,
        EmailDeliveryFailed,
    ];
}
