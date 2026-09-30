namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// Turns a failed <see cref="Domain.Identity.RoleAssignmentPolicy"/> check into a
/// <see cref="RbacDenialReason"/> the API can map to a specific, safe-to-show message, instead of one
/// generic "forbidden" for every command handler in this feature (doc 08 §3.1 DB4).
/// </summary>
internal static class RbacDenialClassifier
{
    public static RbacDenialReason Classify(
        bool actorHasGrantsAll,
        bool actorHasUsersManage,
        bool targetHasGrantsAllBefore,
        bool targetHasGrantsAllAfter)
    {
        if (!actorHasGrantsAll && (targetHasGrantsAllBefore || targetHasGrantsAllAfter))
        {
            return RbacDenialReason.TargetIsOrWouldBecomeGrantsAll;
        }

        if (!actorHasGrantsAll && !actorHasUsersManage)
        {
            return RbacDenialReason.NotAuthorized;
        }

        return RbacDenialReason.NotSubsetOfActorPermissions;
    }

    public static string Message(RbacDenialReason reason) => reason switch
    {
        RbacDenialReason.TargetIsOrWouldBecomeGrantsAll => "Only a Super Admin can grant, or manage a user who holds, the Super Admin role.",
        RbacDenialReason.NotAuthorized => "You do not have permission to manage staff users.",
        RbacDenialReason.SelfEditNotAllowed => "You cannot change your own role assignments.",
        RbacDenialReason.EmailChangeRequiresGrantsAll => "Only a Super Admin can change a staff user's email address.",
        RbacDenialReason.NotSubsetOfActorPermissions or _ => "That action grants or affects permissions you do not hold yourself.",
    };
}
