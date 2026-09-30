namespace SpencerDenim.Domain.Identity;

/// <summary>
/// Who must use two-factor authentication (doc 08 §3.1 N14, D14; doc 02 §G; doc 11 P14 step 3). Every staff user
/// may turn it on; for a GrantsAll (Super Admin) holder it is mandatory and "always on, not a setting" (doc 08
/// §3.8 <c>Security</c> group). Doc 11 gap G-28 (also require it for <c>Orders.Refund</c> / <c>Users.Manage</c>
/// holders) is only a recommendation so far, so it is deliberately not applied here.
/// </summary>
public static class TwoFactorPolicy
{
    /// <summary>True when a user holding these permissions must have 2FA enabled before using the admin panel.</summary>
    public static bool IsRequired(bool grantsAllPermissions) => grantsAllPermissions;

    /// <summary>
    /// True when the user may switch 2FA off themselves. A GrantsAll holder never may: losing the phone is handled
    /// by another Super Admin's reset or the break-glass CLI (doc 11 P14 step 16).
    /// </summary>
    public static bool CanDisable(bool grantsAllPermissions) => !IsRequired(grantsAllPermissions);
}
