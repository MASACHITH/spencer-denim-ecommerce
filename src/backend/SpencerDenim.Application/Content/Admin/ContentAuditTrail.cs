using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Application.Content.Admin;

/// <summary>
/// Builds the <c>ops.AuditLogs</c> rows for an ADM-17 content write (doc 08 §3.8).
/// <para>
/// The same two shapes <c>CatalogAuditTrail</c> has — a field diff and an explicit event — for the same
/// reason issue #59 gives: a client asks "who did this?" about a published policy page as readily as
/// about a category, and a trail of whole entities is a trail nobody reads.
/// </para>
/// <para>
/// <b>One rule here that the catalogue's equivalent does not need, and it is the important part of this
/// file.</b> A content page's <i>body</i> and a contact enquiry's <i>message</i> never appear in an audit
/// row. For the enquiry that is doc 08 §3.7 in as many words — a deletion is audited "with the reason, and
/// the audit row holds no copy of the message body", because the one case the deletion exists for is a
/// customer having pasted bank account details into it, and copying them into a table kept for three
/// years (§8.4) would defeat the deletion entirely. For a page body it is a judgement of the same kind:
/// <c>AuditLogs.BeforeJson</c> is <c>nvarchar(max)</c>, a terms-and-conditions page is tens of kilobytes,
/// and two copies of it per save for three years is a trail that is almost all page copy. Both are
/// recorded as "changed: body", with the length, and nothing else.
/// </para>
/// </summary>
public static class ContentAuditTrail
{
    /// <summary><c>EntityType</c> for a content page row.</summary>
    public const string ContentPageEntityType = "ContentPage";

    /// <summary>
    /// <c>EntityType</c> for a contact enquiry. Doc 08 §3.7 fixes this string:
    /// "The delete is audited (<c>EntityType = 'ContactSubmission'</c>)".
    /// </summary>
    public const string ContactSubmissionEntityType = "ContactSubmission";

    /// <summary>
    /// The field name a body change is recorded under. Never the body itself — see the type remarks.
    /// </summary>
    public const string BodyField = "bodyHtml";

    /// <summary><c>ops.AuditLogs.EntityId</c> is <c>varchar(100)</c>.</summary>
    private const int EntityIdMaxLength = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A row describing the fields that differ, or <see langword="null"/> when none do — so a save of a
    /// form nobody edited is not an event.
    /// <para>
    /// <b>The comparison is on the real values and the redaction happens afterwards, and that ordering is
    /// the whole of a defect this caught.</b> The first version put
    /// <see cref="DescribeBody"/>'s output — a character count — into the snapshots the diff reads. Two
    /// different bodies of the same length then produced <i>no</i> diff, so the caller concluded nothing
    /// had changed and never issued a statement: the editor's new copy was assigned to the entity and
    /// thrown away, and the screen said "Saved". Found by
    /// <c>A_save_that_does_not_mention_the_seo_fields_leaves_them_alone</c>, whose fixture happened to
    /// change <c>&lt;p&gt;Old copy.&lt;/p&gt;</c> for <c>&lt;p&gt;New copy.&lt;/p&gt;</c> — sixteen
    /// characters either way.
    /// </para>
    /// <para>
    /// So callers pass the body itself, this compares it, and the row that goes to
    /// <c>ops.AuditLogs</c> carries the description instead. One comparison, one redaction, and neither
    /// can be got right while the other is wrong.
    /// </para>
    /// </summary>
    /// <param name="redactedFields">
    /// Fields whose value must not reach the audit row — the content page's body, and anything else
    /// later. They are compared normally and then replaced with <see cref="DescribeBody"/>'s summary.
    /// </param>
    /// <param name="describeRedacted">
    /// How a redacted field is described instead, when a length of HTML is the wrong summary — a size
    /// guide's table reads better as "6 columns × 8 rows". Called with the field name and the real value.
    /// </param>
    public static AuditLog? ForChange(
        string action,
        string entityType,
        string entityId,
        IReadOnlyDictionary<string, object?> before,
        IReadOnlyDictionary<string, object?> after,
        AdminActor actor,
        DateTime occurredAtUtc,
        IReadOnlySet<string>? redactedFields = null,
        Func<string, object?, object?>? describeRedacted = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changedBefore = new Dictionary<string, object?>(StringComparer.Ordinal);
        var changedAfter = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (field, afterValue) in after)
        {
            var beforeValue = before.TryGetValue(field, out var existing) ? existing : null;
            if (SameValue(beforeValue, afterValue))
            {
                continue;
            }

            var redact = redactedFields?.Contains(field) == true;

            changedBefore[field] = redact ? Redact(field, beforeValue, describeRedacted) : beforeValue;
            changedAfter[field] = redact ? Redact(field, afterValue, describeRedacted) : afterValue;
        }

        return changedAfter.Count == 0
            ? null
            : Row(action, entityType, entityId, changedBefore, changedAfter, actor, occurredAtUtc);
    }

    private static object? Redact(string field, object? value, Func<string, object?, object?>? describe) =>
        describe is null ? DescribeBody(value as string) : describe(field, value);

    /// <summary>The fields a content-page row redacts. One set, so the two callers cannot disagree.</summary>
    public static IReadOnlySet<string> RedactedContentPageFields { get; } =
        new HashSet<string>(StringComparer.Ordinal) { BodyField };

    /// <summary>A row for an event that is not a field diff — a creation, a removal, a status change.</summary>
    public static AuditLog Row(
        string action,
        string entityType,
        string entityId,
        IReadOnlyDictionary<string, object?>? before,
        IReadOnlyDictionary<string, object?>? after,
        AdminActor actor,
        DateTime occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrEmpty(entityId);

        return new AuditLog
        {
            OccurredAtUtc = occurredAtUtc,
            UserId = actor.UserId,
            UserEmail = actor.Email,
            Action = action,
            EntityType = entityType,
            EntityId = entityId.Length > EntityIdMaxLength ? entityId[..EntityIdMaxLength] : entityId,
            BeforeJson = ToJson(before),
            AfterJson = ToJson(after),
            IpAddress = actor.IpAddress,
            UserAgent = actor.UserAgent,
            CorrelationId = actor.CorrelationId,
        };
    }

    /// <summary>
    /// How a body is described in the trail: its length, never its content.
    /// <para>
    /// A length rather than a hash, because a length is the thing a person reading the trail can use — "the
    /// returns page went from 0 to 3,180 characters" answers the question an audit log is opened to answer,
    /// and a hash answers none of them while still being a value derived from the text.
    /// </para>
    /// </summary>
    public static string DescribeBody(string? bodyHtml) => string.Create(
        CultureInfo.InvariantCulture,
        $"{(bodyHtml ?? string.Empty).Length} characters of sanitised HTML");

    private static string? ToJson(IReadOnlyDictionary<string, object?>? values) =>
        values is null || values.Count == 0 ? null : JsonSerializer.Serialize(values, JsonOptions);

    /// <summary>
    /// Compares two field values without caring whether they arrived boxed as <c>int</c> or <c>short</c>.
    /// Strings compare <see cref="StringComparison.Ordinal"/>, not the database's case-insensitive
    /// collation: an admin correcting "Returns" to "returns" has changed something and the trail must say
    /// so.
    /// </summary>
    private static bool SameValue(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
        _ => Equals(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture)),
    };
}
