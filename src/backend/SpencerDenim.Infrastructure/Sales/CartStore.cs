using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales;

/// <summary>
/// <c>sales.Carts</c> / <c>sales.CartItems</c> writes, as doc 08 §3.5 (DB22) specifies them: item-level
/// upserts, single-statement quantity changes and removals, and a one-column touch for the inactivity
/// clock. No rowversion, and nothing here reads a row in order to write it back.
/// <para>
/// <b>Why raw SQL and not EF change tracking.</b> The whole point of DB22 is that the arithmetic happens
/// inside one statement: <c>Quantity = IIF(Quantity + @q &gt; 10, 10, Quantity + @q)</c> is evaluated by
/// the server against whatever the row holds at that instant. Doing it in C# — load, add, clamp, save —
/// reintroduces exactly the read-then-write window that a cart has no rowversion to protect, and two
/// taps on Add to Bag would lose one of them. Doc 08 §1.6 also requires cart deletes to be set-based
/// (N12), never tracked <c>Remove()</c>.
/// </para>
/// </summary>
internal sealed class CartStore(AppDbContext dbContext, IClock clock) : ICartStore
{
    /// <summary>
    /// How many times a losing race is retried. The loser of an insert race gets 2627/2601 and its retry
    /// finds the row and updates it, so one retry is already enough; three leaves room for the (unlikely)
    /// case of the winner's row being removed in between, and bounds the loop so a pathological state
    /// cannot spin.
    /// </summary>
    private const int UniqueViolationAttempts = 3;

    /// <summary>SQL Server: 2627 is a PK/unique-constraint violation, 2601 a unique-index violation.</summary>
    private static readonly int[] UniqueViolationErrors = [2627, 2601];

    public async Task<CartIdentity?> FindAsync(CartLookup lookup, CancellationToken cancellationToken)
    {
        // A signed-in customer's cart is found by CustomerUserId and never by whatever token they happen
        // to be carrying. That is the whole of the "someone sends another person's token" defence: a
        // token can only ever reach a cart whose CustomerUserId IS NULL, so an account cart is
        // unreachable with a stolen or guessed token and a guest token cannot be used to adopt one.
        if (lookup.CustomerUserId is { } customerUserId)
        {
            return await dbContext.Carts
                .AsNoTracking()
                .Where(cart => cart.CustomerUserId == customerUserId)
                .Select(cart => (CartIdentity?)new CartIdentity(cart.Id, cart.Token, cart.CustomerUserId))
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (lookup.GuestToken is not { } token || token == Guid.Empty)
        {
            return null;
        }

        return await dbContext.Carts
            .AsNoTracking()
            .Where(cart => cart.Token == token && cart.CustomerUserId == null)
            .Select(cart => (CartIdentity?)new CartIdentity(cart.Id, cart.Token, cart.CustomerUserId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<CartIdentity> EnsureAsync(CartLookup lookup, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var existing = await FindAsync(lookup, cancellationToken);
            if (existing is not null)
            {
                return existing.Value;
            }

            try
            {
                return await InsertCartAsync(lookup.CustomerUserId, cancellationToken);
            }
            catch (DbUpdateException exception)
                when (IsUniqueViolation(exception) && attempt < UniqueViolationAttempts)
            {
                // Two requests for the same customer raced to create the account cart and
                // UX_Carts_Customer refused the second. The loop re-reads and finds the winner's row,
                // which is the whole reason that filtered index is in the migration.
            }
        }
    }

    public async Task AddItemAsync(
        int cartId,
        int variantId,
        short quantity,
        decimal unitPriceAtAdd,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.UtcDateTime;
        const short max = CartItem.MaxQuantity;

        for (var attempt = 1; ; attempt++)
        {
            // Doc 08 §3.5 (DB22) exactly: UPDATE first, INSERT only when it matched nothing. The clamp
            // is inside the UPDATE, so two adds arriving together sum and cap in the engine — never in
            // application code that read the row a moment ago.
            var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE sales.CartItems
                   SET Quantity = IIF(Quantity + {quantity} > {max}, {max}, Quantity + {quantity}),
                       UpdatedAtUtc = {now}
                 WHERE CartId = {cartId} AND VariantId = {variantId};
                """,
                cancellationToken);

            if (updated > 0)
            {
                return;
            }

            try
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO sales.CartItems (CartId, VariantId, Quantity, UnitPriceAtAdd, AddedAtUtc)
                    VALUES ({cartId}, {variantId}, {quantity}, {unitPriceAtAdd}, {now});
                    """,
                    cancellationToken);

                return;
            }
            catch (SqlException exception)
                when (IsUniqueViolation(exception) && attempt < UniqueViolationAttempts)
            {
                // Somebody else inserted the same (CartId, VariantId) between our UPDATE and our INSERT.
                // UX_CartItems_Cart_Variant is what turned that into an error instead of a second row,
                // and the next pass's UPDATE adds our quantity to theirs. This is the branch that makes
                // two simultaneous adds end at one row holding both quantities.
            }
        }
    }

    public async Task<bool> SetQuantityAsync(
        int cartId,
        int variantId,
        short quantity,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.UtcDateTime;

        var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.CartItems
               SET Quantity = {quantity}, UpdatedAtUtc = {now}
             WHERE CartId = {cartId} AND VariantId = {variantId};
            """,
            cancellationToken);

        return updated > 0;
    }

    public async Task<bool> RemoveItemAsync(int cartId, int variantId, CancellationToken cancellationToken)
    {
        // Set-based, as doc 08 §1.6 (N12) requires for cart deletes: no tracked Remove().
        var deleted = await dbContext.CartItems
            .Where(item => item.CartId == cartId && item.VariantId == variantId)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted > 0;
    }

    public async Task TouchAsync(int cartId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.UtcDateTime;

        await dbContext.Carts
            .Where(cart => cart.Id == cartId)
            .ExecuteUpdateAsync(cart => cart.SetProperty(row => row.LastActivityAtUtc, now), cancellationToken);
    }

    public async Task<CartMergeOutcome> MergeGuestIntoCustomerAsync(
        Guid guestToken,
        int customerUserId,
        CancellationToken cancellationToken)
    {
        if (guestToken == Guid.Empty)
        {
            return new CartMergeOutcome(0, false);
        }

        // **Everything is inside the retried lambda, and that is three fixes rather than a tidy-up.**
        //
        // 1. *The snapshot could go stale.* The guest lines used to be read before the transaction began,
        //    and the guest cart is then deleted by id — which cascades to **all** its items, including any
        //    added between the read and the delete. The transaction protected a partial failure and not
        //    this, because nothing locked the rows the snapshot came from. The read is now inside a
        //    **serialisable** transaction, so the range lock it takes on `UX_CartItems_Cart_Variant` for
        //    this `CartId` blocks a concurrent insert until the merge commits: a line either arrives first
        //    and is moved, or is refused outright. It can no longer arrive and be destroyed.
        //
        // 2. *A failure could leave a stray account cart.* `EnsureAsync` ran outside the transaction, so a
        //    rollback left an empty cart for a customer who had never added anything — which doc 08 §3.5
        //    says should not exist ("created on the first Add to Bag, never on a page view"). It is inside
        //    now, so it is rolled back with everything else.
        //
        // 3. *A replay could double the quantities.* `AddItemAsync` is additive, so if the execution
        //    strategy re-ran this after a lost commit acknowledgement, every moved line would be added a
        //    second time. With the guest lookup inside, a replay after a commit that really landed finds no
        //    guest cart and does nothing. The operation is idempotent instead of merely transactional.
        //
        // Serialisable is affordable here precisely because this is rare: one sign-in, two tiny tables.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var guest = await FindAsync(CartLookup.ForGuest(guestToken), cancellationToken);
            if (guest is null)
            {
                // Nothing to merge — or a replay of a merge that already committed.
                await transaction.RollbackAsync(cancellationToken);
                return new CartMergeOutcome(0, false);
            }

            var customer = await EnsureAsync(CartLookup.ForCustomer(customerUserId, null), cancellationToken);
            if (customer.Id == guest.Value.Id)
            {
                // Unreachable with the lookup above (it refuses a cart that has a CustomerUserId), but a
                // merge that moved a cart into itself would delete the shopper's bag, so it is checked
                // rather than reasoned about.
                await transaction.RollbackAsync(cancellationToken);
                return new CartMergeOutcome(0, false);
            }

            var lines = await dbContext.CartItems
                .AsNoTracking()
                .Where(item => item.CartId == guest.Value.Id)
                .OrderBy(item => item.Id)
                .Select(item => new { item.VariantId, item.Quantity, item.UnitPriceAtAdd })
                .ToListAsync(cancellationToken);

            var moved = 0;
            foreach (var line in lines)
            {
                // The same upsert as Add to Bag, so a variant in both bags sums and clamps at 10. Doc 08
                // §3.5 also caps the merge "at Available"; it is not capped here because stock is not
                // reserved in a cart (§3.5) and the bag is priced and checked at read time — a line over
                // stock is shown as NotEnoughStock rather than silently trimmed, which is the rule the
                // rest of the bag follows. Checkout (W3-02) is where stock is actually claimed.
                await AddItemAsync(customer.Id, line.VariantId, line.Quantity, line.UnitPriceAtAdd, cancellationToken);
                moved++;
            }

            await dbContext.Carts
                .Where(cart => cart.Id == guest.Value.Id)
                .ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new CartMergeOutcome(moved, true);
        });
    }

    private async Task<CartIdentity> InsertCartAsync(int? customerUserId, CancellationToken cancellationToken)
    {
        // The token is generated here rather than left to the column default, so the caller gets the
        // value without a second read. Guid.NewGuid is a v4 GUID — 122 random bits, which is what doc 08
        // §1.3 means by "unguessable public token".
        var token = Guid.NewGuid();
        var now = clock.UtcNow.UtcDateTime;

        var cart = new Cart
        {
            Token = token,
            CustomerUserId = customerUserId,
            CreatedAtUtc = now,
            LastActivityAtUtc = now,
        };

        dbContext.Carts.Add(cart);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            // The insert is the only thing this context tracks; leaving a failed entry attached would
            // make the retry's SaveChanges try it again.
            dbContext.Entry(cart).State = EntityState.Detached;
        }

        return new CartIdentity(cart.Id, token, customerUserId);
    }

    private static bool IsUniqueViolation(Exception exception) =>
        exception is SqlException sql
            ? sql.Errors.Cast<SqlError>().Any(error => UniqueViolationErrors.Contains(error.Number))
            : exception.InnerException is not null && IsUniqueViolation(exception.InnerException);
}
