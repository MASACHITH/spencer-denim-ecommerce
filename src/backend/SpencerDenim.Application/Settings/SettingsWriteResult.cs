using SpencerDenim.Application.Abstractions.Results;

namespace SpencerDenim.Application.Settings;

/// <summary>
/// What an ADM-22 write use case decided. One type for both of them, so the endpoints share one mapping
/// to HTTP and a third command cannot invent a new way to say "refused".
/// <para>
/// Its own type rather than <c>CatalogWriteResult</c>, which is the per-feature shape this codebase
/// already uses twice (<c>CatalogWriteResult</c> for ADM-11, <c>CartWriteResult</c> for CART-01/02). The
/// cases differ: there is no create and no delete here, so there is no <c>NotFound</c> for a row a client
/// named — a key either exists in <see cref="SpencerDenim.Domain.Settings.SettingCatalog"/>, in which case
/// the seeder made the row, or it is not reachable at all.
/// </para>
/// </summary>
public abstract record SettingsWriteResult
{
    private SettingsWriteResult()
    {
    }

    /// <summary>The write happened and <paramref name="ChangedKeys"/> moved.</summary>
    /// <param name="ChangedKeys">
    /// Which keys actually changed, in catalogue order. The endpoint does not use it; the tests and the
    /// audit assertions do, and it is the thing a future "what did I just change" confirmation needs.
    /// </param>
    public sealed record Succeeded(IReadOnlyList<string> ChangedKeys) : SettingsWriteResult;

    /// <summary>
    /// Every value was already what the request asked for, so nothing was written: no statement issued,
    /// no row version moved, no audit row. Still a success — the request's intent holds.
    /// </summary>
    public sealed record Unchanged : SettingsWriteResult;

    /// <summary>Rejected input, one entry per field, so the screen can attach each message to its control.</summary>
    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : SettingsWriteResult
    {
        public Invalid(string field, string message)
            : this(new List<ValidationError> { new(field, message) }.AsReadOnly())
        {
        }
    }

    /// <summary>
    /// The seeded row is missing, which is a server-side fault rather than a client mistake: it means the
    /// settings seeder has not run against this database. Separated from <see cref="Invalid"/> so the
    /// endpoint can answer 500 rather than tell a staff user to fix a field they cannot see.
    /// </summary>
    public sealed record NotSeeded(string Message) : SettingsWriteResult;

    /// <summary>
    /// Well formed and still not applicable: somebody else saved the same group first.
    /// </summary>
    /// <param name="Code">
    /// Machine-readable, next to the sentence, so the screen branches on the reason rather than parsing
    /// English — the convention <c>AdminMediaEndpoints</c> established.
    /// </param>
    public sealed record Conflict(string Code, string Message) : SettingsWriteResult;
}

/// <summary>The <see cref="SettingsWriteResult.Conflict.Code"/> values these use cases produce.</summary>
public static class SettingsConflictCodes
{
    /// <summary>The caller's row version is not the row's current one; reload and re-apply (doc 08 §1.5).</summary>
    public const string StaleRowVersion = "stale_row_version";
}
