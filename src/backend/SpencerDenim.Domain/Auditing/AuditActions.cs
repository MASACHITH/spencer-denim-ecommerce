namespace SpencerDenim.Domain.Auditing;

/// <summary>
/// The fixed <c>ops.AuditLogs.Action</c> vocabulary (doc 08 §3.8, <c>varchar(30)</c>). Only the sign-in, password
/// and two-factor actions (<see cref="Login"/> … <see cref="RecoveryCodeUsed"/>) are written so far; the rest are
/// declared now so later features reuse these exact values instead of inventing new ones.
/// </summary>
public static class AuditActions
{
    public const string Create = "Create";
    public const string Update = "Update";
    public const string SoftDelete = "SoftDelete";
    public const string Restore = "Restore";
    public const string StatusChange = "StatusChange";
    public const string Approve = "Approve";
    public const string Refund = "Refund";
    public const string Export = "Export";
    public const string Login = "Login";
    public const string LoginFailed = "LoginFailed";
    public const string Lockout = "Lockout";
    public const string PermissionChange = "PermissionChange";
    public const string SettingChange = "SettingChange";
    public const string LinkGuestOrders = "LinkGuestOrders";
    public const string Anonymise = "Anonymise";
    public const string StaffDeactivated = "StaffDeactivated";
    public const string StaffReactivated = "StaffReactivated";
    public const string EmailChanged = "EmailChanged";
    public const string AdminPasswordReset = "AdminPasswordReset";
    public const string TwoFactorReset = "TwoFactorReset";
    public const string RoleAssigned = "RoleAssigned";
    public const string RoleUnassigned = "RoleUnassigned";
    public const string BreakGlass = "BreakGlass";
    public const string AlertAcknowledged = "AlertAcknowledged";

    /// <summary>
    /// A staff user changed their own password (issue #17, doc 11 P14 step 3 — the forced first-sign-in change
    /// included). Not in doc 08 §3.8's list yet: <see cref="AdminPasswordReset"/> there is the Super Admin
    /// resetting someone else's password, which is a different event with a different actor.
    /// </summary>
    public const string PasswordChanged = "PasswordChanged";

    /// <summary>A self-service password change refused because the current password was wrong (issue #17).</summary>
    public const string PasswordChangeFailed = "PasswordChangeFailed";

    /// <summary>
    /// A staff user confirmed their authenticator app and turned 2FA on (W1-10). Not in doc 08 §3.8's list yet;
    /// <see cref="TwoFactorReset"/> there is another user resetting it, a different event with a different actor.
    /// </summary>
    public const string TwoFactorEnabled = "TwoFactorEnabled";

    /// <summary>A staff user who is allowed to (not a GrantsAll holder) turned their own 2FA off (W1-10).</summary>
    public const string TwoFactorDisabled = "TwoFactorDisabled";

    /// <summary>A staff user replaced their recovery codes; the old ones stop working (W1-10).</summary>
    public const string RecoveryCodesRegenerated = "RecoveryCodesRegenerated";

    /// <summary>
    /// A refused 2FA step (W1-10): a wrong authenticator or recovery code at sign-in, or a wrong current password
    /// when regenerating recovery codes or turning 2FA off. The reason and step are in <c>AfterJson</c>; the code
    /// itself never is.
    /// </summary>
    public const string TwoFactorFailed = "TwoFactorFailed";

    /// <summary>A sign-in completed with a recovery code instead of the authenticator app; that code is now spent (W1-10).</summary>
    public const string RecoveryCodeUsed = "RecoveryCodeUsed";
}
