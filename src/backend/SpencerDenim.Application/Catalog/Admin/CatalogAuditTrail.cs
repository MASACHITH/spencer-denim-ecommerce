using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Application.Catalog.Admin;

/// <summary>
/// Builds the <c>ops.AuditLogs</c> rows for an ADM-11 write (doc 08 §3.8).
/// <para>
/// <b>Why W1-12 writes audit rows at all.</b> Issue <b>#59</b> measured the whole table after a session of
/// uploads, rejections and deletes and found <b>one</b> row, a sign-in: <c>AuditActions.Create</c> and
/// <c>SoftDelete</c> existed and nothing used them, so the viewer W1-09 had just shipped showed a client
/// nothing but sign-ins. It names catalogue edits as one of "the things a shop owner would actually ask
/// 'who did this?' about", and a category going off the storefront is precisely that: the products
/// vanish, and without a row nobody can say who did it or when.
/// </para>
/// <para>
/// <b>Changed properties only</b> (doc 08 §3.8), which is why this takes two snapshots and diffs them
/// rather than serialising whole entities. A trail where every row carries every column is a trail nobody
/// reads: the one field that moved is the whole content of the event.
/// </para>
/// </summary>
public static class CatalogAuditTrail
{
    /// <summary><c>EntityType</c> for a department row.</summary>
    public const string DepartmentEntityType = "Department";

    /// <summary><c>EntityType</c> for a category row.</summary>
    public const string CategoryEntityType = "Category";

    /// <summary>
    /// <c>EntityType</c> for a change of sibling order.
    /// <para>
    /// A reorder is one decision about a list, not <i>n</i> edits to <i>n</i> rows, so it is one audit row
    /// naming the list — <c>EntityId</c> is the department and parent whose children moved. Writing a row
    /// per category would bury every other catalogue event under a reorder of twelve siblings, and none of
    /// the twelve rows would say what the new order was.
    /// </para>
    /// </summary>
    public const string CategoryOrderEntityType = "CategoryOrder";

    /// <inheritdoc cref="CategoryOrderEntityType"/>
    public const string DepartmentOrderEntityType = "DepartmentOrder";

    /// <summary><c>ops.AuditLogs.EntityId</c> is <c>varchar(100)</c>.</summary>
    private const int EntityIdMaxLength = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A row describing the fields that differ between <paramref name="before"/> and
    /// <paramref name="after"/>, or <c>null</c> when nothing does.
    /// <para>
    /// <c>null</c> rather than an empty row on purpose: a save of a form nobody edited is not an event, and
    /// a trail full of "changed nothing" rows is how an audit log stops being read.
    /// </para>
    /// </summary>
    public static AuditLog? ForChange(
        string action,
        string entityType,
        string entityId,
        IReadOnlyDictionary<string, object?> before,
        IReadOnlyDictionary<string, object?> after,
        AdminActor actor,
        DateTime occurredAtUtc)
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

            changedBefore[field] = beforeValue;
            changedAfter[field] = afterValue;
        }

        return changedAfter.Count == 0
            ? null
            : Row(action, entityType, entityId, changedBefore, changedAfter, actor, occurredAtUtc);
    }

    /// <summary>
    /// A row for an event that is not a field diff — a creation, a removal, a reorder — where the caller
    /// has already decided what the before and after states are.
    /// </summary>
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
    /// The <c>EntityId</c> of a sibling list: the department, then the parent category or <c>root</c>.
    /// <para>
    /// A composite id rather than the parent's alone, because the root of a department has no parent id —
    /// three departments would otherwise share one <c>EntityId</c> and the entity filter in the viewer
    /// would return all three.
    /// </para>
    /// </summary>
    public static string OrderScopeId(int departmentId, int? parentCategoryId) => string.Create(
        CultureInfo.InvariantCulture,
        $"{departmentId}/{(parentCategoryId is { } parent ? parent.ToString(CultureInfo.InvariantCulture) : "root")}");

    private static string? ToJson(IReadOnlyDictionary<string, object?>? values) =>
        values is null || values.Count == 0 ? null : JsonSerializer.Serialize(values, JsonOptions);

    /// <summary>
    /// Compares two field values without caring whether they arrived boxed as <c>int</c> or <c>short</c>.
    /// <para>
    /// Strings are compared with <see cref="StringComparison.Ordinal"/>, not the database's
    /// case-insensitive collation: an admin correcting "Jeans" to "jeans" has changed something and the
    /// trail must say so.
    /// </para>
    /// </summary>
    private static bool SameValue(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
        _ => Equals(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture)),
    };
}
