namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// Turns a failed <see cref="Domain.Identity.RoleAssignmentPolicy"/> check into a
/// <see cref="RbacDenialReason"/> the API can map to a specific, safe-to-show message, instead of one
/// generic "forbidden" for every command handler in this feature (doc 08 §3.1 DB4).
/// <para>
/// Tester-round-1 fix (W1-08, #8, defect 1): a target that holds, or would gain, GrantsAll is deliberately
/// classified the same as any other over-permissioned target (<see cref="RbacDenialReason.NotSubsetOfActorPermissions"/>)
/// instead of its own distinct reason — see the remarks on <see cref="RbacDenialReason"/> for why a separate
/// value there was an identity oracle. Callers that need to tell the two apart for their own internal
/// logging/telemetry should compare the raw booleans themselves; this classifier's output is specifically the
/// thing that reaches the HTTP response, so it must never carry that distinction.
/// </para>
/// </summary>
internal static class RbacDenialClassifier
{
    public static RbacDenialReason Classify(
        bool actorHasGrantsAll,
        bool actorHasUsersManage,
        bool targetHasGrantsAllBefore,
        bool targetHasGrantsAllAfter)
    {
        if (!actorHasGrantsAll && !actorHasUsersManage)
        {
            return RbacDenialReason.NotAuthorized;
        }

        // targetHasGrantsAllBefore/After folds into the same generic subset-denial reason as any other
        // over-permissioned target: doc 08 §3.1 already says GrantsAll targets are "never a subset", so this
        // is not a special case, and it must not be a *distinguishable* one in the API response (defect 1).
        return RbacDenialReason.NotSubsetOfActorPermissions;
    }

    public static string Message(RbacDenialReason reason) => reason switch
    {
        RbacDenialReason.NotAuthorized => "You do not have permission to manage staff users.",
        RbacDenialReason.SelfEditNotAllowed => "You cannot change your own role assignments.",
        RbacDenialReason.EmailChangeRequiresGrantsAll => "Only a Super Admin can change a staff user's email address.",
        RbacDenialReason.NotSubsetOfActorPermissions or _ => "That action grants or affects permissions you do not hold yourself.",
    };
}
