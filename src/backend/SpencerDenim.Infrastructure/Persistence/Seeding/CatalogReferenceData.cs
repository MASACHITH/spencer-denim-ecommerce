using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// The admin-editable catalogue reference data of doc 08 §9. It is data, not behaviour, so it lives
/// apart from the seeder that writes it.
/// </summary>
internal static class CatalogReferenceData
{
    public static IReadOnlyList<DepartmentSeed> Departments { get; } =
    [
        new("Women", "women", "Everyday essentials and occasion pieces, cut for Sri Lankan weather.", 1, "Women's clothing | Spencer Denim"),
        new("Men", "men", "Workwear, weekend shirts and denim built to last.", 2, "Men's clothing | Spencer Denim"),
        new("Kids", "kids", "Girls, boys and baby — soft fabrics and room to grow.", 3, "Kids' clothing | Spencer Denim"),
    ];

    public static IReadOnlyList<AgeGroupSeed> AgeGroups { get; } =
    [
        new("Baby", "baby", 0, 24, 1),
        new("Toddler", "toddler", 24, 59, 2),
        new("Kids", "kids", 60, 119, 3),
        new("Juniors", "juniors", 120, 191, 4),
    ];

    public static IReadOnlyList<AttributeOptionSeed> AttributeOptions { get; } =
    [
        new(AttributeOptionType.Material, "Cotton", "cotton", 1),
        new(AttributeOptionType.Material, "100% Cotton", "100-cotton", 2),
        new(AttributeOptionType.Material, "Cotton blend", "cotton-blend", 3),
        new(AttributeOptionType.Material, "Linen", "linen", 4),
        new(AttributeOptionType.Material, "Linen blend", "linen-blend", 5),
        new(AttributeOptionType.Material, "Denim", "denim", 6),
        new(AttributeOptionType.Material, "Viscose", "viscose", 7),
        new(AttributeOptionType.Material, "Polyester blend", "polyester-blend", 8),

        new(AttributeOptionType.Fit, "Regular", "regular", 1),
        new(AttributeOptionType.Fit, "Slim", "slim", 2),
        new(AttributeOptionType.Fit, "Relaxed", "relaxed", 3),
        new(AttributeOptionType.Fit, "Wide Leg", "wide-leg", 4),
        new(AttributeOptionType.Fit, "Oversized", "oversized", 5),

        new(AttributeOptionType.Occasion, "Casual", "casual", 1),
        new(AttributeOptionType.Occasion, "Smart Casual", "smart-casual", 2),
        new(AttributeOptionType.Occasion, "Formal", "formal", 3),
        new(AttributeOptionType.Occasion, "Party", "party", 4),
        new(AttributeOptionType.Occasion, "Traditional", "traditional", 5),
        new(AttributeOptionType.Occasion, "Active", "active", 6),
    ];

    public static IReadOnlyList<ColourFamilySeed> ColourFamilies { get; } =
    [
        new("White", "white", "#FFFFFF", 1),
        new("Cream", "cream", "#F3EADA", 2),
        new("Beige", "beige", "#D8CBB6", 3),
        new("Brown", "brown", "#6B4F3A", 4),
        new("Black", "black", "#101010", 5),
        new("Grey", "grey", "#8A8D91", 6),
        new("Navy", "navy", "#1F2A44", 7),
        new("Blue", "blue", "#2E6DB4", 8),
        new("Green", "green", "#2E7D5B", 9),
        new("Red", "red", "#C0392B", 10),
        new("Pink", "pink", "#E8A0BF", 11),
        new("Purple", "purple", "#6C4A8F", 12),
        new("Yellow", "yellow", "#E8C55F", 13),
        new("Orange", "orange", "#D98032", 14),
    ];

    /// <summary>Doc 08 §3.2: the scale name replaces the removed SizeScales table (simplification 6).</summary>
    public const string AlphaScale = "Alpha";
    public const string WomenWaistScale = "Women waist";
    public const string MenWaistScale = "Men waist";
    public const string KidsAgeScale = "Kids age";
    public const string BabyMonthsScale = "Baby months";
    public const string FootwearScale = "Footwear EU";
    public const string OneSizeScale = "One size";

    public static IReadOnlyList<SizeSeed> Sizes { get; } = BuildSizes();

    public static IReadOnlyList<SizeGuideSeed> SizeGuides { get; } =
    [
        new(
            "Women's apparel",
            "Measurements are in centimetres and describe the body, not the garment.",
            """
            {"columns":["Size","Bust","Waist","Hip"],"rows":[["XS","78-82","60-64","86-90"],["S","83-87","65-69","91-95"],["M","88-93","70-75","96-101"],["L","94-99","76-81","102-107"],["XL","100-106","82-88","108-114"],["XXL","107-113","89-95","115-121"]]}
            """,
            "Measure over light clothing, keeping the tape level and snug but not tight."),
        new(
            "Men's apparel",
            "Measurements are in centimetres and describe the body, not the garment.",
            """
            {"columns":["Size","Chest","Waist","Sleeve"],"rows":[["S","88-93","74-79","60"],["M","94-99","80-85","62"],["L","100-105","86-91","64"],["XL","106-112","92-98","65"],["XXL","113-119","99-105","66"]]}
            """,
            "For shirts, measure the fullest part of the chest with your arms relaxed."),
        new(
            "Kids and baby",
            "Sizes follow age. When a child sits between two sizes, take the larger one.",
            """
            {"columns":["Size","Height","Chest","Waist"],"rows":[["2-3Y","92-98","53-55","51-52"],["4-5Y","104-110","57-59","53-54"],["6-7Y","116-122","61-64","55-57"],["8-9Y","128-134","66-69","58-60"],["10-11Y","140-146","71-75","61-64"],["12-13Y","152-158","77-82","65-69"]]}
            """,
            "Growing children are measured best standing straight, without shoes."),
    ];

    /// <summary>
    /// The department → category tree of doc 08 §9. Kids is three levels deep, which is the maximum
    /// <c>CK_Categories_Depth</c> allows.
    /// </summary>
    public static IReadOnlyList<CategorySeed> Categories { get; } =
    [
        // Women
        new("women", "Dresses", "dresses", 1, "From weekday shifts to evening midis.", null, null, "Women's apparel"),
        new("women", "Tops & T-Shirts", "tops-t-shirts", 2, "Everyday jersey, knits and camisoles.", null, null, "Women's apparel"),
        new("women", "Shirts & Blouses", "shirts-blouses", 3, "Cotton and linen shirting for office and off-duty.", null, null, "Women's apparel"),
        new("women", "Jeans", "jeans", 4, "Denim in high-rise, straight and wide-leg cuts.", null, null, "Women's apparel"),
        new("women", "Trousers", "trousers", 5, "Tailored, drawstring and cropped trousers.", null, null, "Women's apparel"),
        new("women", "Skirts", "skirts", 6, "Mini, midi and maxi in easy fabrics.", null, null, "Women's apparel"),
        new("women", "Traditional Wear", "traditional-wear", 7, "Handloom saris, osariya wraps and kurta sets.", null, null, "Women's apparel"),
        new("women", "Activewear", "activewear", 8, "Studio and street pieces that move with you.", null, null, "Women's apparel"),
        new("women", "Footwear", "footwear", 9, "Sandals, flats and everyday sneakers.", null, null, null),
        new("women", "Accessories", "accessories", 10, "Bags, belts, scarves and small leather goods.", null, null, null),

        // Men
        new("men", "Shirts", "shirts", 1, "Linen, oxford and poplin, cut for the climate.", null, null, "Men's apparel"),
        new("men", "T-Shirts & Polos", "t-shirts-polos", 2, "Heavyweight jersey and piqué polos.", null, null, "Men's apparel"),
        new("men", "Jeans", "jeans", 3, "Slim, straight and relaxed denim.", null, null, "Men's apparel"),
        new("men", "Trousers", "trousers", 4, "Chinos and tailored trousers for work and weekend.", null, null, "Men's apparel"),
        new("men", "Shorts", "shorts", 5, "Cotton and linen shorts for warm days.", null, null, "Men's apparel"),
        new("men", "Activewear", "activewear", 6, "Training tees, shorts and layers.", null, null, "Men's apparel"),
        new("men", "Footwear", "footwear", 7, "Sneakers, loafers and sandals.", null, null, null),
        new("men", "Accessories", "accessories", 8, "Belts, wallets, caps and socks.", null, null, null),

        // Kids — level 1
        new("kids", "Girls", "girls", 1, "Dresses, tops and bottoms for girls.", null, "kids", "Kids and baby"),
        new("kids", "Boys", "boys", 2, "Shirts, tees and bottoms for boys.", null, "kids", "Kids and baby"),
        new("kids", "Baby", "baby", 3, "Soft cotton for the first two years.", null, "baby", "Kids and baby"),
        new("kids", "Footwear", "kids-footwear", 4, "School shoes, sandals and sneakers.", null, null, null),

        // Kids — level 2
        new("kids", "Girls Dresses", "girls-dresses", 1, "Cotton dresses for school holidays and parties.", "girls", "kids", "Kids and baby"),
        new("kids", "Girls Tops", "girls-tops", 2, "Tees, blouses and layering pieces.", "girls", "kids", "Kids and baby"),
        new("kids", "Girls Bottoms", "girls-bottoms", 3, "Leggings, shorts and skirts.", "girls", "kids", "Kids and baby"),
        new("kids", "Boys Shirts & T-Shirts", "boys-shirts-t-shirts", 1, "Everyday tees and buttoned shirts.", "boys", "kids", "Kids and baby"),
        new("kids", "Boys Bottoms", "boys-bottoms", 2, "Shorts, joggers and jeans.", "boys", "kids", "Kids and baby"),
        new("kids", "Baby Bodysuits & Sets", "baby-bodysuits-sets", 1, "Bodysuits, sleepsuits and two-piece sets.", "baby", "baby", "Kids and baby"),
        new("kids", "Baby Accessories", "baby-accessories", 2, "Bibs, hats, mittens and socks.", "baby", "baby", null),
    ];

    private static List<SizeSeed> BuildSizes()
    {
        var sizes = new List<SizeSeed>(64);

        void Add(string scale, IEnumerable<string> labels)
        {
            short order = 1;
            foreach (var label in labels)
            {
                sizes.Add(new SizeSeed(scale, label, order));
                order++;
            }
        }

        Add(AlphaScale, ["XS", "S", "M", "L", "XL", "XXL", "XXXL"]);
        Add(WomenWaistScale, ["24", "26", "28", "30", "32", "34", "36"]);
        Add(MenWaistScale, ["28", "30", "32", "34", "36", "38", "40"]);
        Add(KidsAgeScale, ["2-3Y", "3-4Y", "4-5Y", "5-6Y", "6-7Y", "7-8Y", "8-9Y", "9-10Y", "10-11Y", "11-12Y", "12-13Y", "13-14Y"]);
        Add(BabyMonthsScale, ["0-3M", "3-6M", "6-9M", "9-12M", "12-18M", "18-24M"]);
        Add(FootwearScale, ["35", "36", "37", "38", "39", "40", "41", "42", "43", "44", "45", "46"]);
        Add(OneSizeScale, ["One Size"]);

        return sizes;
    }
}

/// <param name="SeoTitle">
/// The whole page title, brand included, because it is copy rather than a pattern. Building it as
/// <c>$"{Name}'s clothing"</c> read as "Kids's clothing" on every Kids listing: English possessives
/// are not a string operation. Admin edits it like any other SEO field.
/// </param>
internal sealed record DepartmentSeed(
    string Name,
    string Slug,
    string Description,
    short SortOrder,
    string SeoTitle);

internal sealed record AgeGroupSeed(string Name, string Slug, short MinMonths, short MaxMonths, short SortOrder);

internal sealed record AttributeOptionSeed(AttributeOptionType Type, string Name, string Slug, short SortOrder);

internal sealed record ColourFamilySeed(string Name, string Slug, string HexCode, short SortOrder);

internal sealed record SizeSeed(string ScaleName, string Label, short SortOrder);

internal sealed record SizeGuideSeed(string Name, string Intro, string TableJson, string MeasuringTips);

internal sealed record CategorySeed(
    string DepartmentSlug,
    string Name,
    string Slug,
    int SortOrder,
    string Description,
    string? ParentSlug,
    string? AgeGroupSlug,
    string? SizeGuideName);
