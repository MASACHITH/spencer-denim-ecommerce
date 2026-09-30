using Microsoft.AspNetCore.Authorization;

namespace SpencerDenim.Infrastructure.Identity.Authorization;

/// <summary>
/// The account-state checks every admin authorization policy carries on top of its own requirement: the
/// default policy (a bare <c>RequireAuthorization()</c>) and every <c>RequirePermission(...)</c> policy get them
/// from <see cref="IdentityRegistration"/>, so a new admin endpoint is gated without having to remember to be.
/// <list type="number">
/// <item><description>The forced password change (issue #17), which also refuses sessions older than the
/// account's security stamp.</description></item>
/// <item><description>The mandatory 2FA enrolment for GrantsAll holders (doc 08 §3.1 N14, W1-10).</description></item>
/// </list>
/// Both are always evaluated; when both refuse, the Api's authorization result handler reports the password change
/// first (doc 11 P14 step 3: "a user flagged must change password must change it first"). Each has its own
/// exemption metadata, and the enrolment endpoints are exempt only from the second.
/// </summary>
public static class AdminAccountGates
{
    public static IReadOnlyList<IAuthorizationRequirement> Requirements { get; } =
    [
        PasswordChangeCompletedRequirement.Instance,
        TwoFactorEnrolmentCompletedRequirement.Instance,
    ];

    public static AuthorizationPolicyBuilder RequireAdminAccountGates(this AuthorizationPolicyBuilder policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return policy.AddRequirements([.. Requirements]);
    }
}
