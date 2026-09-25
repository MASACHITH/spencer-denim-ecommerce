using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// The dev and staging sample catalogue of doc 08 §9. Idempotent on <c>ProductCode</c>: a product that
/// already exists is skipped whole, so re-running the seeder never duplicates rows and never
/// overwrites an edit made in the admin.
/// <para>
/// Media rows point at <c>public/originals/demo/…</c>; the files behind them are drawn by
/// <c>DemoMediaPlaceholderGenerator</c> (W1-04) as labelled placeholders, never as invented product
/// photography. See <see cref="DemoMediaPlaceholders"/>.
/// </para>
/// </summary>
public sealed class DemoCatalogueSeeder(AppDbContext dbContext, IClock clock, IOptions<CatalogOptions> options)
{
    private readonly CatalogOptions _options = options.Value;

    public async Task<SeedCounts> SeedAsync(CancellationToken cancellationToken)
    {
        var counts = new SeedCounts();

        var lookups = await LoadLookupsAsync(cancellationToken);
        var utcNow = clock.UtcNow.UtcDateTime;

        var existingCodes = await dbContext.Products
            .IgnoreQueryFilters()
            .Select(product => product.ProductCode)
            .ToListAsync(cancellationToken);

        var codes = existingCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var categoryMerchandisingOrder = new Dictionary<int, int>();

        foreach (var seed in DemoCatalogueData.Products)
        {
            if (!codes.Add(seed.Code))
            {
                continue;
            }

            var created = await CreateProductAsync(seed, lookups, utcNow, categoryMerchandisingOrder, cancellationToken);
            counts.Products++;
            counts.Variants += created.VariantCount;
            counts.MediaAssets += created.MediaAssetCount;
        }

        await LinkRelatedProductsAsync(cancellationToken);

        return counts;
    }

    private async Task<CreatedProduct> CreateProductAsync(
        DemoProductSeed seed,
        Lookups lookups,
        DateTime utcNow,
        Dictionary<int, int> categoryMerchandisingOrder,
        CancellationToken cancellationToken)
    {
        var departmentId = lookups.Departments[seed.DepartmentSlug];
        var category = lookups.Categories[(departmentId, seed.CategorySlug)];

        // Media first: the product carries denormalised card and hover asset IDs (doc 08 §7.4).
        var assets = CreateMediaAssets(seed);
        dbContext.MediaAssets.AddRange(assets);
        await dbContext.SaveChangesAsync(cancellationToken);

        var firstPublished = utcNow.AddDays(-seed.PublishedDaysAgo);

        var product = new Product
        {
            ProductCode = seed.Code,
            Name = seed.Name,
            Slug = seed.Slug,
            DepartmentId = departmentId,
            PrimaryCategoryId = category.Id,
            AgeGroupId = seed.AgeGroupSlug is { } ageSlug ? lookups.AgeGroups[ageSlug] : null,
            ShortDescription = seed.ShortDescription,
            Description = seed.Description,
            CareInstructions = seed.CareInstructions,
            MaterialId = lookups.Attributes[(AttributeOptionType.Material, seed.MaterialSlug)],
            FitId = lookups.Attributes[(AttributeOptionType.Fit, seed.FitSlug)],
            OccasionId = lookups.Attributes[(AttributeOptionType.Occasion, seed.OccasionSlug)],
            BasePrice = seed.BasePrice,
            CompareAtPrice = seed.CompareAtPrice,
            BadgeOverride = seed.BadgeOverride,
            RecommendedRank = seed.RecommendedRank,
            SizeGuideId = category.SizeGuideId,
            CardImageAssetId = assets[0].Id,
            HoverImageAssetId = assets[^1].Id,
            FirstPublishedAtUtc = firstPublished,
            SeoTitle = $"{seed.Name} | Spencer Denim",
            SeoDescription = seed.ShortDescription,
        };

        product.Publish(utcNow, _options.NewArrivalDays);

        dbContext.Products.Add(product);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Category links. Doc 08 §3.2: the primary category is stored here too, and SortOrder is the
        // merchandiser's manual order inside that category (§7.3). Only the first few products in a
        // category are pinned, so the Recommended sort has both pinned and unpinned rows to order.
        categoryMerchandisingOrder.TryGetValue(category.Id, out var position);
        categoryMerchandisingOrder[category.Id] = position + 1;

        dbContext.ProductCategories.Add(new ProductCategory
        {
            CategoryId = category.Id,
            ProductId = product.Id,
            SortOrder = position < 3 ? position + 1 : null,
        });

        var colours = new List<ProductColour>(seed.Colours.Length);
        short colourOrder = 1;
        foreach (var colourSeed in seed.Colours)
        {
            colours.Add(new ProductColour
            {
                ProductId = product.Id,
                Name = colourSeed.Name,
                ColourFamilyId = lookups.ColourFamilies[colourSeed.FamilySlug],
                SwatchHex = colourSeed.Hex,
                SortOrder = colourOrder,
            });
            colourOrder++;
        }

        dbContext.ProductColours.AddRange(colours);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Gallery: one image per colourway, then a shared detail shot with the Hover role.
        short mediaOrder = 1;
        for (var index = 0; index < colours.Count; index++)
        {
            dbContext.ProductMedia.Add(new ProductMedia
            {
                ProductId = product.Id,
                MediaAssetId = assets[Math.Min(index, assets.Count - 2)].Id,
                ProductColourId = colours[index].Id,
                MediaRole = ProductMediaRole.Gallery,
                SortOrder = mediaOrder,
                AltTextOverride = DemoMediaPlaceholders.AltTextFor(seed.Name, colours[index].Name),
            });
            mediaOrder++;
        }

        dbContext.ProductMedia.Add(new ProductMedia
        {
            ProductId = product.Id,
            MediaAssetId = assets[^1].Id,
            ProductColourId = null,
            MediaRole = ProductMediaRole.Hover,
            SortOrder = mediaOrder,
            AltTextOverride = DemoMediaPlaceholders.AltTextFor(seed.Name, colourName: null),
        });

        // Variants: colour × size, with deterministic stock so a re-seed of a fresh database produces
        // the same catalogue and the integration tests can assert on it.
        var random = new DeterministicRandom(seed.Code);
        var variants = new List<ProductVariant>(colours.Count * seed.SizeLabels.Length);
        var explicitThreshold = ShouldUseExplicitThreshold(seed.Code);

        for (var colourIndex = 0; colourIndex < colours.Count; colourIndex++)
        {
            short sizeOrder = 1;
            foreach (var label in seed.SizeLabels)
            {
                var sizeId = lookups.Sizes[(seed.SizeScale, label)];
                var priceOverride = PriceOverrideFor(seed, label);

                variants.Add(new ProductVariant
                {
                    ProductId = product.Id,
                    ProductColourId = colours[colourIndex].Id,
                    SizeId = sizeId,
                    Sku = BuildSku(seed.Code, colourIndex + 1, label),
                    PriceOverride = priceOverride,
                    CompareAtPriceOverride = priceOverride is { } price && seed.CompareAtPrice is not null
                        ? price + 500m
                        : null,
                    IsActive = true,
                    SortOrder = (short)((colourIndex * 100) + sizeOrder),
                });

                sizeOrder++;
            }
        }

        dbContext.ProductVariants.AddRange(variants);
        await dbContext.SaveChangesAsync(cancellationToken);

        var variantIndex = 0;
        foreach (var variant in variants)
        {
            dbContext.StockLevels.Add(new StockLevel
            {
                VariantId = variant.Id,
                OnHand = OnHandFor(seed.Stock, random),
                Reserved = 0,
                LowStockThreshold = explicitThreshold && variantIndex == 0 ? 3 : null,
                UpdatedAtUtc = utcNow,
            });
            variantIndex++;
        }

        AddMediaUsages(product, assets, utcNow);

        dbContext.ProductStats.Add(BuildStats(seed, product, variants, utcNow));
        dbContext.ProductSearchIndexes.Add(BuildSearchIndex(seed, product, category, colours, variants, lookups, utcNow));

        await dbContext.SaveChangesAsync(cancellationToken);

        return new CreatedProduct(variants.Count, assets.Count);
    }

    /// <summary>
    /// Doc 08 §3.3: every structured reference is mirrored in <c>media.MediaUsages</c>, which is what
    /// the library's "used in…" panel and the delete guard read. The unique index is
    /// (asset, owner type, owner id, role), so each asset contributes one row per role.
    /// </summary>
    private void AddMediaUsages(Product product, List<MediaAsset> assets, DateTime utcNow)
    {
        var ownerId = product.Id.ToString(CultureInfo.InvariantCulture);
        var written = new HashSet<(int AssetId, string Role)>();

        void Add(int assetId, string role)
        {
            if (!written.Add((assetId, role)))
            {
                return;
            }

            dbContext.MediaUsages.Add(new MediaUsage
            {
                MediaAssetId = assetId,
                OwnerType = MediaUsageOwnerTypes.Product,
                OwnerId = ownerId,
                UsageRole = role,
                CreatedAtUtc = utcNow,
            });
        }

        Add(assets[0].Id, MediaUsageRoles.Card);
        Add(assets[^1].Id, MediaUsageRoles.Hover);

        foreach (var asset in assets)
        {
            Add(asset.Id, MediaUsageRoles.Gallery);
        }
    }

    /// <summary>
    /// Doc 08 §6 steps 1, 2 and 4, aggregated over the product's variants. Campaigns (step 3) arrive
    /// with marketing, so the sale price equals the list price here.
    /// </summary>
    private static ProductStats BuildStats(
        DemoProductSeed seed,
        Product product,
        IReadOnlyList<ProductVariant> variants,
        DateTime utcNow)
    {
        var listPrices = variants
            .Select(variant => CatalogPricing.ListPrice(seed.BasePrice, variant.PriceOverride))
            .ToList();

        var originalPrices = variants
            .Select(variant => CatalogPricing.OriginalPrice(
                CatalogPricing.ListPrice(seed.BasePrice, variant.PriceOverride),
                seed.CompareAtPrice,
                variant.CompareAtPriceOverride))
            .ToList();

        var discounts = variants
            .Select(variant =>
            {
                var list = CatalogPricing.ListPrice(seed.BasePrice, variant.PriceOverride);
                var original = CatalogPricing.OriginalPrice(list, seed.CompareAtPrice, variant.CompareAtPriceOverride);
                return CatalogPricing.DiscountPercent(original, CatalogPricing.SalePrice(list, campaignPrice: null));
            })
            .ToList();

        var maxDiscount = discounts.Max();

        return new ProductStats
        {
            ProductId = product.Id,
            MinListPrice = listPrices.Min(),
            MaxListPrice = listPrices.Max(),
            MinSalePrice = listPrices.Min(),
            MaxSalePrice = listPrices.Max(),
            MaxOriginalPrice = originalPrices.Max(),
            MaxDiscountPercent = maxDiscount,
            HasDiscount = maxDiscount > 0m,
            RatingAverage = seed.Rating,
            RatingCount = seed.RatingCount,
            SoldQty30d = seed.SoldQty30d,
            SoldQtyTotal = seed.SoldQtyTotal,
            PricingRefreshedAtUtc = utcNow,
            StatsRefreshedAtUtc = utcNow,
        };
    }

    /// <summary>
    /// Doc 08 §3.2: name, category path names, colour names and families, material, fit, occasion,
    /// product code and SKUs, lower-cased. The column is <c>nvarchar(2000)</c>, so the text is capped.
    /// </summary>
    private static ProductSearchIndex BuildSearchIndex(
        DemoProductSeed seed,
        Product product,
        CategoryLookup category,
        IReadOnlyList<ProductColour> colours,
        IReadOnlyList<ProductVariant> variants,
        Lookups lookups,
        DateTime utcNow)
    {
        var parts = new List<string>(32) { seed.Name };
        parts.AddRange(category.PathNames);

        foreach (var colour in colours)
        {
            parts.Add(colour.Name);
        }

        foreach (var colourSeed in seed.Colours)
        {
            parts.Add(lookups.ColourFamilyNames[colourSeed.FamilySlug]);
        }

        parts.Add(lookups.AttributeNames[(AttributeOptionType.Material, seed.MaterialSlug)]);
        parts.Add(lookups.AttributeNames[(AttributeOptionType.Fit, seed.FitSlug)]);
        parts.Add(lookups.AttributeNames[(AttributeOptionType.Occasion, seed.OccasionSlug)]);
        parts.Add(seed.Code);
        parts.AddRange(variants.Select(variant => variant.Sku));

        var searchText = string.Join(' ', parts.Distinct(StringComparer.OrdinalIgnoreCase)).ToLowerInvariant();
        if (searchText.Length > 2000)
        {
            searchText = searchText[..2000];
        }

        return new ProductSearchIndex
        {
            ProductId = product.Id,
            NameText = seed.Name.ToLowerInvariant(),
            SearchText = searchText,
            UpdatedAtUtc = utcNow,
        };
    }

    /// <summary>
    /// PDP-06: three curated picks per product, taken from the same department but a different
    /// category, so the related rail is not a list of near-identical items.
    /// </summary>
    private async Task LinkRelatedProductsAsync(CancellationToken cancellationToken)
    {
        var products = await dbContext.Products
            .AsNoTracking()
            .Select(product => new { product.Id, product.DepartmentId, product.PrimaryCategoryId })
            .OrderBy(product => product.Id)
            .ToListAsync(cancellationToken);

        var existing = await dbContext.RelatedProducts
            .Select(related => related.ProductId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var linked = existing.ToHashSet();
        var added = false;

        foreach (var product in products)
        {
            if (!linked.Add(product.Id))
            {
                continue;
            }

            var picks = products
                .Where(candidate => candidate.DepartmentId == product.DepartmentId
                    && candidate.Id != product.Id
                    && candidate.PrimaryCategoryId != product.PrimaryCategoryId)
                .Take(3)
                .ToList();

            short order = 1;
            foreach (var pick in picks)
            {
                dbContext.RelatedProducts.Add(new RelatedProduct
                {
                    ProductId = product.Id,
                    RelatedProductId = pick.Id,
                    SortOrder = order,
                });
                order++;
                added = true;
            }
        }

        if (added)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The asset rows for one product. The bytes behind them are drawn by
    /// <c>DemoMediaPlaceholderGenerator</c> (W1-04), which also replaces the placeholder metadata below
    /// with the real size, checksum and dimensions of the file it writes.
    /// <para>
    /// The row is created as <see cref="MediaAssetStatus.Processing"/>, not <c>Ready</c>: until a file
    /// exists, <c>Ready</c> would be a claim the serving endpoint acts on, and it would answer 404 anyway
    /// because there is nothing on disk. A deployment with no media volume therefore keeps its demo images
    /// honestly marked as unfinished rather than broken.
    /// </para>
    /// </summary>
    private static List<MediaAsset> CreateMediaAssets(DemoProductSeed seed)
    {
        var placeholders = DemoMediaPlaceholders.ForProduct(seed);
        var assets = new List<MediaAsset>(placeholders.Count);
        var index = 1;

        foreach (var placeholder in placeholders)
        {
            assets.Add(new MediaAsset
            {
                PublicId = DeterministicGuid(placeholder.StorageKey),
                MediaType = MediaAssetType.Image,
                Visibility = MediaVisibility.Public,
                Status = MediaAssetStatus.Processing,
                OriginalFileName = DemoMediaPlaceholders.FileNameFor(seed.Slug, index),
                StorageKey = placeholder.StorageKey,
                ContentType = "image/jpeg",
                SizeBytes = 1,
                Checksum = SHA256.HashData(Encoding.UTF8.GetBytes(placeholder.StorageKey)),
                AltText = placeholder.AltText,
                Title = seed.Name,
            });

            index++;
        }

        return assets;
    }

    private static Guid DeterministicGuid(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private static string BuildSku(string code, int colourNumber, string sizeLabel)
    {
        var size = new string(sizeLabel.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return $"{code}-{colourNumber.ToString("00", CultureInfo.InvariantCulture)}-{size}";
    }

    /// <summary>The largest sizes cost a little more, which is what the variant price override is for.</summary>
    private static decimal? PriceOverrideFor(DemoProductSeed seed, string label) =>
        label is "XXL" or "XXXL" ? seed.BasePrice + 300m : null;

    /// <summary>
    /// Gives roughly one product in seven an explicit low-stock threshold, so the storefront exercises
    /// both the per-variant value and the configured fallback. Uses the same stable hash as
    /// <see cref="DeterministicRandom"/>, not <see cref="string.GetHashCode(string?)"/>, which .NET
    /// randomises per process.
    /// </summary>
    internal static bool ShouldUseExplicitThreshold(string code) => StableSeed(code) % 7 == 0;

    /// <summary>
    /// A stable 32-bit seed for a product code: the first four bytes of its SHA-256 digest, the same
    /// hash <see cref="DeterministicGuid"/> uses. Unsigned throughout, so there is no
    /// <c>Math.Abs(int.MinValue)</c> to overflow.
    /// </summary>
    internal static uint StableSeed(string value) =>
        BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static int OnHandFor(StockProfile profile, DeterministicRandom random) => profile switch
    {
        StockProfile.OutOfStock => 0,
        StockProfile.Low => 1 + random.Next(4),
        StockProfile.Mixed => random.Next(10) < 3 ? 0 : 6 + random.Next(30),
        _ => 8 + random.Next(40),
    };

    private async Task<Lookups> LoadLookupsAsync(CancellationToken cancellationToken)
    {
        var departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(department => department.Slug, department => department.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var ageGroups = await dbContext.AgeGroups
            .AsNoTracking()
            .ToDictionaryAsync(group => group.Slug, group => group.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var colourFamilies = await dbContext.ColourFamilies.AsNoTracking().ToListAsync(cancellationToken);
        var attributes = await dbContext.AttributeOptions.AsNoTracking().ToListAsync(cancellationToken);
        var sizes = await dbContext.Sizes.AsNoTracking().ToListAsync(cancellationToken);
        var categories = await dbContext.Categories.AsNoTracking().ToListAsync(cancellationToken);

        var categoriesById = categories.ToDictionary(category => category.Id);

        List<string> PathNames(Category category)
        {
            var names = new List<string>(3);
            var current = category;
            while (current is not null)
            {
                names.Insert(0, current.Name);
                current = current.ParentCategoryId is { } parentId && categoriesById.TryGetValue(parentId, out var parent)
                    ? parent
                    : null;
            }

            return names;
        }

        return new Lookups(
            departments,
            ageGroups,
            colourFamilies.ToDictionary(family => family.Slug, family => family.Id, StringComparer.OrdinalIgnoreCase),
            colourFamilies.ToDictionary(family => family.Slug, family => family.Name, StringComparer.OrdinalIgnoreCase),
            attributes.ToDictionary(option => (option.AttributeType, option.Slug), option => (int?)option.Id),
            attributes.ToDictionary(option => (option.AttributeType, option.Slug), option => option.Name),
            sizes.ToDictionary(size => (size.ScaleName, size.Label), size => size.Id),
            categories.ToDictionary(
                category => (category.DepartmentId, category.Slug),
                category => new CategoryLookup(category.Id, category.SizeGuideId, PathNames(category))));
    }

    private sealed record CreatedProduct(int VariantCount, int MediaAssetCount);

    private sealed record CategoryLookup(int Id, int? SizeGuideId, IReadOnlyList<string> PathNames);

    private sealed record Lookups(
        Dictionary<string, int> Departments,
        Dictionary<string, int> AgeGroups,
        Dictionary<string, int> ColourFamilies,
        Dictionary<string, string> ColourFamilyNames,
        Dictionary<(AttributeOptionType Type, string Slug), int?> Attributes,
        Dictionary<(AttributeOptionType Type, string Slug), string> AttributeNames,
        Dictionary<(string Scale, string Label), int> Sizes,
        Dictionary<(int DepartmentId, string Slug), CategoryLookup> Categories);

    /// <summary>
    /// A tiny linear congruential generator seeded from the product code. Deterministic, so the seeded
    /// catalogue is identical on every machine and the integration tests can assert exact counts.
    /// <para>
    /// The seed comes from <see cref="StableSeed"/> rather than <see cref="string.GetHashCode(string?)"/>:
    /// string hashing is randomised per process by default, which made the stock of a re-seeded database
    /// differ from run to run and the "partly sold out" assertions fail on a small share of CI runs.
    /// </para>
    /// It is never used for anything security-related.
    /// </summary>
    internal sealed class DeterministicRandom(string seed)
    {
        private uint _state = StableSeed(seed) | 1u;

        public int Next(int exclusiveMaximum)
        {
            _state = (_state * 1664525u) + 1013904223u;
            return (int)(_state >> 16) % exclusiveMaximum;
        }
    }
}
