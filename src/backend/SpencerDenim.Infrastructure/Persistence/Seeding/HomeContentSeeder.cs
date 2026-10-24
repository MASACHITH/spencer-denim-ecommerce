using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Domain.Settings;

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
public sealed class HomeContentSeeder(AppDbContext dbContext, IClock clock)
{
    public async Task<HomeContentSeedCounts> SeedAsync(CancellationToken cancellationToken)
    {
        var counts = new HomeContentSeedCounts
        {
            Sections = await SeedSectionsAsync(cancellationToken),
        };

        counts.SectionItems = await SeedDepartmentTilesAsync(cancellationToken)
            + await SeedBenefitsAsync(cancellationToken)
            + await SeedArtworkItemsAsync(cancellationToken);
        counts.Announcements = await SeedAnnouncementsAsync(cancellationToken);
        counts.MegaMenuCards = await SeedMegaMenuCardsAsync(cancellationToken);
        counts.ContentPages = await SeedContentPagesAsync(cancellationToken);
        counts.PolicyPages = await SeedPolicyPagesAsync(cancellationToken);

        return counts;
    }

    /// <summary>
    /// GLB-10, doc 08 §9: the six system pages. Five of them "unpublished until the client's copy
    /// (privacy and terms need PDPA review)", and Contact on the shop from the start — see
    /// <see cref="ContentPageKeys.Seeds"/> and the paragraph on the publish flag below.
    /// <para>
    /// <b>Keyed on <c>SystemKey</c></b>, which is the one field staff cannot change, so re-running this
    /// after the client has written About, renamed its slug, retitled it and published it leaves every one
    /// of those alone. A row that has been <i>soft-deleted</i> is also left alone, because the query
    /// ignores the soft-delete filter when it looks for what is present: a system page cannot be deleted
    /// through the API at all, so a deleted one is a deliberate data fix and putting it back on the next
    /// deployment would undo it silently.
    /// </para>
    /// <para>
    /// <b>The bodies are empty and nothing in this repository writes them.</b> Doc 11 P13 is explicit that
    /// the copy is the client's; a returns policy, a privacy policy and terms of business carry legal
    /// weight, and inventing them would put words the client never agreed to on their own shop. The
    /// <i>titles</i> are seeded, because a title is a label rather than copy and a page with no heading
    /// cannot be found on the admin list.
    /// </para>
    /// <para>
    /// <b>Contact is the one that is published on install, and it is published with an empty body</b>
    /// (owner decision, W2-11; see <see cref="ContentPageKeys.Seeds"/>). Its content is the enquiry form
    /// and the <c>Contact.*</c> settings rather than copy, so there is nothing whose absence makes it
    /// wrong — and leaving it unpublished would give a fresh shop no contact route at all, now that the
    /// WhatsApp float correctly hides itself until a number is set. The empty body is <i>not</i> relaxed
    /// for it: published and empty is exactly the intended state.
    /// </para>
    /// <para>
    /// <b>The publish flag decides what a new row looks like and nothing else.</b> The query above has
    /// already excluded every key that exists, so a client who takes Contact off the shop is not a
    /// "missing" page to be fixed on the next deployment — the same protection an edited heading or a
    /// rewritten body has. That is the half worth testing, and
    /// <c>HomeContentSeederTests</c> does.
    /// </para>
    /// </summary>
    private async Task<int> SeedContentPagesAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.ContentPages
            .IgnoreQueryFilters()
            .Where(page => page.SystemKey != null)
            .Select(page => page.SystemKey!)
            .ToListAsync(cancellationToken);

        // Case-insensitively, to match the database collation (Latin1_General_100_CI_AS_SC). An ordinal
        // set here would read `About` as missing next to a stored `about` and then be refused by
        // UX_ContentPages_SystemKey on save — which is the failure SettingCatalog.ByKey documents.
        var present = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var takenSlugs = (await dbContext.ContentPages
                .IgnoreQueryFilters()
                .Select(page => page.Slug)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var utcNow = clock.UtcNow.UtcDateTime;

        var added = 0;
        foreach (var seed in ContentPageKeys.Seeds)
        {
            if (!present.Add(seed.SystemKey))
            {
                continue;
            }

            // A custom page the client created on the slug this shell wants. The shell still has to exist
            // — it is the row the footer and the storefront expect — so it takes a suffixed slug rather
            // than being skipped, and staff can rename either of them afterwards. Without this the insert
            // was refused by UX_ContentPages_Slug and the whole reference-data seed failed on startup.
            var slug = takenSlugs.Contains(seed.Slug) ? seed.Slug + "-page" : seed.Slug;
            takenSlugs.Add(slug);

            dbContext.ContentPages.Add(new ContentPage
            {
                SystemKey = seed.SystemKey,
                Slug = slug,
                Title = seed.Title,
                BodyHtml = string.Empty,
                IsPublished = seed.PublishedOnInstall,

                // Stamped for the page that starts on the shop, because that page did go on the shop —
                // at install. Leaving it null beside IsPublished = true would be an inconsistent row,
                // and SetPublished assigns with `??=`, so the first unpublish-and-republish would then
                // record a date months after the page was actually first visible.
                PublishedAtUtc = seed.PublishedOnInstall ? utcNow : null,
            });
            added++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return added;
    }

    /// <summary>
    /// Issue #33 and issue #252: the starting copy of the Shipping and Returns pages (version 1) and of the About,
    /// Privacy and Terms pages (version 2), all from <see cref="PolicyPageContent"/>, published — the owner's
    /// instructions of 2026-10-21 and 2026-10-24.
    /// <para>
    /// <b>Offered once, by version</b> (<c>Seed.PolicyPagesVersion</c>, the #152 rule), after the shells above
    /// exist, and <b>once per page</b>: a page is considered only while the recorded version is below the version
    /// that first offered it (<see cref="PolicyPageContent.IntroducedIn"/>). So a database that already took
    /// version 1 is offered the three new pages and nothing else, and an admin who emptied Shipping or Returns
    /// after version 1 does not have them filled and published again by the bump.
    /// </para>
    /// <para>
    /// <b>Never over an admin's words:</b> a page is filled only when its body is still empty or still exactly
    /// an earlier version's seed, and only that page is published. A page an admin has written, a page
    /// whose body an admin emptied after a previous run (the marker stops a second run), and a soft-deleted page are
    /// all left alone. The title, slug and SEO fields are never touched — a title is the client's label.
    /// </para>
    /// <para>
    /// <b>An empty page counts as untouched whatever its publish flag.</b> A shell that was never edited is empty and
    /// unpublished, and a page an admin published empty (About on UAT) is empty too; the seeder cannot tell either
    /// from one an admin deliberately unpublished while it was empty, so they are all filled and published on the
    /// first offer. After that the marker holds: an admin who unpublishes a page, or empties it, keeps it that way
    /// through every later deployment.
    /// </para>
    /// </summary>
    private async Task<int> SeedPolicyPagesAsync(CancellationToken cancellationToken)
    {
        var (marker, recorded) = await SeedVersionMarker.ReadAsync(
            dbContext, SettingKeys.Seed.PolicyPagesVersion, cancellationToken);

        if (recorded >= PolicyPageContent.SeedVersion)
        {
            return 0;
        }

        var keys = PolicyPageContent.Bodies.Keys
            .Where(key => PolicyPageContent.IntroducedIn[key] > recorded)
            .ToList();
        var pages = await dbContext.ContentPages
            .Where(page => page.SystemKey != null && keys.Contains(page.SystemKey))
            .ToListAsync(cancellationToken);

        var utcNow = clock.UtcNow.UtcDateTime;
        var filled = 0;

        foreach (var page in pages)
        {
            if (!IsUntouched(page))
            {
                continue;
            }

            page.BodyHtml = PolicyPageContent.Bodies[page.SystemKey!];
            page.SetPublished(true, utcNow);
            filled++;
        }

        SeedVersionMarker.Record(
            dbContext,
            marker,
            SettingKeys.Seed.PolicyPagesVersion,
            PolicyPageContent.SeedVersion,
            "The highest policy page copy version (Shipping, Returns, About, Privacy and Terms) applied to this "
            + "database (#33, #252). Written by the seeder; not editable.");

        await dbContext.SaveChangesAsync(cancellationToken);

        return filled;
    }

    /// <summary>Whether a policy page still holds no words of anybody's but an earlier seed's.</summary>
    internal static bool IsUntouched(ContentPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (string.IsNullOrWhiteSpace(page.BodyHtml))
        {
            return true;
        }

        return PolicyPageContent.PreviousSeeds.TryGetValue(page.SystemKey ?? string.Empty, out var previous)
            && previous.Contains(page.BodyHtml, StringComparer.Ordinal);
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

    /// <summary>
    /// Version 2 (#63): the words and links of the artwork blocks, as switched-off rows waiting for a
    /// picture — see <see cref="HomeContentData.ArtworkItems"/>.
    /// <para>
    /// <b>Offered once, by version, which is the #152 rule.</b> Unlike the tiles and benefits above, these
    /// rows are content a client is expected to delete (a trending tile they do not want, a social network
    /// they do not use), and "insert what is missing" would put a deleted one back on every deployment. So
    /// the step runs only while <c>Seed.HomeContentVersion</c> is below <see cref="HomeContentData.SeedVersion"/>,
    /// and records the new version in the same save: a row introduced in a version above the one a
    /// database records cannot have been removed by its owner, because it did not exist for them to remove.
    /// </para>
    /// <para>
    /// <b>Never over an existing row.</b> A slot that already holds an item — which is what the client-asset
    /// importer left on every database it ran against before this change, words included — is left alone,
    /// with one exception that is not an admin's edit: a field that still holds exactly the words the
    /// importer itself retracted for HOME-06 (<see cref="HomeContentData.Retractions"/>) takes the seeded
    /// words instead, so the linen-and-denim photograph stops being labelled "Activewear". Field by field,
    /// and only on an exact match, so "Activewear" typed into a button label is somebody's choice and stays.
    /// </para>
    /// </summary>
    private async Task<int> SeedArtworkItemsAsync(CancellationToken cancellationToken)
    {
        var marker = await dbContext.Settings
            .FirstOrDefaultAsync(setting => setting.Key == SettingKeys.Seed.HomeContentVersion, cancellationToken);

        var recorded = marker is not null
            && int.TryParse(marker.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 1;

        if (recorded >= HomeContentData.SeedVersion)
        {
            return 0;
        }

        var types = HomeContentData.ArtworkItems.Select(seed => seed.SectionType).Distinct().ToList();

        var sections = await dbContext.HomeSections
            .Include(section => section.Items)
            .Where(section => types.Contains(section.SectionType))
            .ToListAsync(cancellationToken);

        var added = 0;
        foreach (var seed in HomeContentData.ArtworkItems)
        {
            var section = sections.FirstOrDefault(candidate => candidate.SectionType == seed.SectionType);
            if (section is null)
            {
                continue;
            }

            var existing = section.Items.FirstOrDefault(item => item.SortOrder == seed.SortOrder);
            if (existing is not null)
            {
                Retract(existing, seed);
                continue;
            }

            dbContext.HomeSectionItems.Add(new HomeSectionItem
            {
                HomeSectionId = section.Id,
                Title = seed.Title,
                Subtitle = seed.Subtitle,
                ButtonLabel = seed.ButtonLabel,
                LinkUrl = seed.LinkUrl,
                SortOrder = seed.SortOrder,

                // Waiting for its picture. The importer, or a staff member in ADM-17, switches it on with one.
                IsEnabled = false,
            });
            added++;
        }

        if (marker is null)
        {
            dbContext.Settings.Add(new Setting
            {
                Key = SettingKeys.Seed.HomeContentVersion,
                GroupKey = SettingKeys.Seed.Group,
                ValueType = SettingValueType.Int,
                Value = HomeContentData.SeedVersion.ToString(CultureInfo.InvariantCulture),

                // As Seed.Version: no screen lists this key, and CK_Settings_RequiredPermission needs a value.
                RequiredPermission = Permissions.Settings.Security,
                Description =
                    "The highest homepage content seed version applied to this database (#63). Written by the "
                    + "seeder; not editable.",
            });
        }
        else
        {
            marker.Value = HomeContentData.SeedVersion.ToString(CultureInfo.InvariantCulture);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return added;
    }

    /// <summary>
    /// #215: one mega-menu card per department, <b>switched off</b>, in exactly the words the storefront used to
    /// hard-code — "New in {department}", the department's description, "Shop now", <c>/new-arrivals</c> — and
    /// the department's own picture when it has a usable one. Switched on, it reproduces today's menu; left off,
    /// the storefront keeps drawing today's card from the department, so a fresh database looks as it did.
    /// <para>
    /// <b>Offered once, by its own marker</b> (<c>Seed.MegaMenuCardsVersion</c>), the #152 rule: a client who
    /// deletes a card has it stay deleted. Never over an existing card — a department that already holds one,
    /// on or off, is left alone. The cards, their usage rows and the marker are one transaction.
    /// </para>
    /// </summary>
    private async Task<int> SeedMegaMenuCardsAsync(CancellationToken cancellationToken)
    {
        var marker = await dbContext.Settings
            .FirstOrDefaultAsync(setting => setting.Key == SettingKeys.Seed.MegaMenuCardsVersion, cancellationToken);

        var recorded = marker is not null
            && int.TryParse(marker.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

        if (recorded >= HomeContentData.MegaMenuCardSeedVersion)
        {
            return 0;
        }

        var departments = await dbContext.Departments
            .AsNoTracking()
            .OrderBy(department => department.SortOrder)
            .ThenBy(department => department.Id)
            .Select(department => new
            {
                department.Id,
                department.Name,
                department.Description,
                department.HeroImageAssetId,
                HeroUsable = department.HeroImageAsset != null
                    && department.HeroImageAsset.MediaType == MediaAssetType.Image
                    && department.HeroImageAsset.Status == MediaAssetStatus.Ready
                    && department.HeroImageAsset.Visibility == MediaVisibility.Public
                    && department.HeroImageAsset.AltText != null
                    && department.HeroImageAsset.AltText != string.Empty,
            })
            .ToListAsync(cancellationToken);

        var holding = (await dbContext.MegaMenuPromoCards
                .Select(card => card.DepartmentId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var added = new List<MegaMenuPromoCard>();
            foreach (var department in departments.Where(department => !holding.Contains(department.Id)))
            {
                var card = HomeContentData.MegaMenuCardFor(department.Name, department.Description);
                card.DepartmentId = department.Id;
                card.ImageAssetId = department.HeroUsable ? department.HeroImageAssetId : null;
                dbContext.MegaMenuPromoCards.Add(card);
                added.Add(card);
            }

            var value = HomeContentData.MegaMenuCardSeedVersion.ToString(CultureInfo.InvariantCulture);
            if (marker is null)
            {
                dbContext.Settings.Add(new Setting
                {
                    Key = SettingKeys.Seed.MegaMenuCardsVersion,
                    GroupKey = SettingKeys.Seed.Group,
                    ValueType = SettingValueType.Int,
                    Value = value,
                    RequiredPermission = Permissions.Settings.Security,
                    Description =
                        "The highest mega-menu card seed version applied to this database (#215). Written by the "
                        + "seeder; not editable.",
                });
            }
            else
            {
                marker.Value = value;
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            // Doc 08 §3.3: the card's picture is a use of it, so the media library refuses to delete it.
            var utcNow = clock.UtcNow.UtcDateTime;
            foreach (var card in added.Where(card => card.ImageAssetId is not null))
            {
                dbContext.MediaUsages.Add(new MediaUsage
                {
                    MediaAssetId = card.ImageAssetId!.Value,
                    OwnerType = MediaUsageOwnerTypes.MegaMenuPromoCard,
                    OwnerId = card.Id.ToString(CultureInfo.InvariantCulture),
                    UsageRole = MediaUsageRoles.Image,
                    CreatedAtUtc = utcNow,
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return added.Count;
        });
    }

    /// <summary>Puts the seeded words back over words the importer itself retracted. See the caller.</summary>
    private static void Retract(HomeSectionItem item, HomeContentData.ArtworkItemSeed seed)
    {
        foreach (var retraction in HomeContentData.Retractions.Where(
            candidate => candidate.SectionType == seed.SectionType && candidate.SortOrder == seed.SortOrder))
        {
            switch (retraction.Field)
            {
                case nameof(HomeSectionItem.Title) when item.Title == retraction.Value:
                    item.Title = seed.Title;
                    break;
                case nameof(HomeSectionItem.Subtitle) when item.Subtitle == retraction.Value:
                    item.Subtitle = seed.Subtitle;
                    break;
                case nameof(HomeSectionItem.LinkUrl) when item.LinkUrl == retraction.Value:
                    item.LinkUrl = seed.LinkUrl;
                    break;
                case nameof(HomeSectionItem.ButtonLabel) when item.ButtonLabel == retraction.Value:
                    item.ButtonLabel = seed.ButtonLabel;
                    break;
                default:
                    break;
            }
        }
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

    /// <summary><c>content.MegaMenuPromoCards</c> offered, switched off, one per department (#215).</summary>
    public int MegaMenuCards { get; set; }

    /// <summary>
    /// <c>content.ContentPages</c> shells created because the system key had none (GLB-10). Bodies are
    /// always empty and the pages are always unpublished.
    /// </summary>
    public int ContentPages { get; set; }

    /// <summary>Shipping and Returns pages given their seeded copy and published (issue #33).</summary>
    public int PolicyPages { get; set; }
}
