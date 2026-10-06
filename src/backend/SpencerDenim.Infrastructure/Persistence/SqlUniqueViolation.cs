using Microsoft.Data.SqlClient;

namespace SpencerDenim.Infrastructure.Persistence;

/// <summary>
/// "Somebody else inserted this row first", as SQL Server reports it.
/// </summary>
/// <remarks>
/// <b>A unique violation on a row whose job is to exist is success, not failure.</b> Three places in this
/// codebase already act on that — <c>NumberSeriesAllocator</c>'s lazy series insert,
/// <c>CartStore</c>'s cart and item upserts, and <c>OrderPlacementStore</c>'s idempotency key — and each
/// carries its own private copy of this predicate. This is the shared one, added for
/// <c>CatalogSeedHostedService</c> rather than as a fourth copy.
/// <para>
/// The three existing copies are <b>deliberately left alone</b> in this round: they sit inside the money
/// and stock paths, which this task was told not to refactor. Adopting this helper there is a
/// one-line-per-file change for whoever next touches them.
/// </para>
/// </remarks>
internal static class SqlUniqueViolation
{
    /// <summary>SQL Server: 2627 is a PK/unique-constraint violation, 2601 a unique-index violation.</summary>
    private static readonly int[] ErrorNumbers = [2627, 2601];

    /// <summary>
    /// Whether this exception, or anything it wraps, is a unique-key violation. EF Core buries the
    /// <see cref="SqlException"/> inside a <c>DbUpdateException</c>, so the inner chain has to be walked.
    /// </summary>
    public static bool Matches(Exception? exception) =>
        exception switch
        {
            null => false,
            SqlException sql => sql.Errors.Cast<SqlError>().Any(error => ErrorNumbers.Contains(error.Number)),
            _ => Matches(exception.InnerException),
        };
}
