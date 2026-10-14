using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Accounts.Abstractions;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Ops.Email.Contracts;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Identity.Customers;

/// <summary>
/// Doc 11 P11 step C (forgot and reset) and step 10's password half (ACC-01).
/// </summary>
/// <remarks>
/// <b>The reset token is a credential and is treated as one</b> (see <see cref="CustomerAccountTokens"/>
/// for the shape and for why Identity's stateless provider is not used): it lives in
/// <c>identity.UserTokens</c>, it is single-use, it is time-boxed, and its email body is destroyed on
/// send by doc 08 §3.8's G-03 rule.
/// <para>
/// <b>Setting a password bumps the security stamp, and that is load-bearing in two places.</b> Doc 11
/// P11 step C.9 asks for it — "invalidates existing sessions" — and both cookie schemes revalidate
/// against the stamp, so every other browser is signed out within its revalidation interval. It is also
/// the mechanism <c>IEffectivePermissionsProvider</c> keys its five-minute cache on
/// <c>(userId, SecurityStamp)</c>, which is why the same bump is what Tharanga needs on the staff side:
/// a stale permission set cannot outlive a credential change.
/// </para>
/// </remarks>
internal sealed class CustomerPasswordStore(
    AppDbContext dbContext,
    UserManager<AppUser> userManager,
    CustomerUserLookup lookup,
    CustomerAccountLockout lockout,
    IAccountEmailOutbox accountEmails,
    IClock clock,
    IOptions<CustomerSecurityOptions> options,
    ILogger<CustomerPasswordStore> logger) : ICustomerPasswords
{
    /// <summary>
    /// Doc 11 P11 step C.8. Mints a reset token and queues the message — only for an active customer.
    /// </summary>
    /// <remarks>
    /// <b>It returns nothing, which is doc 08 §3.1 DB10 expressed in the type.</b> A method that reported
    /// which of the three cases it took would be one careless endpoint away from leaking it. The outcome
    /// is logged at debug level, and the integration suite asserts the three cases against
    /// <c>ops.EmailOutbox</c> rather than against the response.
    /// <para>
    /// <b>The password-hash cost is <i>not</i> spent here, and that is not an oversight.</b> This
    /// endpoint takes no password, so there is no hash to compare against on the happy path either — what
    /// differs between the three cases is a keyed index read and, for a customer, two inserts. The timing
    /// equalisation that matters is on the sign-in form, where a hash dominates (see
    /// <see cref="CustomerUserLookup.SpendPasswordHashCost"/> and #101). Measuring this one is in the
    /// task report, with the numbers.
    /// </para>
    /// </remarks>
    public async Task RequestResetAsync(string email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);

        var user = await lookup.FindCustomerByEmailAsync(email, cancellationToken);

        if (user?.Email is null)
        {
            // Unknown address, or a staff address. Doc 08 §3.1 DB10: "Only a customer match triggers an
            // email; a staff match sends nothing." Debug, not information: on a public endpoint this is
            // an ordinary event and an information-level line per attempt is a log a shop can be spammed
            // into filling.
            CustomerAccountLog.PasswordResetForUnknownAddress(logger);
            return;
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var fresh = await lookup.FindCustomerByEmailAsync(email, cancellationToken);
            if (fresh?.Email is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }

            var hours = options.Value.PasswordResetTokenHours;
            var minted = CustomerAccountTokens.Mint(fresh.Id, clock.UtcNow.UtcDateTime.AddHours(hours));

            // Minting replaces any token already on the account, so a customer who presses the button
            // twice has exactly one link that works. See CustomerAccountTokens: that is also why no
            // DeduplicationKey is used on the outbox row — a dedup key would suppress the second
            // message, which is the one carrying the only live link.
            var set = await userManager.SetAuthenticationTokenAsync(
                fresh,
                CustomerAccountTokens.LoginProvider,
                CustomerAccountTokens.PasswordResetName,
                minted.StoredValue);

            if (!set.Succeeded)
            {
                throw new InvalidOperationException(
                    "Could not store the password-reset token: "
                    + string.Join("; ", set.Errors.Select(error => error.Description)));
            }

            var queued = accountEmails.EnlistPasswordReset(
                new AccountEmailFacts(
                    new AccountEmailAddressee(fresh.Email, fresh.FirstName, fresh.LastName),
                    Link: null,
                    clock.UtcNow.UtcDateTime)
                {
                    LinkValidHours = hours,
                },
                minted.Link);

            if (!queued)
            {
                // No storefront origin, so no link. Rolling back is the only honest answer: committing
                // would replace a token the customer might still hold with one nobody can ever use.
                await transaction.RollbackAsync(cancellationToken);

                CustomerAccountLog.PasswordResetWithoutLink(logger);

                return;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    /// <summary>
    /// Doc 11 P11 step C.9: redeem the link, set the password, spend the token, bump the stamp.
    /// </summary>
    /// <remarks>
    /// <b>The token is spent by a conditional delete and the delete <i>is</i> the claim</b> — #146's
    /// pattern for the pending 2FA sign-in, for the same reason. Two tabs submitting the same link
    /// together must not both set a password: whichever delete affects a row owns the redemption, and the
    /// other is told the link is invalid. Restricting the delete to the exact stored value is what makes
    /// it a claim rather than a tidy-up.
    /// <para>
    /// <b>Order inside the transaction: claim, then write.</b> Claiming first means a failure to set the
    /// password rolls the claim back with it, so the link survives a transient error rather than being
    /// burnt by one.
    /// </para>
    /// </remarks>
    public async Task<CustomerPasswordResetResult> ResetAsync(
        string token,
        string newPassword,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(newPassword);

        if (CustomerAccountTokens.ReadUserId(token) is not { } userId)
        {
            return new CustomerPasswordResetResult.InvalidToken();
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var user = await lookup.FindCustomerByIdAsync(userId, cancellationToken);
            if (user is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (CustomerPasswordResetResult)new CustomerPasswordResetResult.InvalidToken();
            }

            var stored = await userManager.GetAuthenticationTokenAsync(
                user,
                CustomerAccountTokens.LoginProvider,
                CustomerAccountTokens.PasswordResetName);

            if (CustomerAccountTokens.Verify(token, stored) is not { } redeemed
                || redeemed.ExpiresAtUtc <= clock.UtcNow.UtcDateTime)
            {
                // An expired row is removed on the way past, so a spent credential does not sit in the
                // table until something else happens to the account. The answer is the same either way:
                // telling a holder that a token is "expired" rather than "wrong" tells them it was once
                // real.
                if (stored is not null)
                {
                    await SpendTokenAsync(userId, CustomerAccountTokens.PasswordResetName, stored, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                else
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return (CustomerPasswordResetResult)new CustomerPasswordResetResult.InvalidToken();
            }

            var problems = await ValidatePasswordAsync(user, newPassword);
            if (problems.Count > 0)
            {
                // The token is deliberately *not* spent: the holder proved they have the link, and a
                // refused password is their mistake rather than an attack. Burning the link here would
                // mean a shopper who typed a short password had to ask for a whole new message.
                await transaction.RollbackAsync(cancellationToken);
                return (CustomerPasswordResetResult)new CustomerPasswordResetResult.WeakPassword(problems);
            }

            if (await SpendTokenAsync(userId, CustomerAccountTokens.PasswordResetName, stored, cancellationToken) == 0)
            {
                // Somebody else redeemed the same link first.
                await transaction.RollbackAsync(cancellationToken);
                return (CustomerPasswordResetResult)new CustomerPasswordResetResult.InvalidToken();
            }

            // RemovePasswordAsync + AddPasswordAsync rather than ResetPasswordAsync, because the latter
            // needs one of Identity's own stateless tokens and this flow mints its own. Both of these
            // bump the security stamp through UserManager, which is doc 11 P11 step C.9's
            // "invalidates existing sessions" and the thing IEffectivePermissionsProvider's cache keys
            // on.
            EnsureSucceeded(await userManager.RemovePasswordAsync(user));
            EnsureSucceeded(await userManager.AddPasswordAsync(user, newPassword));
            EnsureSucceeded(await userManager.UpdateSecurityStampAsync(user));

            // A reset also clears a lockout: a customer who was locked out <i>because</i> they had
            // forgotten their password must not have to wait fifteen minutes after proving they own the
            // mailbox. Set-based, so no stale user columns are written back (#146).
            await ClearLockoutAsync(userId, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            CustomerAccountLog.PasswordResetRedeemed(logger, userId);

            return (CustomerPasswordResetResult)new CustomerPasswordResetResult.Success();
        });
    }

    /// <summary>Doc 11 P11 step 10: a signed-in customer changes their own password.</summary>
    /// <remarks>
    /// <b>A wrong current password counts towards the same lockout as a wrong sign-in</b>, and the same
    /// post-verification re-check applies: #146 extended its guard to "change-password and the
    /// recovery-code endpoints" for exactly this reason — an endpoint that verifies a password is a
    /// password oracle whether or not it is called "login".
    /// </remarks>
    public async Task<CustomerPasswordChangeResult> ChangeAsync(
        int userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);

        var user = await lookup.FindCustomerByIdAsync(userId, cancellationToken);
        if (user?.Email is null)
        {
            // The session named an account that is no longer an active customer. Treated as a wrong
            // current password rather than a 404: the caller holds a cookie this request is about to
            // invalidate anyway, and there is nothing to tell them about the row.
            return new CustomerPasswordChangeResult.WrongCurrentPassword();
        }

        if (user.LockoutEnd is { } lockedUntil && lockedUntil.UtcDateTime > clock.UtcNow.UtcDateTime)
        {
            return new CustomerPasswordChangeResult.LockedOut(lockedUntil.UtcDateTime, WasNewlyLockedOut: false);
        }

        if (!await userManager.CheckPasswordAsync(user, currentPassword))
        {
            var outcome = await lockout.RecordFailureAsync(user.Id, cancellationToken);

            return outcome.IsLockedOut && outcome.LockoutEndUtc is { } end
                ? new CustomerPasswordChangeResult.LockedOut(end, outcome.TrippedByThisAttempt)
                : new CustomerPasswordChangeResult.WrongCurrentPassword();
        }

        if (!await lockout.IsStillUnlockedAsync(user.Id, cancellationToken))
        {
            var end = user.LockoutEnd?.UtcDateTime ?? clock.UtcNow.UtcDateTime;
            return new CustomerPasswordChangeResult.LockedOut(end, WasNewlyLockedOut: false);
        }

        var problems = await ValidatePasswordAsync(user, newPassword);
        if (problems.Count > 0)
        {
            return new CustomerPasswordChangeResult.WeakPassword(problems);
        }

        var changed = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!changed.Succeeded)
        {
            // The only thing left that ChangePasswordAsync can refuse after the two checks above is a
            // concurrent change in another tab, which re-reads as a wrong current password.
            return new CustomerPasswordChangeResult.WrongCurrentPassword();
        }

        await lockout.ResetFailureCountAsync(user.Id, cancellationToken);

        var stamp = await userManager.GetSecurityStampAsync(user);

        CustomerAccountLog.PasswordChanged(logger, userId);

        // The caller reissues its own cookie with this stamp, or this browser signs itself out at the
        // next revalidation — which is what every *other* browser is supposed to do.
        return new CustomerPasswordChangeResult.Success(new CustomerSession(
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName,
            user.EmailConfirmed,
            stamp));
    }

    /// <summary>
    /// Every reason a password is refused, in the one order a shopper reads them.
    /// </summary>
    /// <remarks>
    /// <b>The length rule first and Identity's validators after</b>, so the message a shopper most often
    /// needs is the message at the top. Identity's own descriptions are used verbatim rather than
    /// rewritten: they are the ones that match what is actually enforced, and a second wording would be a
    /// second source of truth about the rule (#126's lesson, in copy rather than in a column width).
    /// </remarks>
    private async Task<IReadOnlyList<string>> ValidatePasswordAsync(AppUser user, string password)
    {
        var problems = new List<string>();

        if (!CustomerPasswordPolicy.IsLongEnough(password))
        {
            problems.Add(CustomerPasswordPolicy.Requirement);
        }

        foreach (var validator in userManager.PasswordValidators)
        {
            var result = await validator.ValidateAsync(userManager, user, password);

            if (!result.Succeeded)
            {
                problems.AddRange(result.Errors.Select(error => error.Description));
            }
        }

        return problems;
    }

    /// <summary>
    /// Deletes the token row, restricted to the exact value that was read. The affected row count is the
    /// claim.
    /// </summary>
    private async Task<int> SpendTokenAsync(
        int userId,
        string name,
        string? storedValue,
        CancellationToken cancellationToken) =>
        storedValue is null
            ? 0
            : await dbContext.UserTokens
                .Where(row =>
                    row.UserId == userId
                    && row.LoginProvider == CustomerAccountTokens.LoginProvider
                    && row.Name == name
                    && row.Value == storedValue)
                .ExecuteDeleteAsync(cancellationToken);

    private async Task ClearLockoutAsync(int userId, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE [identity].[Users]
            SET [AccessFailedCount] = 0, [LockoutEnd] = NULL
            WHERE [Id] = {userId} AND [UserType] = {(byte)UserType.Customer}
            """,
            cancellationToken);

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Setting the customer's password failed after validation had passed: "
                + string.Join("; ", result.Errors.Select(error => error.Description)));
        }
    }
}
