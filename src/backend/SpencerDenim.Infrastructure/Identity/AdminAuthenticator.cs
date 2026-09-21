using Microsoft.AspNetCore.Identity;
using SpencerDenim.Application.Admin.Authentication;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Checks staff credentials (doc 08 §3.1 DB10 scoping rules). Uses <see cref="SignInManager{TUser}"/> purely to
/// validate the password and manage Identity's lockout bookkeeping (doc 08 §3.1 N14, time-boxed) — it never
/// signs a cookie in; that is the Api endpoint's job (see <see cref="IAdminAuthenticator"/>).
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
            user.LastLoginAtUtc = DateTime.UtcNow;
            await userManager.UpdateAsync(user);

            var (grantsAll, permissions) = await LoadPermissionsAsync(user);
            return AdminAuthenticationOutcome.Succeeded(user.Id, user.Email ?? email, user.FirstName, user.LastName, grantsAll, permissions);
        }

        if (checkResult.IsLockedOut)
        {
            var lockoutEnd = user.LockoutEnd ?? DateTimeOffset.UtcNow;
            return AdminAuthenticationOutcome.LockedOut(user.Id, user.Email ?? email, lockoutEnd, wasNewlyLockedOut: !wasAlreadyLockedOut);
        }

        return AdminAuthenticationOutcome.InvalidCredentials(user.Id, user.Email);
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
