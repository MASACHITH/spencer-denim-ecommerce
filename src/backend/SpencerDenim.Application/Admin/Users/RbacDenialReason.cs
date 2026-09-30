namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// Why a Users &amp; Roles write was refused (doc 08 §3.1 DB4), so the API can return a precise, safe-to-show
/// message instead of a generic "forbidden" — the frontend maps each of these to copy like "not a subset of
/// your own permissions" (this task's UX requirement) without having to parse a free-text reason.
/// <para>
/// Tester-round-1 fix (W1-08, #8, defect 1): there is deliberately no separate "target is/would become
/// GrantsAll" value here. Doc 08 §3.1 says "GrantsAll holders are never a subset, so they are protected" —
/// that case is just one more way <see cref="NotSubsetOfActorPermissions"/> can be true, and it used to be
/// its own enum value with its own message, which let a non-GrantsAll <c>Users.Manage</c> actor tell "target
/// merely has an extra permission" apart from "target IS the Super Admin" by the shape of the 403 alone — an
/// identity oracle, the same enumeration-bug class as the W1-01 login-timing fix. Do not reintroduce a
/// distinguishable reason for it; if a richer distinction is ever needed again, keep it out of anything that
/// reaches the HTTP response (logs/telemetry only).
/// </para>
/// </summary>
public enum RbacDenialReason
{
    /// <summary>The actor holds neither GrantsAll nor <c>Users.Manage</c>: the endpoint's own permission
    /// policy already stops this in practice, but the handler still checks it has real data to reason from.</summary>
    NotAuthorized,

    /// <summary>DB4 rule 3: nobody may edit their own role assignments (or deactivate themselves).</summary>
    SelfEditNotAllowed,

    /// <summary>
    /// DB4 rule 1/2 (subset rule, N3): the resulting permission set is not a subset of the actor's own. This
    /// is also the reason surfaced when the target holds, or would gain, GrantsAll (see the type remarks) —
    /// and, since tester-round-1 (defect 1), the reason surfaced to a non-GrantsAll actor for a target that
    /// does not exist at all, so "doesn't exist" is not itself distinguishable from "exists but protected".
    /// </summary>
    NotSubsetOfActorPermissions,

    /// <summary>DB4 rule 2: changing a staff user's email requires GrantsAll.</summary>
    EmailChangeRequiresGrantsAll,
}
