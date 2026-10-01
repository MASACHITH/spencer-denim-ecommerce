using SpencerDenim.Application.Abstractions.Results;

namespace SpencerDenim.Application.Catalog.Admin;

/// <summary>
/// What an ADM-11 write use case decided. One type for all seven of them, so the endpoints share one
/// mapping to HTTP and a new command cannot invent a new way to say "refused".
/// <para>
/// Deliberately not <see cref="Result{TValue}"/>: that type carries validation errors only, and these use
/// cases have three other answers a client has to tell apart — the row is gone (404), somebody else got
/// there first or something still points at it (409), and nothing was different (no change, no audit row).
/// Folding a conflict into a 400 would tell an admin to fix a field when what they need to do is reload.
/// </para>
/// </summary>
public abstract record CatalogWriteResult
{
    private CatalogWriteResult()
    {
    }

    /// <summary>The write happened. <paramref name="EntityId"/> is the row's id, new or existing.</summary>
    public sealed record Succeeded(int EntityId) : CatalogWriteResult;

    /// <summary>
    /// Every value was already what the request asked for, so no statement was issued: the row version
    /// does not move and no audit row is written.
    /// </summary>
    public sealed record Unchanged(int EntityId) : CatalogWriteResult;

    /// <summary>Rejected input, one entry per field, so the screen can attach each message to its control.</summary>
    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : CatalogWriteResult
    {
        public Invalid(string field, string message)
            : this(new List<ValidationError> { new(field, message) }.AsReadOnly())
        {
        }
    }

    /// <summary>No such row, or it is soft-deleted.</summary>
    public sealed record NotFound(string Message) : CatalogWriteResult;

    /// <summary>
    /// The request was well formed and still cannot be carried out: a stale row version, a reorder of a
    /// list that has changed, a category something still points at.
    /// </summary>
    /// <param name="Code">
    /// A machine-readable reason next to the sentence, so the screen branches on the reason rather than
    /// parsing English — the convention <c>AdminMediaEndpoints</c> established.
    /// </param>
    /// <param name="Details">Extra facts for the problem body, e.g. the counts behind a refused removal.</param>
    public sealed record Conflict(
        string Code,
        string Message,
        IReadOnlyDictionary<string, object?>? Details = null) : CatalogWriteResult;
}

/// <summary>The <see cref="CatalogWriteResult.Conflict.Code"/> values these use cases produce.</summary>
public static class CatalogConflictCodes
{
    /// <summary>The caller's row version is not the row's current one; reload and re-apply (doc 08 §1.5).</summary>
    public const string StaleRowVersion = "stale_row_version";

    /// <summary>
    /// The list sent to a reorder is not the list that is there now — a sibling was added, removed or
    /// moved away while the screen was open.
    /// </summary>
    public const string StaleOrder = "stale_order";

    /// <summary>Products or child categories still point at the category (doc 08 §3.2).</summary>
    public const string CategoryInUse = "category_in_use";

    /// <summary>A unique index refused the write after the handler's own check passed — the race.</summary>
    public const string DuplicateKey = "duplicate_key";
}
