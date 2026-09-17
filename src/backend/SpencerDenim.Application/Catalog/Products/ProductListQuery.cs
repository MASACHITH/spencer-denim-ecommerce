using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using SpencerDenim.Application.Abstractions.Results;

namespace SpencerDenim.Application.Catalog.Products;

/// <summary>
/// A validated, normalised product listing query (PLP-01..07). Everything in it is already trimmed,
/// lower-cased, de-duplicated and inside its bounds, so the read store composes predicates without
/// re-checking anything (doc 08 §7.1: predicates are only added for filters that are present).
/// </summary>
public sealed partial record ProductListQuery
{
    /// <summary>Longest slug the schema stores (<c>catalog.Categories.Slug varchar(100)</c>).</summary>
    public const int MaxSlugLength = 100;

    /// <summary>Longest size label the schema stores (<c>catalog.Sizes.Label nvarchar(20)</c>).</summary>
    public const int MaxSizeLabelLength = 20;

    /// <summary>Most values one repeatable filter may carry, so a crafted URL cannot build a huge IN list.</summary>
    public const int MaxFilterValues = 25;

    /// <summary>Highest price a filter may name, in LKR. Above it the value is a typo, not a filter.</summary>
    public const decimal MaxPriceValue = 10_000_000m;

    /// <summary>Highest page number, so <c>OFFSET</c> stays sane on a catalogue of ≤ 5,000 products.</summary>
    public const int MaxPage = 1_000;

    private ProductListQuery()
    {
    }

    public string? DepartmentSlug { get; private init; }

    public string? CategorySlug { get; private init; }

    public IReadOnlyList<string> AgeGroupSlugs { get; private init; } = [];

    public decimal? MinPrice { get; private init; }

    public decimal? MaxPrice { get; private init; }

    public IReadOnlyList<string> SizeLabels { get; private init; } = [];

    public IReadOnlyList<string> ColourFamilySlugs { get; private init; } = [];

    public IReadOnlyList<string> MaterialSlugs { get; private init; } = [];

    public IReadOnlyList<string> FitSlugs { get; private init; } = [];

    public IReadOnlyList<string> OccasionSlugs { get; private init; } = [];

    public decimal? MinRating { get; private init; }

    public bool InStockOnly { get; private init; }

    public bool OnSaleOnly { get; private init; }

    public decimal? MinDiscountPercent { get; private init; }

    public bool NewArrivalsOnly { get; private init; }

    public ProductSortOption Sort { get; private init; }

    public int Page { get; private init; } = 1;

    public int PageSize { get; private init; }

    /// <summary>Rows to skip. Offset paging: doc 08 §7.1 specifies <c>OFFSET … FETCH NEXT</c>.</summary>
    public int Skip => (Page - 1) * PageSize;

    /// <summary>
    /// True when exactly one category is selected, which is the case doc 08 §7.3 gives its own
    /// Recommended ordering (the category page's manual merchandising order).
    /// </summary>
    public bool UsesCategoryMerchandisingOrder =>
        Sort == ProductSortOption.Recommended && CategorySlug is not null;

    /// <summary>
    /// Validates and normalises a raw query string. Every problem is collected, so one 400 answers the
    /// whole request instead of the first mistake.
    /// </summary>
    public static Result<ProductListQuery> Create(ProductListCriteria criteria, CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<ValidationError>();

        var department = NormaliseSlug(criteria.Department, "department", errors);
        var category = NormaliseSlug(criteria.Category, "category", errors);
        var ageGroups = NormaliseSlugList(criteria.AgeGroups, "ageGroup", errors);
        var colours = NormaliseSlugList(criteria.Colours, "colour", errors);
        var materials = NormaliseSlugList(criteria.Materials, "material", errors);
        var fits = NormaliseSlugList(criteria.Fits, "fit", errors);
        var occasions = NormaliseSlugList(criteria.Occasions, "occasion", errors);
        var sizes = NormaliseLabelList(criteria.Sizes, "size", errors);

        var minPrice = NormaliseMoney(criteria.MinPrice, "minPrice", errors);
        var maxPrice = NormaliseMoney(criteria.MaxPrice, "maxPrice", errors);
        if (minPrice is not null && maxPrice is not null && minPrice > maxPrice)
        {
            errors.Add(new ValidationError("minPrice", "minPrice must not be greater than maxPrice."));
        }

        var minRating = NormaliseRange(criteria.MinRating, "minRating", 0m, 5m, errors);
        var minDiscount = NormaliseRange(criteria.MinDiscountPercent, "minDiscountPercent", 0m, 100m, errors);

        if (!ProductSortOptions.TryParse(criteria.Sort, out var sort))
        {
            errors.Add(new ValidationError(
                "sort",
                $"sort must be one of: {string.Join(", ", ProductSortOptions.All)}."));
        }

        var page = criteria.Page ?? 1;
        if (page is < 1 or > MaxPage)
        {
            errors.Add(new ValidationError("page", $"page must be between 1 and {MaxPage}."));
        }

        var pageSize = criteria.PageSize ?? options.DefaultPageSize;
        if (pageSize < 1 || pageSize > options.MaxPageSize)
        {
            errors.Add(new ValidationError("pageSize", $"pageSize must be between 1 and {options.MaxPageSize}."));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<ProductListQuery>(errors.AsReadOnly());
        }

        return Result.Success(new ProductListQuery
        {
            DepartmentSlug = department,
            CategorySlug = category,
            AgeGroupSlugs = ageGroups,
            MinPrice = minPrice,
            MaxPrice = maxPrice,
            SizeLabels = sizes,
            ColourFamilySlugs = colours,
            MaterialSlugs = materials,
            FitSlugs = fits,
            OccasionSlugs = occasions,
            MinRating = minRating,
            // "on sale" and a minimum discount are the same predicate; asking for a discount implies the flag.
            InStockOnly = criteria.InStock ?? false,
            OnSaleOnly = (criteria.OnSale ?? false) || minDiscount > 0m,
            MinDiscountPercent = minDiscount,
            NewArrivalsOnly = criteria.NewArrivals ?? false,
            Sort = sort,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>A stable key for the 60 s response cache in doc 08 §7.5. Same filters → same key.</summary>
    public string ToCacheKey()
    {
        var parts = new List<string>(16)
        {
            $"d={DepartmentSlug}",
            $"c={CategorySlug}",
            $"ag={string.Join('|', AgeGroupSlugs)}",
            $"p={Format(MinPrice)}-{Format(MaxPrice)}",
            $"sz={string.Join('|', SizeLabels)}",
            $"cl={string.Join('|', ColourFamilySlugs)}",
            $"m={string.Join('|', MaterialSlugs)}",
            $"f={string.Join('|', FitSlugs)}",
            $"o={string.Join('|', OccasionSlugs)}",
            $"r={Format(MinRating)}",
            $"in={InStockOnly}",
            $"sale={OnSaleOnly}",
            $"disc={Format(MinDiscountPercent)}",
            $"new={NewArrivalsOnly}",
            $"s={ProductSortOptions.ToQueryValue(Sort)}",
            $"pg={Page.ToString(CultureInfo.InvariantCulture)}x{PageSize.ToString(CultureInfo.InvariantCulture)}",
        };

        return string.Join(';', parts);
    }

    private static string Format(decimal? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string? NormaliseSlug(string? raw, string field, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var slug = raw.Trim().ToLowerInvariant();

        if (slug.Length > MaxSlugLength)
        {
            errors.Add(new ValidationError(field, $"{field} must be {MaxSlugLength} characters or fewer."));
            return null;
        }

        if (!SlugPattern().IsMatch(slug))
        {
            errors.Add(new ValidationError(
                field,
                $"{field} must be a slug: lower-case letters, digits and single hyphens."));
            return null;
        }

        return slug;
    }

    private static ReadOnlyCollection<string> NormaliseSlugList(
        IReadOnlyList<string>? raw,
        string field,
        List<ValidationError> errors)
    {
        if (raw is null || raw.Count == 0)
        {
            return [];
        }

        var values = new List<string>(raw.Count);
        foreach (var candidate in raw)
        {
            var slug = NormaliseSlug(candidate, field, errors);
            if (slug is not null && !values.Contains(slug, StringComparer.Ordinal))
            {
                values.Add(slug);
            }
        }

        if (values.Count > MaxFilterValues)
        {
            errors.Add(new ValidationError(field, $"{field} accepts at most {MaxFilterValues} values."));
            return [];
        }

        return values.AsReadOnly();
    }

    private static ReadOnlyCollection<string> NormaliseLabelList(
        IReadOnlyList<string>? raw,
        string field,
        List<ValidationError> errors)
    {
        if (raw is null || raw.Count == 0)
        {
            return [];
        }

        var values = new List<string>(raw.Count);
        foreach (var candidate in raw)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var label = candidate.Trim();
            if (label.Length > MaxSizeLabelLength)
            {
                errors.Add(new ValidationError(field, $"{field} values must be {MaxSizeLabelLength} characters or fewer."));
                continue;
            }

            if (!values.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(label);
            }
        }

        if (values.Count > MaxFilterValues)
        {
            errors.Add(new ValidationError(field, $"{field} accepts at most {MaxFilterValues} values."));
            return [];
        }

        return values.AsReadOnly();
    }

    private static decimal? NormaliseMoney(decimal? value, string field, List<ValidationError> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (value < 0m || value > MaxPriceValue)
        {
            errors.Add(new ValidationError(field, $"{field} must be between 0 and {MaxPriceValue:0}."));
            return null;
        }

        return decimal.Round(value.Value, 2, MidpointRounding.AwayFromZero);
    }

    private static decimal? NormaliseRange(
        decimal? value,
        string field,
        decimal minimum,
        decimal maximum,
        List<ValidationError> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (value < minimum || value > maximum)
        {
            errors.Add(new ValidationError(field, $"{field} must be between {minimum:0.##} and {maximum:0.##}."));
            return null;
        }

        return value;
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex SlugPattern();
}
