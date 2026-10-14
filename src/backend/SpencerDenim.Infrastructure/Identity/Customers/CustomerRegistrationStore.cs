using System.Globalization;
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
/// Doc 11 P11 step A: creates a customer account, or does the non-enumerating nothing (ACC-01).
/// </summary>
/// <remarks>
/// <b>Three cases, one visible answer.</b> Doc 08 §3.1 DB10: "Register with an existing email (either
/// type) gives the same generic response. A customer match receives an 'account already exists, sign in
/// or reset' email." So a free address creates an account and queues two messages, a taken customer
/// address creates nothing and queues one, a staff address creates nothing and queues nothing — and the
/// caller gets an enum it is forbidden to turn into three different replies.
/// <para>
/// <b>Nothing is sent to the shopper's screen about it, and the account is <i>not</i> signed in.</b>
/// Signing in on success would itself be the enumeration oracle the generic sentence exists to close: a
/// response that sometimes carries a session and sometimes does not says whether the address was free.
/// Doc 11 P11 keeps step B (sign in) separate for that reason, and §7 G-24 lets the new customer sign in
/// straight away without confirming anything.
/// </para>
/// </remarks>
internal sealed class CustomerRegistrationStore(
    AppDbContext dbContext,
    UserManager<AppUser> userManager,
    CustomerUserLookup lookup,
    IAccountEmailOutbox accountEmails,
    IClock clock,
    IOptions<CustomerSecurityOptions> options,
    ILogger<CustomerRegistrationStore> logger) : ICustomerRegistration
{
    public async Task<CustomerRegistrationOutcome> RegisterAsync(
        CustomerRegistration registration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var owner = await lookup.ClassifyAsync(registration.Email, cancellationToken);

        return owner switch
        {
            CustomerUserLookup.EmailOwner.Staff => StaffAddress(),
            CustomerUserLookup.EmailOwner.Customer =>
                await QueueAccountExistsAsync(registration, cancellationToken),
            _ => await CreateAsync(registration, cancellationToken),
        };
    }

    public async Task<bool> ResendConfirmationAsync(int userId, CancellationToken cancellationToken)
    {
        var user = await lookup.FindCustomerByIdAsync(userId, cancellationToken);
        if (user is null || user.EmailConfirmed || user.Email is null)
        {
            return false;
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var fresh = await lookup.FindCustomerByIdAsync(userId, cancellationToken);
            if (fresh is null || fresh.EmailConfirmed || fresh.Email is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            var queued = await MintAndQueueConfirmationAsync(fresh, cancellationToken);

            if (!queued)
            {
                // No storefront origin, so no link, so nothing was queued and the freshly minted token
                // would be a credential nobody can use that had replaced one somebody might still hold.
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }

    private CustomerRegistrationOutcome StaffAddress()
    {
        // Doc 08 §3.1 DB10: "a staff match sends nothing". Logged at information level because it is
        // either a staff member who forgot doc 08 D7 and needs telling, or somebody probing — and
        // neither is visible anywhere else, since no message is sent and no row is written.
        CustomerAccountLog.RegistrationBelongsToStaff(logger);

        return CustomerRegistrationOutcome.BelongsToStaff;
    }

    /// <summary>
    /// Doc 11 P11 step A.2's other half: the address already has a customer account, so it gets the
    /// <c>AccountExists</c> message and nothing is created.
    /// </summary>
    /// <remarks>
    /// <b>Only an <i>active</i> customer is written to.</b> <see cref="CustomerUserLookup.ClassifyAsync"/>
    /// answers <c>Customer</c> for a deactivated or anonymised row too — the address is taken either way,
    /// which is what registration needs to know — but an anonymised row's address is
    /// <c>anon-{id}@anonymised.invalid</c> and writing to it would be a message about somebody who asked
    /// to be forgotten.
    /// </remarks>
    private async Task<CustomerRegistrationOutcome> QueueAccountExistsAsync(
        CustomerRegistration registration,
        CancellationToken cancellationToken)
    {
        var existing = await lookup.FindCustomerByEmailAsync(registration.Email, cancellationToken);

        if (existing?.Email is null)
        {
            return CustomerRegistrationOutcome.AlreadyACustomer;
        }

        accountEmails.EnlistAccountExists(new AccountEmailFacts(
            new AccountEmailAddressee(existing.Email, existing.FirstName, existing.LastName),
            Link: null,
            clock.UtcNow.UtcDateTime));

        await dbContext.SaveChangesAsync(cancellationToken);

        return CustomerRegistrationOutcome.AlreadyACustomer;
    }

    /// <summary>
    /// Doc 11 P11 step A.3: the account, its confirmation token and its two messages, in one transaction.
    /// </summary>
    /// <remarks>
    /// <b>One transaction, because a half-registered account is worse than none.</b> An account with no
    /// confirmation token can never link its guest orders and has no way to ask for a new link except the
    /// resend; a queued confirmation for a user row that rolled back is a link to nothing. Doc 08 §3.8's
    /// transactional outbox is the same argument from the other side.
    /// <para>
    /// <b>The unique index is the race winner, not the classify read above.</b> Two simultaneous
    /// registrations for one free address both see <see cref="CustomerUserLookup.EmailOwner.Nobody"/>;
    /// <c>UX_Users_NormalizedEmail</c> then refuses the second, and
    /// <see cref="IsDuplicateAddress"/> turns that into the <i>same</i> outcome the classify read would
    /// have produced — so the loser behaves exactly like somebody registering a taken address, including
    /// the generic reply. Without that mapping the loser would get a 500, which both leaks the collision
    /// and loses the shopper.
    /// </para>
    /// </remarks>
    private async Task<CustomerRegistrationOutcome> CreateAsync(
        CustomerRegistration registration,
        CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var email = registration.Email.Trim();
            var nowUtc = clock.UtcNow.UtcDateTime;

            var user = new AppUser
            {
                UserType = UserType.Customer,
                UserName = email,
                Email = email,
                FirstName = registration.FirstName.Trim(),
                LastName = string.IsNullOrWhiteSpace(registration.LastName) ? null : registration.LastName.Trim(),
                PhoneNumber = string.IsNullOrWhiteSpace(registration.Phone) ? null : registration.Phone.Trim(),

                // Doc 11 §7 G-24: confirmation gates guest-order linking and nothing else, so the account
                // starts unconfirmed and usable.
                EmailConfirmed = false,

                IsActive = true,

                // A customer never has a forced password change: there is no administrator who sets their
                // first password for them (doc 08 §3.1's MustChangePassword is the staff-invite path).
                MustChangePassword = false,

                // Doc 11 P11 step B.5's lockout has to be able to apply.
                LockoutEnabled = true,
            };

            IdentityResult created;

            try
            {
                created = await userManager.CreateAsync(user, registration.Password);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // Identity's own validator pre-checks the address with a read, so almost every duplicate
                // comes back as an IdentityResult below. This is the genuine race getting past that read
                // and being refused by UX_Users_NormalizedEmail instead.
                await transaction.RollbackAsync(cancellationToken);
                return await LostTheRaceAsync(registration, cancellationToken);
            }

            if (!created.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);

                if (IsDuplicateAddress(created))
                {
                    return await LostTheRaceAsync(registration, cancellationToken);
                }

                // Every other failure is a password or field rule, and the caller validated those before
                // calling. Reaching here means the two disagree, which is a defect rather than an input
                // problem, so it is not turned into a generic reply.
                throw new InvalidOperationException(
                    "Creating the customer account failed for a reason the use case had already "
                    + $"validated: {string.Join("; ", created.Errors.Select(error => error.Code))}. "
                    + "The validator and IdentityOptions disagree.");
            }

            var queuedConfirmation = await MintAndQueueConfirmationAsync(user, cancellationToken);

            // Doc 11 P11 step A.3 queues Welcome as well, and it is queued whether or not the
            // confirmation was: it carries no link that is a credential, so it is useful on its own, and
            // its body deliberately does not mention the confirmation message (see AccountEmailComposer).
            accountEmails.EnlistWelcome(new AccountEmailFacts(
                new AccountEmailAddressee(email, user.FirstName, user.LastName),
                Link: null,
                nowUtc));

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (!queuedConfirmation)
            {
                // The one state an operator has to know about: the account exists, and the customer has
                // no way to confirm it, so doc 08 §3.1 D5's guest-order linking cannot happen for them.
                // Warning rather than error: the shop is usable and this is a configuration gap, not a
                // fault. It is in the task report as the deployment consequence of an unset origin.
                CustomerAccountLog.RegistrationWithoutConfirmationLink(logger, user.Id);
            }

            return CustomerRegistrationOutcome.Created;
        });
    }

    /// <summary>
    /// Mints a single-use confirmation token into <c>identity.UserTokens</c> and queues the message.
    /// </summary>
    /// <remarks>
    /// <b>The token is minted against the address as it is <i>now</i></b>, and the row records that
    /// address, so a link emailed to one address cannot confirm a different one the account has moved to
    /// since. Doc 11 P11 step 10's email change is not built, and this is the half of it that is cheap to
    /// get right in advance rather than retrofit.
    /// <para>
    /// <b>Minting replaces any token already on the account</b> — the <c>UserTokens</c> primary key is
    /// <c>(UserId, LoginProvider, Name)</c> — so a customer who presses "send me a new link" twice ends
    /// up with exactly one link that works, which is the only answer that is not confusing.
    /// </para>
    /// </remarks>
    private async Task<bool> MintAndQueueConfirmationAsync(AppUser user, CancellationToken cancellationToken)
    {
        var expiresAtUtc = clock.UtcNow.UtcDateTime.AddHours(options.Value.EmailConfirmationTokenHours);
        var normalised = lookup.Normalise(user.Email)
            ?? throw new InvalidOperationException("A customer account cannot be created without an email address.");

        var minted = CustomerAccountTokens.Mint(user.Id, expiresAtUtc, normalised);

        var set = await userManager.SetAuthenticationTokenAsync(
            user,
            CustomerAccountTokens.LoginProvider,
            CustomerAccountTokens.EmailConfirmationName,
            minted.StoredValue);

        if (!set.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not store the email-confirmation token: "
                + string.Join("; ", set.Errors.Select(error => error.Description)));
        }

        return accountEmails.EnlistEmailConfirmation(
            new AccountEmailFacts(
                new AccountEmailAddressee(user.Email!, user.FirstName, user.LastName),
                Link: null,
                clock.UtcNow.UtcDateTime)
            {
                LinkValidHours = options.Value.EmailConfirmationTokenHours,
            },
            minted.Link);
    }

    /// <summary>
    /// The address was taken after all. Behaves exactly as if the classify read had seen the winner:
    /// nothing created, and the winner is the one who hears about it.
    /// </summary>
    /// <remarks>
    /// <b><c>ChangeTracker.Clear()</c> first, and it is load-bearing.</b> The failed attempt left an
    /// <c>Added</c> <c>AppUser</c> in the tracker; the transaction is rolled back, so the row is gone from
    /// the database but not from the tracker, and the <c>SaveChanges</c> inside
    /// <see cref="QueueAccountExistsAsync"/> would try to insert it again — straight into the same unique
    /// violation, this time with nothing to catch it.
    /// </remarks>
    private async Task<CustomerRegistrationOutcome> LostTheRaceAsync(
        CustomerRegistration registration,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        return await QueueAccountExistsAsync(registration, cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="result"/> is Identity refusing a duplicate address.
    /// </summary>
    /// <remarks>
    /// <b>Both codes, because <c>UserName</c> and <c>Email</c> are the same string here.</b>
    /// <c>IdentityOptions.User.RequireUniqueEmail</c> is on, so Identity's own pre-check raises
    /// <c>DuplicateEmail</c>, and a value that collides on the user name raises the other.
    /// </remarks>
    private static bool IsDuplicateAddress(IdentityResult result) =>
        result.Errors.Any(error =>
            error.Code is "DuplicateEmail" or "DuplicateUserName");

    /// <summary>
    /// Whether <paramref name="exception"/> is SQL Server refusing a duplicate key: 2627 (unique
    /// constraint) or 2601 (unique index).
    /// </summary>
    /// <remarks>
    /// The same two numbers <c>ICartStore.AddItemAsync</c> retries on, and the same reason for naming
    /// them rather than catching every <c>DbUpdateException</c>: a timeout or a check-constraint failure
    /// is not a lost race and must not be answered as one.
    /// </remarks>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };
}
