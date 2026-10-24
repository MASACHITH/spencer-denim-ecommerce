using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Admin.Authentication;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Checks staff credentials (doc 08 §3.1 DB10 scoping rules) and, for an account with 2FA on, the second-step code
/// (W1-10). The password goes through <see cref="StaffAccountLockout"/> (Identity's <see cref="SignInManager{TUser}"/>
/// validates it; the time-boxed lockout of doc 08 §3.1 N14 is counted atomically, issue #46), and the code through
/// <see cref="UserManager{TUser}"/>'s own 2FA methods, its failures counted by the same lockout — it never signs a
/// cookie in; that is the Api endpoint's job (see <see cref="IAdminAuthenticator"/>).
/// </summary>
public sealed class AdminAuthenticator(
    UserManager<AppUser> userManager,
    RoleManager<AppRole> roleManager,
    IPasswordHasher<AppUser> passwordHasher,
    IOptions<AdminSecurityOptions> securityOptions,
    StaffAccountLockout lockout,
    AppDbContext dbContext) : IAdminAuthenticator
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

        // Issue #46: SignInManager still checks the password and resets the counter on success, but a failure is
        // counted by one atomic UPDATE that locks at the threshold configured on IdentityOptions.Lockout (bound from
        // AdminSecurityOptions) and refuses an account already locked, so parallel wrong passwords cannot overwrite
        // each other's count; and a right password is accepted only if no racing failure locked the account while
        // it was being verified. Which attempt tripped the lock comes from that statement, not from comparing with
        // the lockout state loaded above, which every request of a parallel burst sees as "not locked".
        var check = await lockout.CheckPasswordAsync(user, password);

        if (check.Status == StaffSecretCheckStatus.Succeeded)
        {
            var securityStamp = await userManager.GetSecurityStampAsync(user);

            // W1-10: with 2FA on, the right password alone opens no session. SignInManager deliberately leaves the
            // failed-attempt count alone in this case, so a known password cannot be used to reset the counter
            // between rounds of code guesses; the count is reset once the code is accepted.
            if (await userManager.GetTwoFactorEnabledAsync(user))
            {
                // Issue #145: null means another request got to the account's pending sign-in first. The password
                // was right, so this is not a failed login; it is the reply a stale pending sign-in gets.
                var pendingSignInId = await OpenPendingSignInAsync(user);
                return pendingSignInId is null
                    ? AdminAuthenticationOutcome.TwoFactorSessionExpired(user.Id, user.Email)
                    : AdminAuthenticationOutcome.TwoFactorRequired(user.Id, user.Email ?? email, securityStamp, pendingSignInId);
            }

            return await SucceedAsync(user, securityStamp);
        }

        if (check is { Status: StaffSecretCheckStatus.LockedOut, LockoutEnd: { } lockoutEnd })
        {
            return AdminAuthenticationOutcome.LockedOut(user.Id, user.Email ?? email, lockoutEnd, check.WasNewlyLockedOut);
        }

        return AdminAuthenticationOutcome.InvalidCredentials(user.Id, user.Email);
    }

    public async Task<AdminAuthenticationOutcome> CompleteTwoFactorSignInAsync(
        int userId,
        string pendingSecurityStamp,
        string pendingSignInId,
        string code,
        bool isRecoveryCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingSecurityStamp);
        ArgumentNullException.ThrowIfNull(pendingSignInId);
        ArgumentNullException.ThrowIfNull(code);
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture));
        if (user is null || user.UserType != UserType.Staff || !user.IsActive)
        {
            return AdminAuthenticationOutcome.TwoFactorSessionExpired(userId, user?.Email);
        }

        // The pending cookie is only a pointer: the sign-in it names must still be open on the server (not yet
        // completed, not ended by a lockout, not expired). Without this, a copy of the cookie taken before a
        // successful sign-in would open a second session with nothing but a code.
        if (!await IsPendingSignInOpenAsync(user, pendingSignInId))
        {
            return AdminAuthenticationOutcome.TwoFactorSessionExpired(user.Id, user.Email);
        }

        // The pending sign-in belongs to the account as it was at the password step. A password change, a 2FA
        // change or a reset in between rotates the stamp, and the user must start again with the new state.
        var currentStamp = await userManager.GetSecurityStampAsync(user);
        if (!string.Equals(currentStamp, pendingSecurityStamp, StringComparison.Ordinal)
            || !await userManager.GetTwoFactorEnabledAsync(user))
        {
            await ClosePendingSignInAsync(user);
            return AdminAuthenticationOutcome.TwoFactorSessionExpired(user.Id, user.Email);
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            await ClosePendingSignInAsync(user);
            return AdminAuthenticationOutcome.LockedOut(
                user.Id, user.Email ?? string.Empty, user.LockoutEnd ?? DateTimeOffset.UtcNow, wasNewlyLockedOut: false, isRecoveryCode);
        }

        if (!await IsCodeValidAsync(user, code, isRecoveryCode))
        {
            // Wrong codes count towards the same time-boxed lockout as wrong passwords (doc 08 §3.1 N14), through the
            // same atomic statement (issue #46): parallel wrong codes used to get 8–17 checks per window, not 5. A
            // code that raced a lockout another request set is answered as locked, and only the code that tripped
            // the lock is reported as newly locking it.
            var failure = await lockout.RecordFailedAttemptAsync(user);
            if (failure is { Status: StaffSecretCheckStatus.LockedOut, LockoutEnd: { } lockoutEnd })
            {
                await ClosePendingSignInAsync(user);
                return AdminAuthenticationOutcome.LockedOut(
                    user.Id, user.Email ?? string.Empty, lockoutEnd, failure.WasNewlyLockedOut, isRecoveryCode);
            }

            return AdminAuthenticationOutcome.InvalidTwoFactorCode(user.Id, user.Email, isRecoveryCode);
        }

        // A right code is accepted only if the account is still unlocked now it has been verified: the check above
        // read the user as loaded, and parallel wrong codes may have locked it since (see StaffAccountLockout).
        if (await lockout.FindActiveLockoutAsync(user) is { } racedLockoutEnd)
        {
            await ClosePendingSignInAsync(user);
            return AdminAuthenticationOutcome.LockedOut(
                user.Id, user.Email ?? string.Empty, racedLockoutEnd, wasNewlyLockedOut: false, isRecoveryCode);
        }

        // The claim. The code check above is no gate on its own: a recovery code is redeemed by a read-modify-write
        // save, so any number of parallel requests with the same recovery code all pass it. Whether a replayed
        // authenticator code is refused earlier is the TOTP provider's business; this claim does not rely on it.
        // Deleting the pending sign-in is what lets exactly one of them through: the DELETE affects one row for the
        // request that gets there first and none for the rest, which are answered as a sign-in that is already over
        // (the same reply a stale cookie gets), with no session. On main this was Identity's own remove, which fails
        // for the second deleter; the idempotent close below must not be used here, or all of them sign in.
        if (!await TryClaimPendingSignInAsync(user, pendingSignInId))
        {
            return AdminAuthenticationOutcome.TwoFactorSessionExpired(user.Id, user.Email);
        }

        await userManager.ResetAccessFailedCountAsync(user);
        var recoveryCodesLeft = await userManager.CountRecoveryCodesAsync(user);
        return await SucceedAsync(user, currentStamp, twoFactorEnabled: true, isRecoveryCode, recoveryCodesLeft);
    }

    /// <summary>
    /// Opens the account's one pending 2FA sign-in and returns its random id, which goes into the pending cookie.
    /// Only a hash and the expiry are stored. A new password step replaces any earlier pending sign-in, so only the
    /// latest browser to pass the password can finish.
    /// <para>
    /// Returns <see langword="null"/> when the write lost a race (issue #145), and the caller must not hand out a
    /// pending cookie: the row it would name is not this request's. Two races reach it. The write is Identity's
    /// read-modify-write of the token and then of the user row, so a second-step request that completes the sign-in in
    /// between deletes the token and changes the user's concurrency stamp, and the save is reported as a concurrency
    /// failure. And on an account with no pending sign-in yet, two password steps (a double-clicked Sign in) both
    /// read "none" and both insert, so the second insert hits the primary key. In both the winner holds the live
    /// pending sign-in, which is the rule above, and the loser is simply too late.
    /// </para>
    /// <para>
    /// Any other failure is still a bug and still throws, which is why this does not catch what it cannot name.
    /// </para>
    /// </summary>
    private async Task<string?> OpenPendingSignInAsync(AppUser user)
    {
        var id = PendingSignInIds.NewId();
        var expiresUtc = DateTimeOffset.UtcNow.AddMinutes(securityOptions.Value.TwoFactorPendingSignInMinutes);
        var value = string.Create(CultureInfo.InvariantCulture, $"{PendingSignInIds.Hash(id)}|{expiresUtc.ToUnixTimeSeconds()}");

        IdentityResult result;
        try
        {
            result = await userManager.SetAuthenticationTokenAsync(user, AdminTokenNames.LoginProvider, AdminTokenNames.PendingTwoFactorSignIn, value);
        }
        catch (DbUpdateException exception) when (SqlUniqueViolation.Matches(exception))
        {
            ForgetFailedPendingSignInWrite(user);
            return null;
        }

        var concurrencyFailure = userManager.ErrorDescriber.ConcurrencyFailure().Code;
        if (!result.Succeeded && result.Errors.Any(e => e.Code == concurrencyFailure))
        {
            ForgetFailedPendingSignInWrite(user);
            return null;
        }

        EnsureSucceeded(result);
        return id;
    }

    /// <summary>
    /// A save that failed leaves what it tried to write in the change tracker, and the audit write that shares this
    /// <see cref="DbContext"/> would try it again and fail the whole request (see <see cref="ClosePendingSignInAsync"/>).
    /// The token row and the user row are let go; the caller reads only the user's id and email from here on.
    /// </summary>
    private void ForgetFailedPendingSignInWrite(AppUser user)
    {
        ForgetTrackedPendingSignIn(user);
        dbContext.Entry(user).State = EntityState.Detached;
    }

    /// <summary>True when <paramref name="id"/> names the open, unexpired pending sign-in. An expired one is removed.</summary>
    private async Task<bool> IsPendingSignInOpenAsync(AppUser user, string id)
    {
        var stored = await userManager.GetAuthenticationTokenAsync(user, AdminTokenNames.LoginProvider, AdminTokenNames.PendingTwoFactorSignIn);
        var parts = stored?.Split('|', 2);
        if (parts is not { Length: 2 }
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresUnix))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresUnix)
        {
            await ClosePendingSignInAsync(user);
            return false;
        }

        return PendingSignInIds.HashesMatch(parts[0], id);
    }

    /// <summary>
    /// Completes the pending sign-in <paramref name="pendingSignInId"/> names, for exactly one caller: true for the
    /// request whose set-based <c>DELETE</c> removed the row, false for every other request racing it, and for one
    /// whose pending sign-in was replaced by a newer password step in the meantime (the row then holds a different
    /// hash and nothing matches). Only the success path may use this; every path that merely ends a pending
    /// sign-in (wrong code, lockout, stale stamp) uses <see cref="ClosePendingSignInAsync"/>, which is happy to
    /// find nothing left to delete.
    /// </summary>
    private async Task<bool> TryClaimPendingSignInAsync(AppUser user, string pendingSignInId)
    {
        // The stored value is "{hash of the id}|{expiry}" (see OpenPendingSignInAsync); the hash is hex, so the
        // prefix match has no LIKE wildcards in it.
        var valuePrefix = PendingSignInIds.Hash(pendingSignInId) + "|";

        var claimed = await dbContext.Set<IdentityUserToken<int>>()
            .Where(t => t.UserId == user.Id
                && t.LoginProvider == AdminTokenNames.LoginProvider
                && t.Name == AdminTokenNames.PendingTwoFactorSignIn
                && t.Value != null
                && t.Value.StartsWith(valuePrefix))
            .ExecuteDeleteAsync();

        ForgetTrackedPendingSignIn(user);
        return claimed == 1;
    }

    /// <summary>
    /// Ends the pending sign-in, idempotently. Identity's <c>RemoveAuthenticationTokenAsync</c> is not: when two
    /// requests end the same pending sign-in at once (parallel wrong codes racing the lockout, issue #46), the second
    /// one's <c>DELETE</c> finds no row, which EF reports as a concurrency failure, and the deleted entry left in the
    /// change tracker then fails the audit write that shares this <see cref="DbContext"/>. A set-based delete is done
    /// whether or not another request got there first; the tracked copy of the row, if any, is let go so nothing later
    /// in the request tries to save it.
    /// </summary>
    private async Task ClosePendingSignInAsync(AppUser user)
    {
        await dbContext.Set<IdentityUserToken<int>>()
            .Where(t => t.UserId == user.Id
                && t.LoginProvider == AdminTokenNames.LoginProvider
                && t.Name == AdminTokenNames.PendingTwoFactorSignIn)
            .ExecuteDeleteAsync();

        ForgetTrackedPendingSignIn(user);
    }

    private void ForgetTrackedPendingSignIn(AppUser user)
    {
        foreach (var tracked in dbContext.ChangeTracker.Entries<IdentityUserToken<int>>()
            .Where(e => e.Entity.UserId == user.Id
                && e.Entity.LoginProvider == AdminTokenNames.LoginProvider
                && e.Entity.Name == AdminTokenNames.PendingTwoFactorSignIn)
            .ToList())
        {
            tracked.State = EntityState.Detached;
        }
    }

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not open the pending sign-in: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
    }

    /// <summary>
    /// An authenticator code is checked against the current key by the replay-protected TOTP provider (a few
    /// 30-second steps either side for clock drift, each step usable once); a recovery code is redeemed, which
    /// removes it so it can never work again.
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
