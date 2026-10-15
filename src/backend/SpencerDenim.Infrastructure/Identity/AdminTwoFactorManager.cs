using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Admin.Account;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// A staff user's own 2FA (W1-10) through <see cref="UserManager{TUser}"/>'s built-in authenticator support: the key
/// and the recovery codes are Identity's own <c>identity.UserTokens</c> rows, and turning 2FA on or off rotates the
/// <c>SecurityStamp</c> (Identity's behaviour), which ends every other session for the account.
/// </summary>
public sealed class AdminTwoFactorManager(
    UserManager<AppUser> userManager,
    StaffAccountLockout lockout,
    IOptions<AdminSecurityOptions> securityOptions) : IAdminTwoFactorManager
{
    public async Task<AdminTwoFactorState?> GetStateAsync(int userId, CancellationToken cancellationToken)
    {
        var user = await FindActiveStaffAsync(userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var enabled = await userManager.GetTwoFactorEnabledAsync(user);
        var codesLeft = enabled ? await userManager.CountRecoveryCodesAsync(user) : 0;
        return new AdminTwoFactorState(user.Id, user.Email, enabled, codesLeft);
    }

    public async Task<AdminTwoFactorOutcome> BeginSetupAsync(int userId, CancellationToken cancellationToken)
    {
        var user = await FindActiveStaffAsync(userId, cancellationToken);
        if (user is null)
        {
            return AdminTwoFactorOutcome.Failed(AdminTwoFactorStatus.AccountUnavailable, userId, null);
        }

        if (await userManager.GetTwoFactorEnabledAsync(user))
        {
            // Replacing the key of a live 2FA would silently break the app it is registered in. Moving to a new
            // phone is: turn it off and on again, or (GrantsAll) another Super Admin's reset.
            return AdminTwoFactorOutcome.Failed(AdminTwoFactorStatus.AlreadyEnabled, user.Id, user.Email);
        }

        // Resumed rather than replaced when a key already exists, so reloading the enrolment page does not
        // invalidate a QR code the user has just scanned. Disabling discards the key, so every fresh enrolment
        // after a disable still gets a new one.
        //
        // Expected behaviour: creating the key (the first set-up after sign-in or after turning 2FA off) goes
        // through Identity's ResetAuthenticatorKeyAsync, which also rotates the security stamp. The endpoint
        // re-issues the caller's cookie with the new stamp, so this session carries on, but every OTHER session
        // for the account is refused on its next request (401 session_ended). Resuming an existing key changes
        // nothing.
        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            var reset = await userManager.ResetAuthenticatorKeyAsync(user);
            EnsureSucceeded(reset, "create the authenticator key");
            key = await userManager.GetAuthenticatorKeyAsync(user)
                ?? throw new InvalidOperationException("Identity stored no authenticator key.");
        }

        var email = user.Email ?? string.Empty;
        return AdminTwoFactorOutcome.Succeeded(
            user.Id,
            email,
            user.FirstName,
            user.LastName,
            await userManager.GetSecurityStampAsync(user),
            TwoFactorCodeFormat.FormatSharedKey(key),
            TwoFactorCodeFormat.BuildAuthenticatorUri(securityOptions.Value.TwoFactorIssuer, email, key));
    }

    public async Task<AdminTwoFactorOutcome> EnableAsync(int userId, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);

        var user = await FindActiveStaffAsync(userId, cancellationToken);
        if (user is null)
        {
            return AdminTwoFactorOutcome.Failed(AdminTwoFactorStatus.AccountUnavailable, userId, null);
        }

        if (await userManager.GetTwoFactorEnabledAsync(user))
        {
            return AdminTwoFactorOutcome.Failed(AdminTwoFactorStatus.AlreadyEnabled, user.Id, user.Email);
        }

        if (string.IsNullOrEmpty(await userManager.GetAuthenticatorKeyAsync(user)))
        {
            return AdminTwoFactorOutcome.Failed(AdminTwoFactorStatus.NotSetUp, user.Id, user.Email);
        }

        var normalised = TwoFactorCodeFormat.NormaliseAuthenticatorCode(code);
        if (normalised.Length == 0
            || !await userManager.VerifyTwoFactorTokenAsync(user, userManager.Options.Tokens.AuthenticatorTokenProvider, normalised))
        {
            return AdminTwoFactorOutcome.Failed(AdminTwoFactorStatus.InvalidCode, user.Id, user.Email);
        }

        // SetTwoFactorEnabledAsync rotates the stamp: every session opened before 2FA was on ends (the gate refuses
        // it on its next request), and the endpoint re-issues the caller's own cookie with the new stamp.
        EnsureSucceeded(await userManager.SetTwoFactorEnabledAsync(user, true), "enable two-factor authentication");
        var recoveryCodes = await GenerateRecoveryCodesAsync(user);

        return AdminTwoFactorOutcome.Succeeded(
            user.Id,
            user.Email ?? string.Empty,
            user.FirstName,
            user.LastName,
            await userManager.GetSecurityStampAsync(user),
            recoveryCodes: recoveryCodes);
    }

    public async Task<AdminTwoFactorOutcome> RegenerateRecoveryCodesAsync(int userId, string currentPassword, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);

        var user = await FindActiveStaffAsync(userId, cancellationToken);
        if (user is null)
        {
            return AdminTwoFactorOutcome.Failed(AdminTwoFactorStatus.AccountUnavailable, userId, null);
        }

        if (!await userManager.GetTwoFactorEnabledAsync(user))
        {
            return AdminTwoFactorOutcome.Failed(AdminTwoFactorStatus.NotEnabled, user.Id, user.Email);
        }

        var refused = await CheckCurrentPasswordAsync(user, currentPassword);
        if (refused is not null)
        {
            return refused;
        }

        var recoveryCodes = await GenerateRecoveryCodesAsync(user);
        return AdminTwoFactorOutcome.Succeeded(
            user.Id,
            user.Email ?? string.Empty,
            user.FirstName,
            user.LastName,
            await userManager.GetSecurityStampAsync(user),
            recoveryCodes: recoveryCodes);
    }

    public async Task<AdminTwoFactorOutcome> DisableAsync(int userId, string currentPassword, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);

        var user = await FindActiveStaffAsync(userId, cancellationToken);
        if (user is null)
        {
            return AdminTwoFactorOutcome.Failed(AdminTwoFactorStatus.AccountUnavailable, userId, null);
        }

        if (!await userManager.GetTwoFactorEnabledAsync(user))
        {
            return AdminTwoFactorOutcome.Failed(AdminTwoFactorStatus.NotEnabled, user.Id, user.Email);
        }

        var refused = await CheckCurrentPasswordAsync(user, currentPassword);
        if (refused is not null)
        {
            return refused;
        }

        EnsureSucceeded(await userManager.SetTwoFactorEnabledAsync(user, false), "disable two-factor authentication");

        // Nothing usable is left behind: a new key (never shown to anyone) replaces the registered one, and the
        // recovery codes are replaced by none, so turning 2FA on again always starts from a fresh scan.
        EnsureSucceeded(await userManager.ResetAuthenticatorKeyAsync(user), "discard the authenticator key");
        await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);

        return AdminTwoFactorOutcome.Succeeded(
            user.Id,
            user.Email ?? string.Empty,
            user.FirstName,
            user.LastName,
            await userManager.GetSecurityStampAsync(user));
    }

    private async Task<AppUser?> FindActiveStaffAsync(int userId, CancellationToken cancellationToken)
    {
        // UserManager methods take no CancellationToken; checking once up front matches AdminAuthenticator.
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
        return user is null || user.UserType != UserType.Staff || !user.IsActive ? null : user;
    }

    /// <summary>
    /// The same check (<see cref="StaffAccountLockout"/>, issue #46) as sign-in and change-password, so a stolen
    /// session cookie cannot be used to guess the password any faster than the sign-in form allows (doc 08 §3.1
    /// N14). Null when the password is right.
    /// </summary>
    private async Task<AdminTwoFactorOutcome?> CheckCurrentPasswordAsync(AppUser user, string currentPassword)
    {
        var check = await lockout.CheckPasswordAsync(user, currentPassword);

        if (check is { Status: StaffSecretCheckStatus.LockedOut, LockoutEnd: { } lockoutEnd })
        {
            return AdminTwoFactorOutcome.LockedOut(user.Id, user.Email, lockoutEnd, check.WasNewlyLockedOut);
        }

        if (check.Status != StaffSecretCheckStatus.Succeeded)
        {
            return AdminTwoFactorOutcome.Failed(AdminTwoFactorStatus.InvalidCurrentPassword, user.Id, user.Email);
        }

        // SignInManager leaves the failed-attempt count alone for 2FA accounts on a right password (see
        // AdminAuthenticator). This caller has already passed both factors to get its session, so reset it here.
        await userManager.ResetAccessFailedCountAsync(user);
        return null;
    }

    private async Task<IReadOnlyList<string>> GenerateRecoveryCodesAsync(AppUser user)
    {
        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, securityOptions.Value.TwoFactorRecoveryCodeCount);
        return [.. codes ?? []];
    }

    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not {action}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
    }
}
