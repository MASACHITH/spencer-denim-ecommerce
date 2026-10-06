using Microsoft.Data.SqlClient;

namespace SpencerDenim.Infrastructure.Persistence;

/// <summary>
/// "Somebody else inserted this row first", as SQL Server reports it.
/// </summary>
/// <remarks>
/// <b>A unique violation on a row whose job is to exist is success, not failure.</b> Four places act on
/// that — <c>NumberSeriesAllocator</c>'s lazy series insert, <c>CartStore</c>'s cart and item upserts,
/// <c>OrderPlacementStore</c>'s idempotency key, and <c>CatalogSeedHostedService</c>'s reference-data
/// pass — and each of the first three carried its own byte-identical private copy of this predicate.
/// <para>
/// <b>It is now actually shared</b>, which it was not when it was introduced: it had one caller and the
/// word "shared" in its own documentation, which the W3-02 round-3 tester rightly called out. The three
/// copies are gone and the call sites read <c>SqlUniqueViolation.Matches(…)</c>. Nothing about the
/// behaviour of the money or stock paths changed — the predicate is the same comparison against the same
/// two error numbers, and the concurrency suite that proves those paths is what says so.
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
