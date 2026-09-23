namespace SpencerDenim.Application.Catalog.Contracts;

/// <summary>
/// One page of a listing, with the live total PLP-06 shows and the flags PLP-07 "Load More" needs.
/// </summary>
/// <typeparam name="TItem">The item type.</typeparam>
/// <param name="Items">The rows on this page.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows requested per page.</param>
/// <param name="TotalCount">Rows the filter matches in total.</param>
public sealed record PagedResult<TItem>(
    IReadOnlyList<TItem> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPreviousPage => Page > 1 && TotalCount > 0;

    public bool HasNextPage => Page < TotalPages;
}

/// <summary>
/// Factory helpers for <see cref="PagedResult{TItem}"/>, on a non-generic type so the type argument
/// is inferred at the call site.
/// </summary>
public static class PagedResult
{
    /// <summary>The "No products found" state PLP-06 renders.</summary>
    public static PagedResult<TItem> Empty<TItem>(int page, int pageSize) => new([], page, pageSize, 0);
}
