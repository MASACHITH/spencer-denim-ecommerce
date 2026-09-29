using System.Globalization;
using Microsoft.AspNetCore.Identity;
using SpencerDenim.Application.Admin.Authentication;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Checks staff credentials (doc 08 §3.1 DB10 scoping rules) and, for an account with 2FA on, the second-step code
/// (W1-10). Uses <see cref="SignInManager{TUser}"/> purely to validate the password and manage Identity's lockout
/// bookkeeping (doc 08 §3.1 N14, time-boxed), and <see cref="UserManager{TUser}"/>'s own 2FA methods for the code —
/// it never signs a cookie in; that is the Api endpoint's job (see <see cref="IAdminAuthenticator"/>).
/// </summary>
public sealed class AdminAuthenticator(
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    RoleManager<AppRole> roleManager,
    IPasswordHasher<AppUser> passwordHasher) : IAdminAuthenticator
{
    // Doc 08 §3.1 DB10: "the response never reveals which" — an unknown email, a customer match, or an
    // inactive account must cost the same as a genuine wrong-password attempt against a real, active staff
    // user, or the endpoint becomes a username-enumeration timing oracle (a real account's wrong-password
    // check is dominated by the constant-time password hash verification inside CheckPasswordSignInAsync,
    // which the three early-return paths below never used to reach). Running the same hasher against a fixed
    // dummy hash keeps the cost comparable without touching the database. The dummy hash is computed once
    // (static, at process start), so it never adds per-request variance of its own.
    private static readonly AppUser DummyHashSubject = new() { UserName = "no-such-user@invalid" };

    private static readonly string DummyPasswordHash =
        new PasswordHasher<AppUser>().HashPassword(DummyHashSubject, "Dummy-Password-Never-Used-1!");

    public async Task<AdminAuthenticationOutcome> AuthenticateAsync(string email, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);
        cancellationToken.ThrowIfCancellationRequested();

        // Doc 08 §3.1 DB10 "Scoping rules": customer flows are filtered UserType = Customer before UserManager is
        // even consulted. The mirror image for admin: staff lookup happens here, and a customer match (or no
        // match at all) is treated identically as invalid credentials — the response never reveals which.
        var user = await userManager.FindByEmailAsync(email);
        if (user is null || user.UserType != UserType.Staff)
        {
            VerifyDummyPassword(password);
            return AdminAuthenticationOutcome.InvalidCredentials(userId: null, email);
        }

        if (!user.IsActive)
        {
            VerifyDummyPassword(password);
            return AdminAuthenticationOutcome.InvalidCredentials(user.Id, user.Email);
        }

        var wasAlreadyLockedOut = await userManager.IsLockedOutAsync(user);

        // lockoutOnFailure: true — SignInManager increments AccessFailedCount and sets LockoutEnd once the
        // threshold configured on IdentityOptions.Lockout (bound from AdminSecurityOptions) is reached, and
        // resets the counter on success. This is the one call site that owns all of that bookkeeping.
        var checkResult = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);

        if (checkResult.Succeeded)
        {
            var securityStamp = await userManager.GetSecurityStampAsync(user);

            // W1-10: with 2FA on, the right password alone opens no session. SignInManager deliberately leaves the
            // failed-attempt count alone in this case, so a known password cannot be used to reset the counter
            // between rounds of code guesses; the count is reset once the code is accepted.
            if (await userManager.GetTwoFactorEnabledAsync(user))
            {
                return AdminAuthenticationOutcome.TwoFactorRequired(user.Id, user.Email ?? email, securityStamp);
            }

            return await SucceedAsync(user, securityStamp);
        }

        if (checkResult.IsLockedOut)
        {
            var lockoutEnd = user.LockoutEnd ?? DateTimeOffset.UtcNow;
            return AdminAuthenticationOutcome.LockedOut(user.Id, user.Email ?? email, lockoutEnd, wasNewlyLockedOut: !wasAlreadyLockedOut);
        }

        return AdminAuthenticationOutcome.InvalidCredentials(user.Id, user.Email);
    }

    public async Task<AdminAuthenticationOutcome> CompleteTwoFactorSignInAsync(
        int userId,
        string pendingSecurityStamp,
        string code,
        bool isRecoveryCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingSecurityStamp);
        ArgumentNullException.ThrowIfNull(code);
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
        if (user is null || user.UserType != UserType.Staff || !user.IsActive)
        {
            return AdminAuthenticationOutcome.TwoFactorSessionExpired(userId, user?.Email);
        }

        // The pending sign-in belongs to the account as it was at the password step. A password change, a 2FA
        // change or a reset in between rotates the stamp, and the user must start again with the new state.
        var currentStamp = await userManager.GetSecurityStampAsync(user);
        if (!string.Equals(currentStamp, pendingSecurityStamp, StringComparison.Ordinal)
            || !await userManager.GetTwoFactorEnabledAsync(user))
        {
            return AdminAuthenticationOutcome.TwoFactorSessionExpired(user.Id, user.Email);
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            return AdminAuthenticationOutcome.LockedOut(
                user.Id, user.Email ?? string.Empty, user.LockoutEnd ?? DateTimeOffset.UtcNow, wasNewlyLockedOut: false, isRecoveryCode);
        }

        if (!await IsCodeValidAsync(user, code, isRecoveryCode))
        {
            // Wrong codes count towards the same time-boxed lockout as wrong passwords (doc 08 §3.1 N14).
            await userManager.AccessFailedAsync(user);
            if (await userManager.IsLockedOutAsync(user))
            {
                return AdminAuthenticationOutcome.LockedOut(
                    user.Id, user.Email ?? string.Empty, user.LockoutEnd ?? DateTimeOffset.UtcNow, wasNewlyLockedOut: true, isRecoveryCode);
            }

            return AdminAuthenticationOutcome.InvalidTwoFactorCode(user.Id, user.Email, isRecoveryCode);
        }

        await userManager.ResetAccessFailedCountAsync(user);
        var recoveryCodesLeft = await userManager.CountRecoveryCodesAsync(user);
        return await SucceedAsync(user, currentStamp, twoFactorEnabled: true, isRecoveryCode, recoveryCodesLeft);
    }

    /// <summary>
    /// An authenticator code is checked against the current key (Identity's TOTP provider, a few 30-second steps
    /// either side for clock drift); a recovery code is redeemed, which removes it so it can never work again.
    /// A blank code is refused before Identity sees it: its recovery-code store treats an empty string as a match
    /// when no codes are stored.
    /// </summary>
    private async Task<bool> IsCodeValidAsync(AppUser user, string code, bool isRecoveryCode)
    {
        if (isRecoveryCode)
        {
            var recoveryCode = TwoFactorCodeFormat.NormaliseRecoveryCode(code);
            return recoveryCode.Length > 0
                && (await userManager.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCode)).Succeeded;
        }

        var authenticatorCode = TwoFactorCodeFormat.NormaliseAuthenticatorCode(code);
        return authenticatorCode.Length > 0
            && await userManager.VerifyTwoFactorTokenAsync(user, userManager.Options.Tokens.AuthenticatorTokenProvider, authenticatorCode);
    }

    /// <summary>A completed sign-in: records it on the account and loads what the session principal is built from.</summary>
    private async Task<AdminAuthenticationOutcome> SucceedAsync(
        AppUser user,
        string securityStamp,
        bool twoFactorEnabled = false,
        bool usedRecoveryCode = false,
        int recoveryCodesLeft = 0)
    {
        user.LastLoginAtUtc = DateTime.UtcNow;
        await userManager.UpdateAsync(user);

        var (grantsAll, permissions) = await LoadPermissionsAsync(user);
        return AdminAuthenticationOutcome.Succeeded(
            user.Id,
            user.Email ?? string.Empty,
            user.FirstName,
            user.LastName,
            grantsAll,
            permissions,
            securityStamp,
            user.MustChangePassword,
            twoFactorEnabled,
            usedRecoveryCode,
            recoveryCodesLeft);
    }

    /// <summary>
    /// Loaded once here (rather than through <c>IEffectivePermissionsProvider</c>) purely to embed the claims
    /// the endpoint needs to build the sign-in principal; the cache-backed provider is still the source of truth
    /// for every later authorization check on this session.
    /// </summary>
    private async Task<(bool GrantsAll, IReadOnlyCollection<string> Permissions)> LoadPermissionsAsync(AppUser user)
    {
        var roleNames = await userManager.GetRolesAsync(user);
        if (roleNames.Count == 0)
        {
            return (false, []);
        }

        var permissions = new HashSet<string>(StringComparer.Ordinal);
        var grantsAll = false;

        foreach (var roleName in roleNames)
        {
            var role = await roleManager.FindByNameAsync(roleName);
            if (role is null)
            {
                continue;
            }

            if (role.GrantsAllPermissions)
            {
                grantsAll = true;
                continue;
            }

            var claims = await roleManager.GetClaimsAsync(role);
            foreach (var claim in claims)
            {
                if (claim.Type == RoleClaimConfiguration.PermissionClaimType)
                {
                    permissions.Add(claim.Value);
                }
            }
        }

        return (grantsAll, grantsAll ? [] : permissions);
    }

    /// <summary>
    /// Spends the same CPU cost a real password check would, without revealing anything: the result is
    /// deliberately discarded, and <see cref="DummyPasswordHash"/> never matches any real password.
    /// </summary>
    private void VerifyDummyPassword(string password) =>
        passwordHasher.VerifyHashedPassword(DummyHashSubject, DummyPasswordHash, password);
}
