using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog.Products;

namespace SpencerDenim.Application.Catalog.Search;

/// <summary>
/// A validated search request (SRCH-02): the tokens to match, plus the same filters and sorts a
/// listing accepts, because the search results page carries them (doc 08 §7.6).
/// </summary>
public sealed record ProductSearchQuery
{
    private ProductSearchQuery(string term, IReadOnlyList<string> tokens, ProductListQuery listQuery)
    {
        Term = term;
        Tokens = tokens;
        ListQuery = listQuery;
    }

    /// <summary>The trimmed term, echoed back in the response and logged for SRCH-01 popular searches.</summary>
    public string Term { get; }

    /// <summary>Up to five tokens; every one must match (doc 08 §7.6).</summary>
    public IReadOnlyList<string> Tokens { get; }

    /// <summary>The filters, sort and paging that apply on top of the term.</summary>
    public ProductListQuery ListQuery { get; }

    /// <summary>
    /// True when <see cref="ProductSortOption.Recommended"/> was asked for, which for a search means
    /// "by relevance" rather than by merchandising rank.
    /// </summary>
    public bool SortByRelevance => ListQuery.Sort == ProductSortOption.Recommended;

    public static Result<ProductSearchQuery> Create(
        string? term,
        ProductListCriteria criteria,
        CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<ValidationError>();

        var trimmed = term?.Trim() ?? string.Empty;
        if (trimmed.Length < SearchTerms.MinTermLength)
        {
            errors.Add(new ValidationError("q", $"q must be at least {SearchTerms.MinTermLength} characters."));
        }
        else if (trimmed.Length > SearchTerms.MaxTermLength)
        {
            errors.Add(new ValidationError("q", $"q must be {SearchTerms.MaxTermLength} characters or fewer."));
        }

        var tokens = SearchTerms.Tokenise(trimmed);
        if (errors.Count == 0 && tokens.Count == 0)
        {
            errors.Add(new ValidationError("q", $"q must contain a word of at least {SearchTerms.MinTermLength} characters."));
        }

        var listQuery = ProductListQuery.Create(criteria, options);
        if (!listQuery.IsSuccess)
        {
            errors.AddRange(listQuery.Errors);
        }

        if (errors.Count > 0)
        {
            return Result.Failure<ProductSearchQuery>(errors.AsReadOnly());
        }

        return Result.Success(new ProductSearchQuery(trimmed, tokens, listQuery.Value));
    }
}
