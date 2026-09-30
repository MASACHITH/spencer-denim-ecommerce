using Microsoft.AspNetCore.Authorization;

namespace SpencerDenim.Infrastructure.Identity.Authorization;

/// <summary>
/// "This staff account is not a GrantsAll holder without 2FA" (doc 08 §3.1 N14: "until enrolled, such a user can
/// reach only the 2FA enrolment page"; doc 11 P14 step 3; W1-10). Part of every admin authorization policy through
/// <see cref="AdminAccountGates"/>, and checked against the database on every request by
/// <see cref="TwoFactorEnrolmentCompletedHandler"/>, so enrolling takes effect on the very next request, and so
/// does being given the Super Admin role without 2FA.
/// </summary>
public sealed class TwoFactorEnrolmentCompletedRequirement : IAuthorizationRequirement
{
    public static readonly TwoFactorEnrolmentCompletedRequirement Instance = new();

    private TwoFactorEnrolmentCompletedRequirement()
    {
    }
}

/// <summary>
/// Endpoint metadata that exempts an endpoint from <see cref="TwoFactorEnrolmentCompletedRequirement"/>: only the
/// endpoints a user needs to get through enrolment carry it (who-am-I, logout, change-password, and the 2FA status,
/// setup and enable endpoints). Added with <c>AllowWhileTwoFactorEnrolmentRequired()</c> in the Api project. It
/// does not exempt the endpoint from the forced password change, which comes first.
/// </summary>
public sealed class AllowWhileTwoFactorEnrolmentRequiredMetadata
{
    public static readonly AllowWhileTwoFactorEnrolmentRequiredMetadata Instance = new();

    private AllowWhileTwoFactorEnrolmentRequiredMetadata()
    {
    }
}

/// <summary>
/// Why <see cref="TwoFactorEnrolmentCompletedHandler"/> refused a request. The Api's authorization result handler
/// looks for this reason to answer 403 with the <see cref="Code"/> problem-details code, so the admin app can send
/// the user to the enrolment page instead of showing a generic "not allowed".
/// </summary>
public sealed class TwoFactorEnrolmentRequiredFailureReason(IAuthorizationHandler handler)
    : AuthorizationFailureReason(handler, "Two-factor authentication must be set up before any other admin action.")
{
    public const string Code = "two_factor_enrolment_required";
}
