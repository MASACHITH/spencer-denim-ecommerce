using System.Collections.ObjectModel;

namespace SpencerDenim.Application.Catalog.Products;

/// <summary>The seven sorts PLP-04 offers, in the order doc 08 §7.3 defines them.</summary>
public enum ProductSortOption : byte
{
    Recommended = 0,
    Newest = 1,
    BestSelling = 2,
    PriceAscending = 3,
    PriceDescending = 4,
    HighestRated = 5,
    BiggestDiscount = 6,
}

/// <summary>Maps the <c>sort</c> query-string value to <see cref="ProductSortOption"/> and back.</summary>
public static class ProductSortOptions
{
    public const string Recommended = "recommended";
    public const string Newest = "newest";
    public const string BestSelling = "best-selling";
    public const string PriceAscending = "price-asc";
    public const string PriceDescending = "price-desc";
    public const string HighestRated = "rating";
    public const string BiggestDiscount = "discount";

    private static readonly ReadOnlyDictionary<string, ProductSortOption> ByValue =
        new(new Dictionary<string, ProductSortOption>(StringComparer.OrdinalIgnoreCase)
        {
            [Recommended] = ProductSortOption.Recommended,
            [Newest] = ProductSortOption.Newest,
            [BestSelling] = ProductSortOption.BestSelling,
            [PriceAscending] = ProductSortOption.PriceAscending,
            [PriceDescending] = ProductSortOption.PriceDescending,
            [HighestRated] = ProductSortOption.HighestRated,
            [BiggestDiscount] = ProductSortOption.BiggestDiscount,
        });

    private static readonly ReadOnlyDictionary<ProductSortOption, string> ByOption =
        new(ByValue.ToDictionary(pair => pair.Value, pair => pair.Key));

    /// <summary>Every accepted <c>sort</c> value, for the 400 message and the OpenAPI description.</summary>
    public static IReadOnlyList<string> All { get; } = ByValue.Keys.ToList().AsReadOnly();

    /// <summary>Parses a <c>sort</c> value. An absent or blank value means <see cref="ProductSortOption.Recommended"/>.</summary>
    public static bool TryParse(string? value, out ProductSortOption sort)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            sort = ProductSortOption.Recommended;
            return true;
        }

        return ByValue.TryGetValue(value.Trim(), out sort);
    }

    /// <summary>The query-string value for a sort, so responses and links can echo it back.</summary>
    public static string ToQueryValue(ProductSortOption sort) =>
        ByOption.TryGetValue(sort, out var value) ? value : Recommended;
}
