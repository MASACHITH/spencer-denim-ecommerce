namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// Why a Users &amp; Roles write was refused (doc 08 §3.1 DB4), so the API can return a precise, safe-to-show
/// message instead of a generic "forbidden" — the frontend maps each of these to copy like "not a subset of
/// your own permissions" (this task's UX requirement) without having to parse a free-text reason.
/// </summary>
public enum RbacDenialReason
{
    /// <summary>The actor holds neither GrantsAll nor <c>Users.Manage</c>: the endpoint's own permission
    /// policy already stops this in practice, but the handler still checks it has real data to reason from.</summary>
    NotAuthorized,

    /// <summary>DB4 rule 3: nobody may edit their own role assignments (or deactivate themselves).</summary>
    SelfEditNotAllowed,

    /// <summary>DB4 rule 2: a non-GrantsAll actor may never manage a user who holds (or would gain) GrantsAll.</summary>
    TargetIsOrWouldBecomeGrantsAll,

    /// <summary>DB4 rule 1/2 (subset rule, N3): the resulting permission set is not a subset of the actor's own.</summary>
    NotSubsetOfActorPermissions,

    /// <summary>DB4 rule 2: changing a staff user's email requires GrantsAll.</summary>
    EmailChangeRequiresGrantsAll,
}
