using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Application.Admin.Account;

/// <summary>
/// Answers "is my 2FA on, is it mandatory, and how many recovery codes are left" for the enrolment and security
/// pages (W1-10). Whether it is mandatory comes from the user's roles in the database (doc 08 §3.1 R4).
/// </summary>
public sealed class GetAdminTwoFactorStatusQueryHandler(IAdminTwoFactorManager twoFactorManager, IEffectivePermissionsProvider permissionsProvider)
    : IQueryHandler<GetAdminTwoFactorStatusQuery, AdminTwoFactorStatusResponse?>
{
    public async Task<AdminTwoFactorStatusResponse?> HandleAsync(GetAdminTwoFactorStatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var state = await twoFactorManager.GetStateAsync(query.UserId, cancellationToken);
        if (state is null)
        {
            return null;
        }

        var permissions = await permissionsProvider.GetEffectivePermissionsAsync(query.UserId, cancellationToken);

        return new AdminTwoFactorStatusResponse(
            state.Enabled,
            TwoFactorPolicy.IsRequired(permissions.GrantsAllPermissions),
            state.Enabled && TwoFactorPolicy.CanDisable(permissions.GrantsAllPermissions),
            state.RecoveryCodesLeft);
    }
}
