using SpencerDenim.Application.Abstractions.Results;

namespace SpencerDenim.Application.Content.Admin;

/// <summary>
/// What an ADM-17 content write decided. One type for all of them, so the endpoints share one mapping to
/// HTTP and a new command cannot invent a new way to say "refused" — the arrangement
/// <c>CatalogWriteResult</c> established and the reason it gives: these use cases have answers a client
/// must tell apart that a validation-only result type cannot express.
/// </summary>
public abstract record ContentWriteResult
{
    private ContentWriteResult()
    {
    }

    /// <summary>
    /// The write happened.
    /// </summary>
    /// <param name="EntityId">The row's id, new or existing.</param>
    /// <param name="RemovedFromBody">
    /// What the sanitiser took out of the body, if anything (see <c>SanitisedHtml.Removed</c>). Carried all
    /// the way to the screen on a <b>successful</b> save, deliberately: the save worked, and the editor
    /// still has to be told that their pasted <c>&lt;iframe&gt;</c> is not on the page. A silent save that
    /// dropped part of a paste is the defect shape this project has shipped three times.
    /// </param>
    public sealed record Succeeded(int EntityId, IReadOnlyList<string> RemovedFromBody) : ContentWriteResult
    {
        public Succeeded(int entityId)
            : this(entityId, [])
        {
        }
    }

    /// <summary>
    /// Every value was already what the request asked for, so no statement was issued: the row version
    /// does not move and no audit row is written.
    /// </summary>
    public sealed record Unchanged(int EntityId) : ContentWriteResult;

    /// <summary>Rejected input, one entry per field, so the screen can attach each message to its control.</summary>
    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : ContentWriteResult
    {
        public Invalid(string field, string message)
            : this(new List<ValidationError> { new(field, message) }.AsReadOnly())
        {
        }
    }

    /// <summary>No such row, or it is soft-deleted.</summary>
    public sealed record NotFound(string Message) : ContentWriteResult;

    /// <summary>
    /// Well formed and still impossible: a stale row version, a system page somebody tried to delete, a
    /// publish the alt-text checklist refuses.
    /// </summary>
    /// <param name="Code">A machine-readable reason beside the sentence, so a screen branches on the
    /// reason rather than parsing English.</param>
    /// <param name="Message">What to tell the staff member.</param>
    /// <param name="Details">Extra facts for the problem body, e.g. how many images have no alt text.</param>
    public sealed record Conflict(
        string Code,
        string Message,
        IReadOnlyDictionary<string, object?>? Details = null) : ContentWriteResult;
}

/// <summary>The <see cref="ContentWriteResult.Conflict.Code"/> values these use cases produce.</summary>
public static class ContentConflictCodes
{
    /// <summary>The caller's row version is not the row's current one; reload and re-apply (doc 08 §1.5).</summary>
    public const string StaleRowVersion = "stale_row_version";

    /// <summary>A unique index refused the write after the handler's own check passed — the race.</summary>
    public const string DuplicateKey = "duplicate_key";

    /// <summary>
    /// Doc 11 P13: "System pages cannot be deleted." A 409 and not a 403: the caller may well hold
    /// <c>Content.Delete</c>, and nothing about their permissions would change the answer.
    /// </summary>
    public const string SystemPageNotDeletable = "system_page_not_deletable";

    /// <summary>
    /// Publishing was refused because the body holds an image with no alt text (doc 08 §3.3, GLB-12:
    /// "public images require alt text before the content that uses them is published").
    /// <para>
    /// A 409 rather than a 400, because nothing a staff member typed into <i>this</i> request is wrong:
    /// the publish instruction is perfectly well formed and the page is not ready. The <c>Details</c>
    /// carry the count, so the screen can say how many to fix.
    /// </para>
    /// </summary>
    public const string AltTextRequired = "alt_text_required";

    /// <summary>
    /// An edit would have left a page that is <b>on the shop</b> in a state its own publish rule forbids
    /// — a body whose images lost their alt text. Refused rather than applied, and rather than silently
    /// unpublishing: taking a page off the shop is its own deliberate act with its own
    /// <c>StatusChange</c> audit row, and it must not become a side effect of saving a form. This is
    /// W1-03's <c>published_product_would_break</c>, one feature over.
    /// </summary>
    public const string PublishedPageWouldBreak = "published_page_would_break";

    /// <summary>
    /// The page's <b>stored</b> body is markup the sanitiser will not parse — longer than
    /// <c>HtmlSanitiser.MaxInputLength</c> or nested past <c>HtmlSanitiser.MaxNestingDepth</c> — so the
    /// publish gate cannot read it to check its images, and a gate that cannot check refuses.
    /// <para>
    /// Only reachable for a row that did not come through this API, because the same markup is refused on
    /// the way in. That is the case worth a code of its own: a support script, data fix or restore is
    /// exactly how such a row appears, and the staff member meeting this message needs to be told the
    /// stored copy is the problem rather than the one they are sending.
    /// </para>
    /// </summary>
    public const string BodyNotReadable = "body_not_readable";
}
