using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Accounts.Abstractions;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Identity.Customers;

/// <summary>
/// Doc 11 P11 step A.4: confirms a customer's address and links the guest orders behind it (doc 08 §3.1
/// <b>D5</b>).
/// </summary>
/// <remarks>
/// <b>Doc 08 §3.1 D5, verbatim:</b> "only after email confirmation,
/// <c>UPDATE sales.Orders SET CustomerUserId = @u WHERE EmailNormalized = @e AND CustomerUserId IS
/// NULL</c>. The linked order IDs are written to AuditLogs (Action <c>LinkGuestOrders</c>)." That is one
/// statement and it is the whole of the linking; what this class adds is the three guards that make it
/// safe to run.
/// <list type="number">
/// <item><description>
/// <b><c>CustomerUserId IS NULL</c> is in the predicate, so an order that already belongs to somebody is
/// never taken from them.</b> It is the clause that makes the statement idempotent and the clause that
/// makes it safe at all: without it, two accounts registered with one address at different times would
/// each claim the other's orders on confirmation. Doc 11 P11's own exception says the same thing from the
/// customer's side — "Customer changes email → orders placed under the old email stay linked (they
/// already have the account ID)".
/// </description></item>
/// <item><description>
/// <b>The address matched is the one the <i>token</i> was minted for, not the account's current
/// address.</b> <see cref="CustomerAccountTokens"/> stores it in the token row for this reason. Doc 11
/// P11's next exception is the case: "guest orders under the new email link after confirming it" — so a
/// link emailed to one address must claim that address's orders and no others, however many times the
/// account has moved since.
/// </description></item>
/// <item><description>
/// <b>Confirmation and linking are one transaction.</b> A confirmed address with its orders unlinked
/// looks to the customer exactly like the feature not working, and there is no second chance: the token
/// is spent. An audit row for a link that rolled back is worse.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class CustomerEmailConfirmationStore(
    AppDbContext dbContext,
    UserManager<AppUser> userManager,
    CustomerUserLookup lookup,
    IAuditLogWriter auditLog,
    IClock clock,
    ILogger<CustomerEmailConfirmationStore> logger) : ICustomerEmailConfirmation
{
    /// <summary>Doc 08 §3.8's <c>EntityType</c> for a user row.</summary>
    private const string UserEntityType = "User";

    public async Task<CustomerEmailConfirmationResult> ConfirmAsync(
        string token,
        int signedInUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        // **The token must name the account that is signed in** (Blocker 2). Checked on the selector,
        // before any row is read, so a token presented from somebody else's session is refused without
        // being spent — a stranger cannot burn the owner's link — and so the mailbox proof and the
        // credentials proof are always for the same account.
        if (CustomerAccountTokens.ReadUserId(token) is not { } userId || userId != signedInUserId)
        {
            return new CustomerEmailConfirmationResult.InvalidToken();
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var user = await lookup.FindCustomerByIdAsync(userId, cancellationToken);
            if (user?.Email is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (CustomerEmailConfirmationResult)new CustomerEmailConfirmationResult.InvalidToken();
            }

            var stored = await userManager.GetAuthenticationTokenAsync(
                user,
                CustomerAccountTokens.LoginProvider,
                CustomerAccountTokens.EmailConfirmationName);

            var verified = CustomerAccountTokens.Verify(token, stored);

            if (verified is null)
            {
                // **The row is deliberately left alone.** An earlier version removed it here, and this
                // test caught what that meant: a *stale* link — one the customer replaced by asking for
                // a new one — would delete the row holding the **new** token, so opening the old message
                // killed the live link. Worse, anyone holding any old link, or guessing at the shape of
                // one, could invalidate a stranger's pending confirmation. A token that does not verify
                // is not a token this account has, and nothing about it is ours to spend.
                await transaction.RollbackAsync(cancellationToken);
                return (CustomerEmailConfirmationResult)new CustomerEmailConfirmationResult.InvalidToken();
            }

            var redeemed = verified.Value;

            if (redeemed.ExpiresAtUtc <= clock.UtcNow.UtcDateTime || redeemed.Subject is null)
            {
                // Here the verifier *did* match, so this is the account's own token and spending it is
                // right: a dead credential should not sit in the table until something else happens to
                // the account. The answer is the same as for a wrong token either way — saying "expired"
                // rather than "wrong" would tell a holder it was once real.
                await SpendTokenAsync(userId, stored, cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return (CustomerEmailConfirmationResult)new CustomerEmailConfirmationResult.InvalidToken();
            }

            // The token must still be about the account's current address. A customer whose address has
            // moved on since the link was sent gets "invalid" rather than a confirmation of an address
            // they no longer use — which would set `EmailConfirmed` on a *different* address and link
            // nothing, the worst of the three possible answers.
            if (!string.Equals(redeemed.Subject, lookup.Normalise(user.Email), StringComparison.Ordinal))
            {
                await SpendTokenAsync(userId, stored, cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                CustomerAccountLog.ConfirmationForStaleAddress(logger, userId);

                return (CustomerEmailConfirmationResult)new CustomerEmailConfirmationResult.InvalidToken();
            }

            // Claim first: two tabs on one link must produce one confirmation and one audit row.
            if (await SpendTokenAsync(userId, stored, cancellationToken) == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (CustomerEmailConfirmationResult)new CustomerEmailConfirmationResult.InvalidToken();
            }

            // Set-based, so no other column of a user instance loaded earlier in this request is written
            // back with it — #146's second finding, which applies to every save of a user row and not
            // only to the lockout path. `userManager.SetEmailConfirmedAsync` would write all of them.
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE [identity].[Users]
                SET [EmailConfirmed] = 1, [UpdatedAtUtc] = {clock.UtcNow.UtcDateTime}
                WHERE [Id] = {userId} AND [UserType] = {(byte)UserType.Customer}
                """,
                cancellationToken);

            var linked = await LinkGuestOrdersAsync(userId, redeemed.Subject, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            CustomerAccountLog.EmailConfirmed(logger, userId, linked.Count);

            return (CustomerEmailConfirmationResult)new CustomerEmailConfirmationResult.Confirmed(
                new CustomerSession(
                    user.Id,
                    user.Email,
                    user.FirstName,
                    user.LastName,
                    EmailConfirmed: true,
                    await userManager.GetSecurityStampAsync(user)),
                linked.ConvertAll(order => order.OrderNumber));
        });
    }

    /// <summary>
    /// Doc 08 §3.1 D5's one statement, plus the audit row it requires.
    /// </summary>
    /// <remarks>
    /// <b>Read then update, and the read is what the audit row is built from.</b> D5 asks for "the linked
    /// order IDs" in the audit trail, and a bare <c>ExecuteUpdate</c> returns only a count. Reading the
    /// ids first inside the same transaction and then updating <i>that set</i> keeps the two in step: the
    /// update's predicate still carries <c>CustomerUserId IS NULL</c>, so a concurrent linker cannot make
    /// this one claim an order it did not read, and the count is compared against the list.
    /// <para>
    /// <b>Why <c>EmailNormalized</c> and not <c>Email</c>.</b> Doc 08 §3.1 D5 names the normalised
    /// column, and <c>sales.Orders</c> carries it for this. A shopper who checked out as
    /// <c>Nimal.Perera@example.com</c> and registered as <c>nimal.perera@example.com</c> is the same
    /// person, and matching the display column would silently not link their orders — the exact defect
    /// this feature exists to prevent, wearing a different hat.
    /// </para>
    /// <para>
    /// <b>Newest first</b>, because that is the order the shopper is shown and the order the history page
    /// uses; a list in primary-key order would read as arbitrary in the one message that tells a customer
    /// the feature worked.
    /// </para>
    /// </remarks>
    private async Task<List<LinkedOrder>> LinkGuestOrdersAsync(
        int userId,
        string emailNormalized,
        CancellationToken cancellationToken)
    {
        var candidates = await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.EmailNormalized == emailNormalized && order.CustomerUserId == null)
            .OrderByDescending(order => order.PlacedAtUtc)
            .ThenByDescending(order => order.Id)
            .Select(order => new LinkedOrder(order.Id, order.OrderNumber))
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return candidates;
        }

        var ids = candidates.ConvertAll(order => order.Id);

        var updated = await dbContext.Orders
            .Where(order => ids.Contains(order.Id) && order.CustomerUserId == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(order => order.CustomerUserId, userId),
                cancellationToken);

        if (updated != candidates.Count)
        {
            // Somebody else linked one of these between the read and the update. It cannot happen through
            // this class — the token is claimed before this runs, and there is one token per account —
            // but it can happen through a future admin action, and an audit row naming an order this
            // transaction did not actually link would be a false record. Rather than guess which, the
            // whole transaction is abandoned: the token's claim rolls back with it, so the customer's
            // link still works and the next attempt sees a settled world.
            throw new DbUpdateConcurrencyException(
                $"Guest-order linking for customer {userId} read {candidates.Count} unlinked order(s) and "
                + $"updated {updated}. Another writer claimed one in between; the transaction is rolled "
                + "back so the confirmation link can be used again.");
        }

        // Doc 08 §3.1 D5: "The linked order IDs are written to AuditLogs (Action LinkGuestOrders)."
        // Enlisted, not written: the trail and the change it describes commit together (issue #59).
        //
        // The order *numbers* go in the JSON as well as the ids. An id is what the rule names and what a
        // later query joins on; a number is what a staff member reading the trail can type into the admin
        // search. **The email is not recorded at all**: UserEmail is null and the JSON carries none.
        // UserId names the account, which is enough to find its address on identity.Users for as long as
        // the account exists, and leaves this row holding no address for PDPA anonymisation to scrub.
        // (An earlier version of this comment said "the row's own UserEmail carries it", which the code
        // below has never done — independent tester, W3-01.)
        auditLog.Enlist(new AuditLog
        {
            OccurredAtUtc = clock.UtcNow.UtcDateTime,
            UserId = userId,
            UserEmail = null,
            Action = AuditActions.LinkGuestOrders,
            EntityType = UserEntityType,
            EntityId = userId.ToString(CultureInfo.InvariantCulture),
            BeforeJson = null,
            AfterJson = JsonSerializer.Serialize(new
            {
                orderIds = candidates.ConvertAll(order => order.Id),
                orderNumbers = candidates.ConvertAll(order => order.OrderNumber),
            }),
        });

        return candidates;
    }

    /// <summary>
    /// Deletes the confirmation token row, restricted to the exact value that was read. The affected row
    /// count is the claim (#146's pattern).
    /// </summary>
    private async Task<int> SpendTokenAsync(int userId, string? storedValue, CancellationToken cancellationToken) =>
        storedValue is null
            ? 0
            : await dbContext.UserTokens
                .Where(row =>
                    row.UserId == userId
                    && row.LoginProvider == CustomerAccountTokens.LoginProvider
                    && row.Name == CustomerAccountTokens.EmailConfirmationName
                    && row.Value == storedValue)
                .ExecuteDeleteAsync(cancellationToken);

    private readonly record struct LinkedOrder(int Id, string OrderNumber);
}
