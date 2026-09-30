using System.Globalization;
using Microsoft.AspNetCore.Identity;
using SpencerDenim.Application.Admin.Account;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Changes a staff user's own password (issue #17) through <see cref="UserManager{TUser}"/>, which rehashes the
/// password and rotates the <c>SecurityStamp</c> in the same update that clears <c>MustChangePassword</c>.
/// </summary>
public sealed class AdminPasswordChanger(UserManager<AppUser> userManager, SignInManager<AppUser> signInManager) : IAdminPasswordChanger
{
    public async Task<AdminPasswordChangeOutcome> ChangePasswordAsync(
        int userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);

        // UserManager methods take no CancellationToken; checking once up front matches AdminAuthenticator.
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
        if (user is null || user.UserType != UserType.Staff || !user.IsActive)
        {
            return AdminPasswordChangeOutcome.AccountUnavailable(userId);
        }

        var wasAlreadyLockedOut = await userManager.IsLockedOutAsync(user);

        // The same call and the same lockoutOnFailure: true as sign-in, so a stolen session cookie cannot be used
        // to guess the current password any faster than the sign-in form allows (doc 08 §3.1 N14): failures here
        // count towards the same time-boxed lockout, and an already locked account is refused without a check.
        var checkResult = await signInManager.CheckPasswordSignInAsync(user, currentPassword, lockoutOnFailure: true);
        if (checkResult.IsLockedOut)
        {
            var lockoutEnd = user.LockoutEnd ?? DateTimeOffset.UtcNow;
            return AdminPasswordChangeOutcome.LockedOut(user.Id, user.Email, lockoutEnd, wasNewlyLockedOut: !wasAlreadyLockedOut);
        }

        if (!checkResult.Succeeded)
        {
            return AdminPasswordChangeOutcome.InvalidCurrentPassword(user.Id, user.Email);
        }

        // Validated before anything on the tracked user is touched. The audit writer shares this request's
        // DbContext, so a half-edited AppUser left behind by a rejected change would be saved with the audit row.
        var policyErrors = await ValidateNewPasswordAsync(user, currentPassword, newPassword);
        if (policyErrors.Count > 0)
        {
            return AdminPasswordChangeOutcome.NewPasswordRejected(user.Id, user.Email, policyErrors);
        }

        var clearedMustChangePassword = user.MustChangePassword;
        user.MustChangePassword = false;

        // ChangePasswordAsync re-verifies the current password, rehashes, rotates the SecurityStamp and saves the
        // user once — so the flag and the new stamp land in the same UPDATE.
        var changeResult = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!changeResult.Succeeded)
        {
            user.MustChangePassword = clearedMustChangePassword;
            return AdminPasswordChangeOutcome.NewPasswordRejected(user.Id, user.Email, [.. changeResult.Errors.Select(e => e.Description)]);
        }

        var securityStamp = await userManager.GetSecurityStampAsync(user);
        return AdminPasswordChangeOutcome.Succeeded(
            user.Id,
            user.Email ?? string.Empty,
            user.FirstName,
            user.LastName,
            securityStamp,
            clearedMustChangePassword);
    }

    private async Task<List<string>> ValidateNewPasswordAsync(AppUser user, string currentPassword, string newPassword)
    {
        List<string> errors = [];

        // A forced change that "changes" the password to the bootstrap value would defeat the point of it.
        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            errors.Add("The new password must be different from the current password.");
            return errors;
        }

        foreach (var validator in userManager.PasswordValidators)
        {
            var result = await validator.ValidateAsync(userManager, user, newPassword);
            if (!result.Succeeded)
            {
                errors.AddRange(result.Errors.Select(e => e.Description));
            }
        }

        return errors;
    }
}
