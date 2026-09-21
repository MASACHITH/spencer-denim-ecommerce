namespace SpencerDenim.Domain.Auditing;

/// <summary>
/// The fixed <c>ops.AuditLogs.Action</c> vocabulary (doc 08 §3.8, <c>varchar(30)</c>). Only <see cref="Login"/>,
/// <see cref="LoginFailed"/> and <see cref="Lockout"/> are written by this slice; the rest are declared now so
/// later features reuse these exact values instead of inventing new ones.
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
}
