namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// The arithmetic of <c>catalog.Categories.Path</c> (doc 08 §3.2): a materialised id path such as
/// <c>/12/34/</c>, always with a leading and a trailing slash, so <c>LIKE parentPath + '%'</c> selects
/// exactly a subtree and nothing else.
/// <para>
/// It lives in the domain, as pure string arithmetic, because the move use case (doc 08 §10.1
/// "<c>Categories.Path/Depth</c> … move use case, one transaction") has to rewrite the path of a whole
/// subtree, and getting that wrong is not a visible bug: the rows look fine and the storefront's
/// subtree predicate silently returns the wrong products. Pure functions make the cases that matter —
/// a sibling whose id is a prefix of another's, a move under a descendant — testable without a
/// database.
/// </para>
/// </summary>
public static class CategoryPath
{
    /// <summary>The path of a category given its parent's path (null or empty for a top-level one).</summary>
    public static string Build(string? parentPath, int categoryId) =>
        string.IsNullOrEmpty(parentPath) ? $"/{categoryId}/" : $"{parentPath}{categoryId}/";

    /// <summary>
    /// How deep a path is: 1 for <c>/12/</c>, 2 for <c>/12/34/</c>. This is the value
    /// <c>catalog.Categories.Depth</c> holds, and <c>CK_Categories_Depth</c> bounds at
    /// <see cref="Category.MaxDepth"/>.
    /// </summary>
    public static byte DepthOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var segments = 0;
        foreach (var character in path)
        {
            if (character == '/')
            {
                segments++;
            }
        }

        // "/12/34/" has three slashes and depth 2; an empty or malformed path has depth 0.
        return (byte)Math.Max(0, segments - 1);
    }

    /// <summary>
    /// The category ids a path names, root first: <c>/12/34/</c> gives 12 then 34.
    /// <para>
    /// Added for ADM-10 (W1-03): doc 08 §3.2 puts "category path names" into
    /// <c>ProductSearchIndex.SearchText</c>, so a product filed under Midi is found by a shopper who
    /// searches for "dresses". The names need the ancestors, and a materialised path already holds exactly
    /// which rows those are — so the whole set can be read in one query instead of walked one parent at a
    /// time.
    /// </para>
    /// <para>
    /// A segment that is not a number is skipped rather than thrown over: a path is written by this file's
    /// own arithmetic, so a malformed one is a corrupted row, and the right answer to a corrupted row in a
    /// search-text builder is a slightly poorer search text rather than a 500 on a save.
    /// </para>
    /// </summary>
    public static IReadOnlyList<int> IdsIn(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return [];
        }

        var ids = new List<int>(4);

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(segment, System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="ancestorPath"/> or sits underneath it.
    /// <para>
    /// The trailing slash is what makes this safe: without it <c>/1/</c> would "contain" <c>/12/</c>,
    /// and a move would be refused — or allowed — for the wrong category.
    /// </para>
    /// </summary>
    public static bool IsWithin(string path, string ancestorPath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(ancestorPath);

        return path.StartsWith(ancestorPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rewrites one path of a moved subtree: the part of <paramref name="path"/> that used to read
    /// <paramref name="oldSubtreePath"/> now reads <paramref name="newSubtreePath"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// When <paramref name="path"/> is not inside <paramref name="oldSubtreePath"/>. A caller that has
    /// collected the subtree with <see cref="IsWithin"/> cannot hit this; a caller that has muddled two
    /// subtrees would otherwise write a path that is silently wrong.
    /// </exception>
    public static string Rebase(string path, string oldSubtreePath, string newSubtreePath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(oldSubtreePath);
        ArgumentNullException.ThrowIfNull(newSubtreePath);

        if (!IsWithin(path, oldSubtreePath))
        {
            throw new ArgumentException(
                $"'{path}' is not inside '{oldSubtreePath}', so it is not part of the subtree being moved.",
                nameof(path));
        }

        return string.Concat(newSubtreePath, path.AsSpan(oldSubtreePath.Length));
    }
}
