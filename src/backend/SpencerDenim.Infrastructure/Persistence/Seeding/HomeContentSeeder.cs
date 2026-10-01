using Microsoft.EntityFrameworkCore;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// The homepage and announcement part of doc 08 §9's idempotent seed step (N13): it inserts only
/// what is missing and never touches a row the client has edited.
/// <para>
/// <b>Sections, department tiles and benefits</b> are keyed on something the client cannot change
/// by editing copy — the section's type, the tile's department, the benefit's icon key — so
/// re-running this after a heading has been rewritten, the page reordered, a section switched on or
/// a benefit's wording replaced leaves all of it alone. Re-running it after one of those rows has
/// been <b>deleted</b> does put it back, which is the same trade-off the catalogue seeder makes for
/// a deleted department: the seeder is the definition of the shape a new store starts in,
/// and those rows are structure.
/// </para>
/// <para>
/// <b>Announcements are different, and are seeded all or nothing.</b> An announcement has no key
/// except the sentence itself, so "insert what is missing" and "the client rewrote it" are the same
/// event: an edited line looked like a missing one and the seeder put the original back beside it.
/// Worse, a placeholder the client <i>deleted</i> — "Free island-wide delivery on orders over
/// Rs. 10,000" is a promise they may have removed on purpose — came back on the next deployment, on
/// every page of the site. Doc 08 §9/N13 forbids overwriting an admin edit, so once the table has
/// any row in it at all, this step does nothing.
/// </para>
/// </summary>
public sealed class HomeContentSeeder(AppDbContext dbContext)
{
    public async Task<HomeContentSeedCounts> SeedAsync(CancellationToken cancellationToken)
    {
        var counts = new HomeContentSeedCounts
        {
            Sections = await SeedSectionsAsync(cancellationToken),
        };

        counts.SectionItems = await SeedDepartmentTilesAsync(cancellationToken)
            + await SeedBenefitsAsync(cancellationToken);
        counts.Announcements = await SeedAnnouncementsAsync(cancellationToken);

        return counts;
    }

    private async Task<int> SeedSectionsAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.HomeSections
            .Select(section => section.SectionType)
            .ToListAsync(cancellationToken);

        var present = existing.ToHashSet();

        var added = 0;
        foreach (var seed in HomeContentData.Sections)
        {
            if (!present.Add(seed.SectionType))
            {
                continue;
            }

            dbContext.HomeSections.Add(new HomeSection
            {
                SectionType = seed.SectionType,
                Title = seed.Title,
                Subtitle = seed.Subtitle,
                SettingsJson = seed.SettingsJson,
                SortOrder = seed.SortOrder,
                IsEnabled = seed.IsEnabled,
            });
            added++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return added;
    }

    /// <summary>
    /// HOME-02. Keyed on the department, so a tile the client has retitled or given a photograph to
    /// is left exactly as it is.
    /// </summary>
    private async Task<int> SeedDepartmentTilesAsync(CancellationToken cancellationToken)
    {
        var section = await FindSectionAsync(HomeSectionType.ShopByDepartment, cancellationToken);
        if (section is null)
        {
            return 0;
        }

        var departments = await dbContext.Departments
            .Select(department => new { department.Id, department.Slug })
            .ToListAsync(cancellationToken);

        var byslug = departments.ToDictionary(
            department => department.Slug,
            department => department.Id,
            StringComparer.OrdinalIgnoreCase);

        var taken = await dbContext.HomeSectionItems
            .Where(item => item.HomeSectionId == section.Value && item.DepartmentId != null)
            .Select(item => item.DepartmentId!.Value)
            .ToListAsync(cancellationToken);

        var present = taken.ToHashSet();

        var added = 0;
        foreach (var seed in HomeContentData.DepartmentTiles)
        {
            // A department the client has renamed the slug of, or one that is not seeded at all:
            // no tile rather than a tile pointing nowhere.
            if (!byslug.TryGetValue(seed.DepartmentSlug, out var departmentId) || !present.Add(departmentId))
            {
                continue;
            }

            dbContext.HomeSectionItems.Add(new HomeSectionItem
            {
                HomeSectionId = section.Value,
                DepartmentId = departmentId,
                LinkUrl = "/" + seed.DepartmentSlug,
                ButtonLabel = seed.ButtonLabel,
                SortOrder = seed.SortOrder,
                IsEnabled = true,
            });
            added++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return added;
    }

    /// <summary>HOME-09. Keyed on the icon key, which is the one field the copy does not change.</summary>
    private async Task<int> SeedBenefitsAsync(CancellationToken cancellationToken)
    {
        var section = await FindSectionAsync(HomeSectionType.Benefits, cancellationToken);
        if (section is null)
        {
            return 0;
        }

        var taken = await dbContext.HomeSectionItems
            .Where(item => item.HomeSectionId == section.Value && item.IconKey != null)
            .Select(item => item.IconKey!)
            .ToListAsync(cancellationToken);

        var present = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var seed in HomeContentData.Benefits)
        {
            if (!present.Add(seed.IconKey))
            {
                continue;
            }

            dbContext.HomeSectionItems.Add(new HomeSectionItem
            {
                HomeSectionId = section.Value,
                IconKey = seed.IconKey,
                Title = seed.Title,
                Body = seed.Body,
                SortOrder = seed.SortOrder,
                IsEnabled = true,
            });
            added++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return added;
    }

    /// <summary>
    /// GLB-03, all or nothing.
    ///
    /// <para>
    /// The message <i>is</i> the row, so there is no key to match on that survives the client
    /// editing it. The test is therefore "has anyone put anything here yet", not "is this exact
    /// sentence present": the strip belongs to the client from the first time they touch it, and a
    /// line they deleted stays deleted.
    /// </para>
    /// </summary>
    private async Task<int> SeedAnnouncementsAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.AnnouncementMessages.AnyAsync(cancellationToken))
        {
            return 0;
        }

        var added = 0;
        foreach (var seed in HomeContentData.Announcements)
        {

            dbContext.AnnouncementMessages.Add(new AnnouncementMessage
            {
                Message = seed.Message,
                SortOrder = seed.SortOrder,
                IsActive = true,
            });
            added++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return added;
    }

    private async Task<int?> FindSectionAsync(HomeSectionType type, CancellationToken cancellationToken)
    {
        var id = await dbContext.HomeSections
            .Where(section => section.SectionType == type)
            .Select(section => (int?)section.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return id;
    }
}

/// <summary>What <see cref="HomeContentSeeder"/> inserted, folded into the run's totals.</summary>
public sealed class HomeContentSeedCounts
{
    public int Sections { get; set; }

    public int SectionItems { get; set; }

    public int Announcements { get; set; }
}
