using Microsoft.AspNetCore.Authorization;

namespace SpencerDenim.Infrastructure.Identity.Authorization;

/// <summary>
/// "This staff account has no pending forced password change" (doc 08 §9, doc 11 P14 step 3). Part of every
/// admin authorization policy through <see cref="AdminAccountGates"/>, and checked against the database on every
/// request by <see cref="PasswordChangeCompletedHandler"/>, so clearing the flag takes effect on the very next
/// request and setting it (a Super Admin reset, the break-glass CLI) takes effect just as fast.
/// </summary>
public sealed class PasswordChangeCompletedRequirement : IAuthorizationRequirement
{
    public static readonly PasswordChangeCompletedRequirement Instance = new();

    private PasswordChangeCompletedRequirement()
    {
    }
}

/// <summary>
/// Endpoint metadata that exempts an endpoint from <see cref="PasswordChangeCompletedRequirement"/>: only the
/// endpoints a user needs to get through the forced change carry it (who-am-I, change-password, logout). Added
/// with <c>AllowWhilePasswordChangeRequired()</c> in the Api project.
/// </summary>
public sealed class AllowWhilePasswordChangeRequiredMetadata
{
    public static readonly AllowWhilePasswordChangeRequiredMetadata Instance = new();

    private AllowWhilePasswordChangeRequiredMetadata()
    {
    }
}

/// <summary>
/// Why <see cref="PasswordChangeCompletedHandler"/> refused a request. The Api's authorization result handler
/// looks for this reason to answer 403 with the <see cref="Code"/> problem-details code instead of a bare 403,
/// so the admin app can tell "change your password first" apart from "you lack that permission".
/// </summary>
public sealed class PasswordChangeRequiredFailureReason(IAuthorizationHandler handler)
    : AuthorizationFailureReason(handler, "The password must be changed before any other admin action.")
{
    public const string Code = "password_change_required";
}
