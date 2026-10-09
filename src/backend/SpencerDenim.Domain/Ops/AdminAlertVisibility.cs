using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Domain.Ops;

/// <summary>
/// Doc 08 §3.8 <b>R10</b>, the one rule that decides who sees an admin alert and who may acknowledge it:
/// "GrantsAll holders see and acknowledge every alert. <c>Orders.Approve</c> and <c>Orders.Refund</c>
/// holders also see and acknowledge <c>PaymentException</c> and <c>CaptureRecoveryFailed</c> alerts."
/// <para>
/// <b>Seeing and acknowledging are the same rule, deliberately.</b> Doc 08 writes them as one sentence
/// and doc 11 §6.1 step 1 reads from it: a staff member who can open a banner is the staff member who
/// clears it. So there is one predicate here and not two, and an endpoint cannot accidentally let
/// somebody acknowledge a row they were never shown.
/// </para>
/// <para>
/// <b>For <see cref="AdminAlertTypes.EmailDeliveryFailed"/> this means Super Admins only</b> (doc 11 §7
/// G-37: "the visibility rule in 08 §3.8 R10 gives no other role a path to it"). Whether a Store Manager
/// should see it too is <b>still open with the Architect</b> and would need a doc 08 change; it is not
/// widened here.
/// </para>
/// <para>
/// <b>Primitives rather than <c>EffectivePermissions</c></b> (which lives in the Application layer, and
/// whose <c>Has</c> cannot tell a GrantsAll holder from an <c>Orders.Approve</c> holder — it answers true
/// for both). The distinction is the whole rule, so this takes the two facts separately.
/// </para>
/// </summary>
public static class AdminAlertVisibility
{
    /// <summary>
    /// The alert types R10 shows to a holder of <see cref="Permissions.Orders.Approve"/> or
    /// <see cref="Permissions.Orders.Refund"/> who is not a GrantsAll holder.
    /// </summary>
    /// <remarks>
    /// Doc 11 §4.3's table agrees type for type: these two are the only rows whose "visible to" column
    /// names anything besides Super Admins.
    /// </remarks>
    public static IReadOnlyList<string> OrderStaffTypes { get; } =
    [
        AdminAlertTypes.PaymentException,
        AdminAlertTypes.CaptureRecoveryFailed,
    ];

    /// <summary>
    /// The permissions that, short of GrantsAll, open <see cref="OrderStaffTypes"/>.
    /// </summary>
    public static IReadOnlyList<string> OrderStaffPermissions { get; } =
    [
        Permissions.Orders.Approve,
        Permissions.Orders.Refund,
    ];

    /// <summary>
    /// Whether this user may see — and therefore acknowledge — alerts of <paramref name="alertType"/>.
    /// </summary>
    /// <param name="grantsAllPermissions">
    /// True when one of the user's roles is the GrantsAll role (doc 08 §3.1 R4). Not the same thing as
    /// holding every permission: it is the flag R10 names.
    /// </param>
    /// <param name="permissions">
    /// The user's individual grants. Read only when <paramref name="grantsAllPermissions"/> is false.
    /// </param>
    public static bool CanSee(
        string alertType,
        bool grantsAllPermissions,
        IReadOnlyCollection<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        if (grantsAllPermissions)
        {
            return true;
        }

        return OrderStaffTypes.Contains(alertType, StringComparer.Ordinal)
            && OrderStaffPermissions.Any(permission => permissions.Contains(permission, StringComparer.Ordinal));
    }

    /// <summary>
    /// Every type this user may see, or an empty list for a user R10 gives no path at all — which is most
    /// staff, and is why the alerts endpoint answers an empty list rather than a refusal.
    /// </summary>
    public static IReadOnlyList<string> VisibleTypes(
        bool grantsAllPermissions,
        IReadOnlyCollection<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        if (grantsAllPermissions)
        {
            return AdminAlertTypes.All;
        }

        return OrderStaffPermissions.Any(permission => permissions.Contains(permission, StringComparer.Ordinal))
            ? OrderStaffTypes
            : [];
    }
}
