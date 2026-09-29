using Microsoft.AspNetCore.Authorization;

namespace SpencerDenim.Infrastructure.Identity.Authorization;

/// <summary>
/// The account-state checks every admin authorization policy carries on top of its own requirement: the
/// default policy (a bare <c>RequireAuthorization()</c>) and every <c>RequirePermission(...)</c> policy get them
/// from <see cref="IdentityRegistration"/>, so a new admin endpoint is gated without having to remember to be.
/// Today that is the forced password change (issue #17). The mandatory 2FA enrolment for GrantsAll holders
/// (doc 08 §3.1 N14, W1-10) is the next requirement to add here, with its own exemption metadata for the
/// enrolment endpoints.
/// </summary>
public static class AdminAccountGates
{
    public static IReadOnlyList<IAuthorizationRequirement> Requirements { get; } =
    [
        PasswordChangeCompletedRequirement.Instance,
    ];

    public static AuthorizationPolicyBuilder RequireAdminAccountGates(this AuthorizationPolicyBuilder policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return policy.AddRequirements([.. Requirements]);
    }
}
