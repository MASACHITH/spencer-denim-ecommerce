using Microsoft.EntityFrameworkCore;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// The idempotent reference-data step of doc 08 §9 (N13): it inserts only the rows that are missing
/// and never overwrites an admin edit, so running it again after the client has renamed a category or
/// reordered a department is safe.
/// <para>
/// Doc 08 §9 seeds departments with EF <c>HasData</c>. They are written here instead, because
/// departments are admin-editable (<c>[A] [RV]</c>) and "insert only what is missing" is strictly
/// safer than a model-driven seed row that EF re-applies whenever the seed value changes. On an empty
/// database the three rows still land as IDs 1, 2 and 3, in the order doc 08 §3.2 gives.
/// </para>
/// </summary>
public sealed class CatalogReferenceDataSeeder(AppDbContext dbContext)
{
    public async Task<SeedCounts> SeedAsync(CancellationToken cancellationToken)
    {
        var counts = new SeedCounts();

        counts.Departments = await SeedDepartmentsAsync(cancellationToken);
        counts.AgeGroups = await SeedAgeGroupsAsync(cancellationToken);
        counts.AttributeOptions = await SeedAttributeOptionsAsync(cancellationToken);
        counts.ColourFamilies = await SeedColourFamiliesAsync(cancellationToken);
        counts.Sizes = await SeedSizesAsync(cancellationToken);
        counts.SizeGuides = await SeedSizeGuidesAsync(cancellationToken);
        counts.Categories = await SeedCategoriesAsync(cancellationToken);

        return counts;
    }

    private async Task<int> SeedDepartmentsAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.Departments
            .Select(department => department.Slug)
            .ToListAsync(cancellationToken);

        var added = 0;
        foreach (var seed in CatalogReferenceData.Departments)
        {
            if (existing.Contains(seed.Slug, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            dbContext.Departments.Add(new Department
            {
                Name = seed.Name,
                Slug = seed.Slug,
                Description = seed.Description,
                SortOrder = seed.SortOrder,
                IsActive = true,
                SeoTitle = seed.SeoTitle,
                SeoDescription = seed.Description,
            });
            added++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return added;
    }

    private async Task<int> SeedAgeGroupsAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.AgeGroups.Select(group => group.Slug).ToListAsync(cancellationToken);

        var added = 0;
        foreach (var seed in CatalogReferenceData.AgeGroups)
        {
            if (existing.Contains(seed.Slug, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            dbContext.AgeGroups.Add(new AgeGroup
            {
                Name = seed.Name,
                Slug = seed.Slug,
                MinMonths = seed.MinMonths,
                MaxMonths = seed.MaxMonths,
                SortOrder = seed.SortOrder,
                IsActive = true,
            });
            added++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return added;
    }

    private async Task<int> SeedAttributeOptionsAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.AttributeOptions
            .Select(option => new { option.AttributeType, option.Slug })
            .ToListAsync(cancellationToken);

        var keys = existing
            .Select(option => $"{(byte)option.AttributeType}:{option.Slug}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var seed in CatalogReferenceData.AttributeOptions)
        {
            if (!keys.Add($"{(byte)seed.Type}:{seed.Slug}"))
            {
                continue;
            }

            dbContext.AttributeOptions.Add(new AttributeOption
            {
                AttributeType = seed.Type,
                Name = seed.Name,
                Slug = seed.Slug,
                SortOrder = seed.SortOrder,
                IsActive = true,
            });
            added++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return added;
    }

    private async Task<int> SeedColourFamiliesAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.ColourFamilies.Select(family => family.Slug).ToListAsync(cancellationToken);

        var added = 0;
        foreach (var seed in CatalogReferenceData.ColourFamilies)
        {
            if (existing.Contains(seed.Slug, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            dbContext.ColourFamilies.Add(new ColourFamily
            {
                Name = seed.Name,
                Slug = seed.Slug,
                HexCode = seed.HexCode,
                SortOrder = seed.SortOrder,
            });
            added++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return added;
    }

    private async Task<int> SeedSizesAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.Sizes
            .Select(size => new { size.ScaleName, size.Label })
            .ToListAsync(cancellationToken);

        var keys = existing
            .Select(size => $"{size.ScaleName}|{size.Label}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var seed in CatalogReferenceData.Sizes)
        {
            if (!keys.Add($"{seed.ScaleName}|{seed.Label}"))
            {
                continue;
            }

            dbContext.Sizes.Add(new Size
            {
                ScaleName = seed.ScaleName,
                Label = seed.Label,
                SortOrder = seed.SortOrder,
            });
            added++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return added;
    }

    private async Task<int> SeedSizeGuidesAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.SizeGuides.Select(guide => guide.Name).ToListAsync(cancellationToken);

        var added = 0;
        foreach (var seed in CatalogReferenceData.SizeGuides)
        {
            if (existing.Contains(seed.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            dbContext.SizeGuides.Add(new SizeGuide
            {
                Name = seed.Name,
                IntroHtml = $"<p>{seed.Intro}</p>",
                TableJson = seed.TableJson,
                MeasuringTipsHtml = $"<p>{seed.MeasuringTips}</p>",
            });
            added++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return added;
    }

    /// <summary>
    /// Categories are written parent-first, so a child's <c>Path</c> and <c>Depth</c> can be built
    /// from the parent that was just saved (doc 08 §3.2: the path is maintained, never derived at
    /// query time).
    /// </summary>
    private async Task<int> SeedCategoriesAsync(CancellationToken cancellationToken)
    {
        var departments = await dbContext.Departments
            .Select(department => new { department.Id, department.Slug })
            .ToDictionaryAsync(department => department.Slug, department => department.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var ageGroups = await dbContext.AgeGroups
            .Select(group => new { group.Id, group.Slug })
            .ToDictionaryAsync(group => group.Slug, group => group.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var sizeGuides = await dbContext.SizeGuides
            .Select(guide => new { guide.Id, guide.Name })
            .ToDictionaryAsync(guide => guide.Name, guide => guide.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var added = 0;

        // Ordered so that a parent is always created before its children.
        foreach (var seed in CatalogReferenceData.Categories.OrderBy(category => category.ParentSlug is null ? 0 : 1))
        {
            if (!departments.TryGetValue(seed.DepartmentSlug, out var departmentId))
            {
                continue;
            }

            var existing = await dbContext.Categories
                .FirstOrDefaultAsync(
                    category => category.DepartmentId == departmentId && category.Slug == seed.Slug,
                    cancellationToken);

            if (existing is not null)
            {
                continue;
            }

            Category? parent = null;
            if (seed.ParentSlug is { } parentSlug)
            {
                parent = await dbContext.Categories
                    .FirstOrDefaultAsync(
                        category => category.DepartmentId == departmentId && category.Slug == parentSlug,
                        cancellationToken);

                if (parent is null)
                {
                    continue;
                }
            }

            var category = new Category
            {
                DepartmentId = departmentId,
                ParentCategoryId = parent?.Id,
                Depth = (byte)((parent?.Depth ?? 0) + 1),
                Name = seed.Name,
                Slug = seed.Slug,
                Description = seed.Description,
                SortOrder = seed.SortOrder,
                ShowInMegaMenu = true,
                MegaMenuColumn = (byte)(((seed.SortOrder - 1) % 4) + 1),
                IsActive = true,
                AgeGroupId = seed.AgeGroupSlug is { } ageSlug && ageGroups.TryGetValue(ageSlug, out var ageGroupId)
                    ? ageGroupId
                    : null,
                SizeGuideId = seed.SizeGuideName is { } guideName && sizeGuides.TryGetValue(guideName, out var guideId)
                    ? guideId
                    : null,
                SeoTitle = $"{seed.Name} | Spencer Denim",
                SeoDescription = seed.Description,
                Path = string.Empty,
            };

            dbContext.Categories.Add(category);

            // The path needs the identity value, so it is written straight after the insert.
            await dbContext.SaveChangesAsync(cancellationToken);
            category.Path = Category.BuildPath(parent?.Path, category.Id);
            await dbContext.SaveChangesAsync(cancellationToken);

            added++;
        }

        return added;
    }
}

/// <summary>How many rows each step inserted. Zero everywhere means the database was already seeded.</summary>
public sealed class SeedCounts
{
    public int Departments { get; set; }

    public int AgeGroups { get; set; }

    public int AttributeOptions { get; set; }

    public int ColourFamilies { get; set; }

    public int Sizes { get; set; }

    public int SizeGuides { get; set; }

    public int Categories { get; set; }

    public int Products { get; set; }

    public int Variants { get; set; }

    public int MediaAssets { get; set; }

    /// <summary>Demo placeholder image sets drawn and written by the media pipeline (W1-04).</summary>
    public int MediaPlaceholders { get; set; }

    public int HomeSections { get; set; }

    public int HomeSectionItems { get; set; }

    public int AnnouncementMessages { get; set; }

    public override string ToString() =>
        $"departments {Departments}, age groups {AgeGroups}, attribute options {AttributeOptions}, " +
        $"colour families {ColourFamilies}, sizes {Sizes}, size guides {SizeGuides}, categories {Categories}, " +
        $"products {Products}, variants {Variants}, media assets {MediaAssets}, " +
        $"media placeholders {MediaPlaceholders}, " +
        $"home sections {HomeSections}, home section items {HomeSectionItems}, " +
        $"announcements {AnnouncementMessages}";
}
