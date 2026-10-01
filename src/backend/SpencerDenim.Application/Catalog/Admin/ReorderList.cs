using System.Globalization;
using SpencerDenim.Application.Abstractions.Results;

namespace SpencerDenim.Application.Catalog.Admin;

/// <summary>
/// The guard that makes a whole-list reorder safe to send from a page somebody has had open for a while.
/// <para>
/// <b>Why a reorder is a list and not a position.</b> "Move Jeans up one" is an increment, and two admins
/// each incrementing a different row from the same starting list produce an order neither of them asked
/// for — both writes succeed, both rows move, and the result is arbitrary. Sending the whole intended
/// order makes the request state what the admin believes the list should be, which is a claim the server
/// can check.
/// </para>
/// <para>
/// <b>And checking it has to mean "exactly this set".</b> A list that is merely a subset would reorder what
/// it names and silently leave out a sibling created a minute ago by someone else — the new category would
/// keep a sort order from the old numbering and land somewhere nobody chose. A list with an extra id would
/// be reordering around a category that has since been removed or moved away. Both are the same mistake as
/// the increment, one step later, so both are refused with
/// <see cref="CatalogConflictCodes.StaleOrder"/> and the client reloads.
/// </para>
/// </summary>
public static class ReorderList
{
    /// <summary>
    /// <c>null</c> when <paramref name="requested"/> is exactly <paramref name="actual"/> as a set, with no
    /// repeats; otherwise the answer to give the caller.
    /// </summary>
    /// <param name="field">The request field to name in a validation error.</param>
    /// <param name="noun">Singular noun for the message, e.g. "category".</param>
    public static CatalogWriteResult? Check(
        IReadOnlyList<int>? requested,
        IReadOnlyList<int> actual,
        string field,
        string noun)
    {
        ArgumentNullException.ThrowIfNull(actual);

        if (requested is null || requested.Count == 0)
        {
            return new CatalogWriteResult.Invalid(
                field,
                string.Create(CultureInfo.InvariantCulture, $"Send every {noun} id, in the order they should appear."));
        }

        var seen = new HashSet<int>();
        var repeats = requested.Where(id => !seen.Add(id)).Distinct().ToList();
        if (repeats.Count > 0)
        {
            return new CatalogWriteResult.Invalid(
                field,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The same {noun} is listed more than once ({string.Join(", ", repeats)})."));
        }

        var existing = actual.ToHashSet();
        var unknown = requested.Where(id => !existing.Contains(id)).ToList();
        var missing = actual.Where(id => !seen.Contains(id)).ToList();

        if (unknown.Count == 0 && missing.Count == 0)
        {
            return null;
        }

        var parts = new List<string>(2);
        if (missing.Count > 0)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"it leaves out {noun} {string.Join(", ", missing)}"));
        }

        if (unknown.Count > 0)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"it lists {noun} {string.Join(", ", unknown)}, which is not in this list any more"));
        }

        return new CatalogWriteResult.Conflict(
            CatalogConflictCodes.StaleOrder,
            $"This order was made from an older version of the list: {string.Join(" and ", parts)}. " +
            "Reload the page and put them in order again.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["missingIds"] = missing,
                ["unknownIds"] = unknown,
            });
    }
}
