using SpencerDenim.Application.Catalog.Admin.Abstractions;

namespace SpencerDenim.Application.Catalog.Admin;

/// <summary>
/// The two decisions every ADM-11 write makes the same way: is the caller's row version current, and what
/// did the database make of the save.
/// </summary>
internal static class CatalogWriteSupport
{
    /// <summary>
    /// A conflict when the caller's token is not the row's current one, otherwise <c>null</c>.
    /// <para>
    /// This is the <i>first</i> of two concurrency checks and it earns its place: it turns "somebody else
    /// edited this while your form was open" into a clear answer before anything is written, whereas the
    /// <c>RowVersion</c> predicate EF puts on the UPDATE can only fire for a change that lands between this
    /// handler's read and its save. Keeping both means the common case gets the good message and the narrow
    /// race still cannot overwrite anybody.
    /// </para>
    /// <para>
    /// A missing or malformed token is refused too, rather than taken as "no opinion". A client that forgot
    /// to send one is a client that cannot have seen the current row, and silently allowing the write is how
    /// optimistic concurrency becomes decorative.
    /// </para>
    /// </summary>
    public static CatalogWriteResult.Conflict? CheckRowVersion(byte[]? current, string? supplied)
    {
        var expected = CatalogFieldRules.ReadRowVersion(supplied);

        if (expected is not null && current is not null && expected.AsSpan().SequenceEqual(current))
        {
            return null;
        }

        return new CatalogWriteResult.Conflict(
            CatalogConflictCodes.StaleRowVersion,
            expected is null
                ? "This request did not carry the row's current version, so it cannot be applied safely. " +
                  "Reload the page and try again."
                : "Somebody else changed this while you were editing it. Reload the page to see their " +
                  "change, then re-apply yours.");
    }

    /// <summary>Turns the store's outcome into the use case's answer.</summary>
    public static CatalogWriteResult ToResult(CatalogSaveOutcome outcome, int entityId) => outcome switch
    {
        CatalogSaveOutcome.Saved => new CatalogWriteResult.Succeeded(entityId),

        CatalogSaveOutcome.NothingToSave => new CatalogWriteResult.Unchanged(entityId),

        CatalogSaveOutcome.ConcurrencyConflict => new CatalogWriteResult.Conflict(
            CatalogConflictCodes.StaleRowVersion,
            "Somebody else changed this while your change was being saved, so nothing was written. " +
            "Reload the page and try again."),

        CatalogSaveOutcome.DuplicateKey => new CatalogWriteResult.Conflict(
            CatalogConflictCodes.DuplicateKey,
            "Somebody else has just used that name or URL slug. Choose another one and try again."),

        _ => throw new InvalidOperationException($"Unhandled save outcome '{outcome}'."),
    };
}
