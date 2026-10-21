using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Media;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// #215's seed step and table, each test on a throwaway database (<see cref="FreshDeployment"/>), because what is
/// under test is the marker row, rows going missing, and rows the database must refuse.
/// <para>
/// The seed step offers one switched-off card per department in the words the storefront used to hard-code, with
/// the department's own picture when it is usable; it is idempotent, never overwrites, never brings back a card a
/// client removed, and works on a database that existed before the table did.
/// </para>
/// </summary>
public sealed class MegaMenuCardSeedAndSchemaTests(SqlServerDatabaseFixture sqlServer)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fresh_database_is_offered_todays_card_per_department_switched_off_once()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        int women, men, picture;
        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            picture = await AddImageAsync(db, "A woman in a linen dress");
            women = await AddDepartmentAsync(db, "Women", "women", "Everyday essentials.", picture, sortOrder: 1);
            men = await AddDepartmentAsync(db, "Men", "men", null, heroImageAssetId: null, sortOrder: 2);

            var counts = await Seeder(db).SeedAsync(Token);
            Assert.Equal(2, counts.MegaMenuCards);
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cards = await db.MegaMenuPromoCards.AsNoTracking().OrderBy(card => card.DepartmentId).ToListAsync(Token);

            Assert.Equal(2, cards.Count);
            Assert.All(cards, card =>
            {
                Assert.False(card.IsActive);
                Assert.Equal("Shop now", card.ButtonLabel);
                Assert.Equal("/new-arrivals", card.LinkUrl);
                Assert.Equal(0, card.SortOrder);
                Assert.Null(card.StartsAtUtc);
                Assert.Null(card.EndsAtUtc);
            });

            var womenCard = cards.Single(card => card.DepartmentId == women);
            Assert.Equal("New in Women", womenCard.Title);
            Assert.Equal("Everyday essentials.", womenCard.Subtitle);
            Assert.Equal(picture, womenCard.ImageAssetId);

            var menCard = cards.Single(card => card.DepartmentId == men);
            Assert.Equal("New in Men", menCard.Title);
            Assert.Null(menCard.Subtitle);
            Assert.Null(menCard.ImageAssetId);

            // The picture is held: one usage row, for the card that has it.
            var usage = Assert.Single(await db.MediaUsages.AsNoTracking()
                .Where(row => row.OwnerType == MediaUsageOwnerTypes.MegaMenuPromoCard)
                .ToListAsync(Token));
            Assert.Equal(womenCard.Id.ToString(CultureInfo.InvariantCulture), usage.OwnerId);
            Assert.Equal(picture, usage.MediaAssetId);
            Assert.Equal(MediaUsageRoles.Image, usage.UsageRole);

            Assert.Equal("1", await MarkerAsync(db));
        }

        // Again: nothing. Then the client removes a card, and the next deployment leaves it removed.
        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, (await Seeder(db).SeedAsync(Token)).MegaMenuCards);

            await db.MegaMenuPromoCards.Where(card => card.DepartmentId == men).ExecuteDeleteAsync(Token);

            Assert.Equal(0, (await Seeder(db).SeedAsync(Token)).MegaMenuCards);
            Assert.False(await db.MegaMenuPromoCards.AnyAsync(card => card.DepartmentId == men, Token));
            Assert.Equal(1, await db.MegaMenuPromoCards.CountAsync(Token));
        }
    }

    /// <summary>
    /// A database from before #215 (no marker) where a card already exists for one department: that department is
    /// left exactly as it is — words, switch and all — and only the others are offered a card.
    /// </summary>
    [Fact]
    public async Task An_existing_database_keeps_its_cards_and_is_offered_the_rest()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        int women, kids;
        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var picture = await AddImageAsync(db, "Kids in the garden");
            women = await AddDepartmentAsync(db, "Women", "women", null, null, 1);
            kids = await AddDepartmentAsync(db, "Kids", "kids", "Soft fabrics.", null, 3);

            db.MegaMenuPromoCards.Add(new MegaMenuPromoCard
            {
                DepartmentId = kids,
                Title = "Back to school",
                ButtonLabel = "Shop the edit",
                LinkUrl = "/kids/uniforms",
                ImageAssetId = picture,
                IsActive = true,
                SortOrder = 3,
            });
            await db.SaveChangesAsync(Token);

            Assert.Null(await MarkerAsync(db));
        }

        await using (var scope = deployment.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, (await Seeder(db).SeedAsync(Token)).MegaMenuCards);

            var kidsCard = await db.MegaMenuPromoCards.AsNoTracking().SingleAsync(card => card.DepartmentId == kids, Token);
            Assert.Equal("Back to school", kidsCard.Title);
            Assert.Equal("Shop the edit", kidsCard.ButtonLabel);
            Assert.True(kidsCard.IsActive);

            var womenCard = await db.MegaMenuPromoCards.AsNoTracking().SingleAsync(card => card.DepartmentId == women, Token);
            Assert.Equal("New in Women", womenCard.Title);
            Assert.False(womenCard.IsActive);

            Assert.Equal("1", await MarkerAsync(db));
        }
    }

    /// <summary>A recorded marker means the offer has been made: an emptied table stays empty.</summary>
    [Fact]
    public async Task Once_recorded_the_offer_is_never_made_again_even_to_an_empty_table()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        await using var scope = deployment.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await AddDepartmentAsync(db, "Women", "women", null, null, 1);

        Assert.Equal(1, (await Seeder(db).SeedAsync(Token)).MegaMenuCards);
        await db.MegaMenuPromoCards.ExecuteDeleteAsync(Token);

        Assert.Equal(0, (await Seeder(db).SeedAsync(Token)).MegaMenuCards);
        Assert.False(await db.MegaMenuPromoCards.AnyAsync(Token));
    }

    /// <summary>
    /// A department picture the shop could not show on a card — no alt text, still processing, private — is not
    /// put on one: the card is offered without a picture, to be given one in the editor.
    /// </summary>
    [Theory]
    [InlineData("no-alt")]
    [InlineData("processing")]
    [InlineData("private")]
    public async Task A_department_picture_the_card_could_not_show_is_not_used(string flaw)
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        await using var scope = deployment.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var picture = await AddImageAsync(
            db,
            flaw == "no-alt" ? null : "Alt",
            flaw == "processing" ? MediaAssetStatus.Processing : MediaAssetStatus.Ready,
            flaw == "private" ? MediaVisibility.Private : MediaVisibility.Public);
        await AddDepartmentAsync(db, "Women", "women", null, picture, 1);

        await Seeder(db).SeedAsync(Token);

        var card = await db.MegaMenuPromoCards.AsNoTracking().SingleAsync(Token);
        Assert.Null(card.ImageAssetId);
        Assert.False(await db.MediaUsages.AnyAsync(row => row.OwnerType == MediaUsageOwnerTypes.MegaMenuPromoCard, Token));
    }

    // ================================================================== the table's own rules

    [Theory]
    [InlineData("active-without-image", "CK_MegaMenuPromoCards_ActiveImage")]
    [InlineData("window-never-opens", "CK_MegaMenuPromoCards_Schedule")]
    [InlineData("blank-title", "CK_MegaMenuPromoCards_Title")]
    [InlineData("blank-button", "CK_MegaMenuPromoCards_ButtonLabel")]
    [InlineData("blank-link", "CK_MegaMenuPromoCards_LinkUrl")]
    public async Task The_database_refuses_a_card_that_breaks_its_rules(string shape, string constraint)
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        await using var scope = deployment.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var department = await AddDepartmentAsync(db, "Women", "women", null, null, 1);

        var (isActive, title, button, link, starts, ends) = shape switch
        {
            "active-without-image" => (1, "T", "B", "/x", "NULL", "NULL"),
            "window-never-opens" => (0, "T", "B", "/x", "'2030-01-01T10:00:00'", "'2030-01-01T10:00:00'"),
            "blank-title" => (0, "", "B", "/x", "NULL", "NULL"),
            "blank-button" => (0, "T", "", "/x", "NULL", "NULL"),
            _ => (0, "T", "B", "", "NULL", "NULL"),
        };

        var sql = string.Create(
            CultureInfo.InvariantCulture,
            $"""
            INSERT INTO content.MegaMenuPromoCards (DepartmentId, Title, ButtonLabel, LinkUrl, SortOrder, IsActive, StartsAtUtc, EndsAtUtc)
            VALUES ({department}, N'{title}', N'{button}', N'{link}', 0, {isActive}, {starts}, {ends});
            """);

        var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql, Token));
        Assert.Contains(constraint, error.Message, StringComparison.Ordinal);
    }

    /// <summary>A valid row lands, so the constraints above are the reason the others did not; and the named indexes exist.</summary>
    [Fact]
    public async Task A_valid_card_lands_and_the_named_indexes_exist()
    {
        sqlServer.SkipIfUnavailable();
        await using var deployment = await FreshDeployment.CreateAsync(sqlServer.ConnectionString, Token);

        await using var scope = deployment.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var department = await AddDepartmentAsync(db, "Women", "women", null, null, 1);

        await db.Database.ExecuteSqlRawAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT INTO content.MegaMenuPromoCards (DepartmentId, Title, ButtonLabel, LinkUrl, SortOrder, IsActive) VALUES ({department}, N'T', N'B', N'/x', 0, 0);"),
            Token);

        var indexes = await db.Database
            .SqlQuery<string>($"""
                SELECT i.name AS [Value]
                FROM sys.indexes i
                WHERE i.object_id = OBJECT_ID(N'content.MegaMenuPromoCards') AND i.name IS NOT NULL
                """)
            .ToListAsync(Token);

        Assert.Contains("PK_MegaMenuPromoCards", indexes);
        Assert.Contains("IX_MegaMenuPromoCards_Department_SortOrder", indexes);
        Assert.Contains("IX_MegaMenuPromoCards_ImageAssetId", indexes);
    }

    // ================================================================== helpers

    private static HomeContentSeeder Seeder(AppDbContext db) => new(db, new SystemTestClock());

    private static Task<string?> MarkerAsync(AppDbContext db) =>
        db.Settings.AsNoTracking()
            .Where(setting => setting.Key == SettingKeys.Seed.MegaMenuCardsVersion)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(Token);

    private static async Task<int> AddDepartmentAsync(
        AppDbContext db,
        string name,
        string slug,
        string? description,
        int? heroImageAssetId,
        short sortOrder)
    {
        var department = new Department
        {
            Name = name,
            Slug = slug,
            Description = description,
            HeroImageAssetId = heroImageAssetId,
            SortOrder = sortOrder,
            IsActive = true,
        };

        db.Departments.Add(department);
        await db.SaveChangesAsync(Token);
        return department.Id;
    }

    private static async Task<int> AddImageAsync(
        AppDbContext db,
        string? altText,
        MediaAssetStatus status = MediaAssetStatus.Ready,
        MediaVisibility visibility = MediaVisibility.Public)
    {
        var asset = new MediaAsset
        {
            MediaType = MediaAssetType.Image,
            Visibility = visibility,
            Status = status,
            OriginalFileName = "department.jpg",
            StorageKey = $"public/originals/{Guid.NewGuid():n}.jpg",
            ContentType = "image/jpeg",
            SizeBytes = 2048,
            Width = 1200,
            Height = 900,
            RenditionSetVersion = 1,
            Checksum = [.. Guid.NewGuid().ToByteArray(), .. Guid.NewGuid().ToByteArray()],
            AltText = altText,
        };

        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(Token);
        return asset.Id;
    }

    private sealed class SystemTestClock : IClock
    {
        public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();
    }
}
