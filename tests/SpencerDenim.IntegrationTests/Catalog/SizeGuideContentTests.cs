using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Catalog;

/// <summary>
/// PDP-04, issue #32: the measurements the product page shows arrive on the product payload.
/// <para>
/// The shape is the storefront's, not this task's — <c>SizeGuideContentDto</c> in
/// <c>core/catalog/catalog.model.ts</c> wrote it down before the API served it, and
/// <c>hasSizeGuideContent</c> is the single condition that unhides the trigger. So the assertions here
/// are about that contract: content is present when there is something to read, and <b>absent</b>, not
/// empty, when there is not.
/// </para>
/// <para>
/// Several tests change the seeded catalogue and restore it in a <c>finally</c>; the assembly runs its
/// test classes sequentially, so nothing else observes the half-applied state.
/// </para>
/// </summary>
public sealed class SizeGuideContentTests(SqlServerDatabaseFixture sqlServer) : CatalogTestBase(sqlServer)
{
    private const string WomensGuide = "Women's apparel";
    private const string MensGuide = "Men's apparel";
    private const string Shirt = "relaxed-linen-blend-shirt";

    /// <summary>
    /// The whole of issue #32 in one assertion: the real seeded women's table, cell for cell, on a real
    /// product. The numbers are written out rather than read from the seed constant, because a test that
    /// reads its expectation from the thing under test cannot fail.
    /// </summary>
    [Fact]
    public async Task The_product_page_carries_the_seeded_womens_measurement_table()
    {
        var api = CreateApi();

        var product = await api.GetProductAsync(Shirt, Token);

        var guide = Assert.IsType<SizeGuideRefDto>(product.SizeGuide);
        Assert.Equal(WomensGuide, guide.Name);

        var content = Assert.IsType<SizeGuideContentDto>(guide.Content);
        // The seeder wraps both in a paragraph, so what arrives is markup and not a sentence — which is
        // the point: the storefront renders these two fields through `[innerHTML]`, exactly as it already
        // renders `Products.Description`.
        Assert.Equal(
            "<p>Measurements are in centimetres and describe the body, not the garment.</p>",
            content.IntroHtml);
        Assert.Equal(
            "<p>Measure over light clothing, keeping the tape level and snug but not tight.</p>",
            content.MeasuringTipsHtml);

        // No seeded guide has a diagram until an admin attaches one (ADM-11), which is an absence and
        // not a broken link.
        Assert.Null(content.ImageUrl);

        var table = Assert.IsType<SizeGuideTableDto>(content.Table);
        Assert.Equal(["Size", "Bust", "Waist", "Hip"], table.Columns);
        Assert.Equal(
            [
                ["XS", "78-82", "60-64", "86-90"],
                ["S", "83-87", "65-69", "91-95"],
                ["M", "88-93", "70-75", "96-101"],
                ["L", "94-99", "76-81", "102-107"],
                ["XL", "100-106", "82-88", "108-114"],
                ["XXL", "107-113", "89-95", "115-121"],
            ],
            table.Rows);
    }

    /// <summary>All three seeded guides, one product per department, so no department is left unproven.</summary>
    [Theory]
    [InlineData(Shirt, WomensGuide, "Bust")]
    [InlineData("washed-linen-camp-shirt", MensGuide, "Chest")]
    [InlineData("girls-printed-tee", "Kids and baby", "Height")]
    public async Task Each_department_gets_its_own_guide_with_measurements(
        string slug,
        string guideName,
        string expectedColumn)
    {
        var api = CreateApi();

        var product = await api.GetProductAsync(slug, Token);

        Assert.Equal(guideName, product.SizeGuide?.Name);
        var table = product.SizeGuide?.Content?.Table;
        Assert.NotNull(table);
        Assert.Contains(expectedColumn, table.Columns);
        Assert.NotEmpty(table.Rows);
        Assert.All(table.Rows, row => Assert.Equal(table.Columns.Count, row.Count));
    }

    /// <summary>
    /// <c>women/footwear</c>, <c>women/accessories</c> and <c>men/accessories</c> are seeded with no
    /// guide, which is the right answer for a tote: there is no size to guide.
    /// </summary>
    [Theory]
    [InlineData("everyday-canvas-tote")]
    [InlineData("woven-slide-sandals")]
    [InlineData("leather-look-belt")]
    public async Task A_product_whose_category_has_no_guide_has_no_guide(string slug)
    {
        var api = CreateApi();

        var product = await api.GetProductAsync(slug, Token);

        Assert.Null(product.SizeGuide);
    }

    /// <summary>
    /// A guide an admin has created and not filled in. <c>TableJson</c>'s own default is <c>{}</c>, so
    /// this is the state such a guide is really in, and the storefront must get a named guide with
    /// <b>no</b> content — not four empty fields, which <c>hasSizeGuideContent</c> would have to unpick
    /// before deciding the trigger is not worth drawing.
    /// </summary>
    [Fact]
    public async Task A_guide_with_nothing_in_it_arrives_named_and_without_content()
    {
        var api = CreateApi();
        var original = await ReadGuideAsync(WomensGuide);

        try
        {
            // Whitespace rather than NULL for the intro: an admin clearing a rich-text box leaves blank
            // markup behind far more often than it leaves a NULL.
            await WriteGuideAsync(WomensGuide, intro: "   ", tableJson: "{}", tips: null);

            var product = await api.GetProductAsync(Shirt, Token);

            Assert.Equal(WomensGuide, product.SizeGuide?.Name);
            Assert.Null(product.SizeGuide?.Content);
        }
        finally
        {
            await RestoreGuideAsync(original);
        }
    }

    /// <summary>
    /// <c>ISJSON</c> allows any shape, so the table can be JSON and still be unreadable. It costs the
    /// table and nothing else: the intro and the tips are still worth opening the modal for, and the
    /// product page is the page the shopper actually came for.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"columns\":[\"Size\"],\"rows\":\"S\"}")]
    [InlineData("{\"rows\":[[\"S\",\"88-93\"]]}")]
    [InlineData("{\"columns\":[],\"rows\":[[\"S\"]]}")]
    public async Task A_table_that_cannot_be_read_costs_the_table_and_not_the_page(string tableJson)
    {
        var api = CreateApi();
        var original = await ReadGuideAsync(WomensGuide);

        try
        {
            await WriteGuideAsync(
                WomensGuide,
                intro: "<p>Measurements are in centimetres.</p>",
                tableJson: tableJson,
                tips: "<p>Keep the tape level.</p>");

            var product = await api.GetProductAsync(Shirt, Token);

            var content = product.SizeGuide?.Content;
            Assert.NotNull(content);
            Assert.Null(content.Table);
            Assert.Equal("<p>Measurements are in centimetres.</p>", content.IntroHtml);
            Assert.Equal("<p>Keep the tape level.</p>", content.MeasuringTipsHtml);
        }
        finally
        {
            await RestoreGuideAsync(original);
        }
    }

    /// <summary>
    /// An unpaired surrogate, written <b>through a live <c>ISJSON</c></b> — which is the finding. The
    /// constraint accepts it, so this is not a corrupted-backup scenario: it is one character an admin
    /// can paste out of a word processor, and it used to be HTTP 500 on every product page inheriting
    /// the guide while the listings carried on answering 200.
    /// <para>
    /// Both forms, because they break in different places: the raw <c>NCHAR(0xD800)</c> fails
    /// transcoding inside <c>JsonDocument.Parse</c>, and the <c>\uD800</c> escape parses and then fails
    /// when the string is unescaped, further down. Each one is a different exception type from a
    /// different call.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unpaired_surrogate_in_the_table_is_not_a_500_on_every_product_page()
    {
        var api = CreateApi();
        var original = await ReadGuideAsync(WomensGuide);
        var guideId = original.Id;

        try
        {
            // `where` is built separately on purpose: the C# compiler folds "literal" + $"interpolated"
            // into a single interpolated string, which would make every `{` in the JSON below a format
            // placeholder.
            var where = " WHERE Id = " + guideId.ToString(CultureInfo.InvariantCulture) + ";";

            // The UPDATE itself is half the finding: it runs with CK_SizeGuides_TableJson in force, so
            // ISJSON is no guard here. It would throw if the constraint rejected the value.
            await ExecuteAsync(
                "UPDATE catalog.SizeGuides SET IntroHtml = N'<p>Still useful.</p>', MeasuringTipsHtml = NULL, "
                + "TableJson = N'{\"columns\":[\"Si' + NCHAR(0xD800) + N'ze\"],\"rows\":[[\"S\"]]}'"
                + where);

            var product = await api.GetProductAsync(Shirt, Token);

            Assert.Null(product.SizeGuide?.Content?.Table);
            Assert.Equal("<p>Still useful.</p>", product.SizeGuide?.Content?.IntroHtml);

            // The escaped form, in a cell: it costs that cell and nothing else.
            await ExecuteAsync(
                "UPDATE catalog.SizeGuides SET TableJson = "
                + "N'{\"columns\":[\"Size\",\"Bust\"],\"rows\":[[\"S\",\"\\uDC00\"],[\"M\",\"88-93\"]]}'"
                + where);

            var escaped = await api.GetProductAsync(Shirt, Token);
            var table = escaped.SizeGuide?.Content?.Table;

            Assert.NotNull(table);
            Assert.Equal(["Size", "Bust"], table.Columns);
            Assert.Equal(["S", ""], table.Rows[0]);
            Assert.Equal(["M", "88-93"], table.Rows[1]);
        }
        finally
        {
            await RestoreGuideAsync(original);
        }
    }

    /// <summary>
    /// Text that is not JSON at all cannot be written through <c>ISJSON</c>, so the constraint is
    /// switched off for the length of the test — which is the only way to prove the reader survives a
    /// database somebody has restored or edited around the check.
    /// </summary>
    [Fact]
    public async Task Text_that_is_not_json_at_all_still_only_costs_the_table()
    {
        var api = CreateApi();
        var original = await ReadGuideAsync(WomensGuide);

        await ExecuteAsync("ALTER TABLE catalog.SizeGuides NOCHECK CONSTRAINT CK_SizeGuides_TableJson;");
        try
        {
            await WriteGuideAsync(WomensGuide, intro: "<p>Still useful.</p>", tableJson: "not json", tips: null);

            var product = await api.GetProductAsync(Shirt, Token);

            Assert.Null(product.SizeGuide?.Content?.Table);
            Assert.Equal("<p>Still useful.</p>", product.SizeGuide?.Content?.IntroHtml);
        }
        finally
        {
            await RestoreGuideAsync(original);
            await ExecuteAsync("ALTER TABLE catalog.SizeGuides WITH CHECK CHECK CONSTRAINT CK_SizeGuides_TableJson;");
        }
    }

    /// <summary>
    /// Doc 08 §3.2: a product may override the guide it inherits from its primary category, and the id,
    /// the name and the measurements then all have to come from the override.
    /// </summary>
    [Fact]
    public async Task A_products_own_guide_overrides_the_one_its_category_gives_it()
    {
        var api = CreateApi();
        var mens = await ReadGuideAsync(MensGuide);

        await SetProductGuideAsync(Shirt, mens.Id);
        try
        {
            var product = await api.GetProductAsync(Shirt, Token);

            Assert.Equal(mens.Id, product.SizeGuide?.Id);
            Assert.Equal(MensGuide, product.SizeGuide?.Name);
            Assert.Contains("Chest", product.SizeGuide?.Content?.Table?.Columns ?? []);
        }
        finally
        {
            await SetProductGuideAsync(Shirt, null);
        }

        var restored = await api.GetProductAsync(Shirt, Token);
        Assert.Equal(WomensGuide, restored.SizeGuide?.Name);
    }

    /// <summary>
    /// The soft-delete rule the rest of the catalogue readers honour (doc 08 §1.5). It also pins the
    /// defect the old projection had: the id came from <c>Products.SizeGuideId</c>, which no query filter
    /// can touch, while the name came from the navigation — so a product whose own guide had been deleted
    /// answered with the deleted guide's id beside its <i>category's</i> guide's name, and would now have
    /// answered with a third thing's measurements.
    /// </summary>
    [Fact]
    public async Task A_deleted_override_falls_back_to_the_category_whole_rather_than_half()
    {
        var api = CreateApi();
        var mens = await ReadGuideAsync(MensGuide);

        await SetProductGuideAsync(Shirt, mens.Id);
        try
        {
            await SetGuideDeletedAsync(mens.Id, isDeleted: true);

            var product = await api.GetProductAsync(Shirt, Token);

            // The category's guide, entirely: not the deleted guide's id with somebody else's name.
            Assert.Equal(WomensGuide, product.SizeGuide?.Name);
            Assert.NotEqual(mens.Id, product.SizeGuide?.Id);
            Assert.Contains("Bust", product.SizeGuide?.Content?.Table?.Columns ?? []);
        }
        finally
        {
            await SetGuideDeletedAsync(mens.Id, isDeleted: false);
            await SetProductGuideAsync(Shirt, null);
        }
    }

    /// <summary>Deleting the only guide a product can reach leaves it with none, not with a named shell.</summary>
    [Fact]
    public async Task A_deleted_guide_leaves_the_product_with_no_guide_at_all()
    {
        var api = CreateApi();
        var women = await ReadGuideAsync(WomensGuide);

        await SetGuideDeletedAsync(women.Id, isDeleted: true);
        try
        {
            var product = await api.GetProductAsync(Shirt, Token);

            Assert.Null(product.SizeGuide);
        }
        finally
        {
            await SetGuideDeletedAsync(women.Id, isDeleted: false);
        }
    }

    /// <summary>
    /// The same <c>Status == Ready</c> rule every other media reference in this reader follows: a row
    /// still processing, or one whose processing failed, has no file behind it, so linking it would put a
    /// 404 where a measuring diagram should have been.
    /// </summary>
    [Theory]
    [InlineData(MediaAssetStatus.Ready, true)]
    [InlineData(MediaAssetStatus.Processing, false)]
    [InlineData(MediaAssetStatus.Failed, false)]
    public async Task A_diagram_is_linked_only_when_its_asset_is_ready(MediaAssetStatus status, bool expectUrl)
    {
        var api = CreateApi();
        var women = await ReadGuideAsync(WomensGuide);
        var assetId = await CreateImageAssetAsync(status);

        try
        {
            await SetGuideImageAsync(women.Id, assetId);

            var product = await api.GetProductAsync(Shirt, Token);
            var imageUrl = product.SizeGuide?.Content?.ImageUrl;

            if (expectUrl)
            {
                Assert.NotNull(imageUrl);

                // `large`, not `zoom`: the diagram is drawn inside a dialog narrower than the page.
                Assert.StartsWith("/media/public/renditions/", imageUrl, StringComparison.Ordinal);
                Assert.EndsWith("/large.webp", imageUrl, StringComparison.Ordinal);

                // The asset's alt text travels with it. A diagram is informative, so a client given
                // only a URL would have to choose between hiding it from a screen reader and
                // inventing a description of a picture it cannot see.
                Assert.Equal(
                    "How to measure the bust, waist and hip.",
                    product.SizeGuide?.Content?.ImageAltText);
            }
            else
            {
                Assert.Null(imageUrl);

                // No file, so nothing to describe either: alt text for an image that is not linked
                // would be a label with nothing under it.
                Assert.Null(product.SizeGuide?.Content?.ImageAltText);
            }
        }
        finally
        {
            await SetGuideImageAsync(women.Id, null);
            await DeleteAssetAsync(assetId);
        }
    }

    /// <summary>
    /// An asset an admin uploaded without alt text. The API says so rather than inventing one; the
    /// client then names the guide, which is honest chrome instead of a description of a picture
    /// nothing in the chain has seen. ADM-11 should require it (issue #70).
    /// </summary>
    [Fact]
    public async Task A_diagram_with_no_alt_text_arrives_with_none_rather_than_an_invented_one()
    {
        var api = CreateApi();
        var women = await ReadGuideAsync(WomensGuide);
        var assetId = await CreateImageAssetAsync(MediaAssetStatus.Ready, altText: null);

        try
        {
            await SetGuideImageAsync(women.Id, assetId);

            var product = await api.GetProductAsync(Shirt, Token);

            Assert.NotNull(product.SizeGuide?.Content?.ImageUrl);
            Assert.Null(product.SizeGuide?.Content?.ImageAltText);
        }
        finally
        {
            await SetGuideImageAsync(women.Id, null);
            await DeleteAssetAsync(assetId);
        }
    }

    /// <summary>
    /// A deleted asset is not a diagram either, and the global query filter is what says so — the foreign
    /// key still holds its id.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_diagram_leaves_the_guide_without_an_image()
    {
        var api = CreateApi();
        var women = await ReadGuideAsync(WomensGuide);
        var assetId = await CreateImageAssetAsync(MediaAssetStatus.Ready);

        try
        {
            await SetGuideImageAsync(women.Id, assetId);
            await SetAssetDeletedAsync(assetId, isDeleted: true);

            var product = await api.GetProductAsync(Shirt, Token);

            Assert.Null(product.SizeGuide?.Content?.ImageUrl);

            // The rest of the guide is untouched: a missing diagram is not a missing guide.
            Assert.NotNull(product.SizeGuide?.Content?.Table);
        }
        finally
        {
            await SetAssetDeletedAsync(assetId, isDeleted: false);
            await SetGuideImageAsync(women.Id, null);
            await DeleteAssetAsync(assetId);
        }
    }

    /// <summary>
    /// A deactivated primary category takes the whole product page away (doc 08 §7.1), so the guide
    /// cannot be reached through a product that is off the storefront. Worth pinning: embedding the
    /// content on the product payload is what makes that true for free, where a size-guide route of its
    /// own would have had to answer the question itself.
    /// </summary>
    [Fact]
    public async Task The_guide_is_unreachable_once_its_product_leaves_the_storefront()
    {
        var api = CreateApi();

        await SetCategoryActiveAsync("shirts-blouses", isActive: false);
        try
        {
            var (status, _) = await api.GetRawAsync("/api/catalog/products/" + Shirt, Token);

            Assert.Equal(HttpStatusCode.NotFound, status);
        }
        finally
        {
            await SetCategoryActiveAsync("shirts-blouses", isActive: true);
        }
    }

    /// <summary>
    /// The content costs no extra round trip: it is projected in the product query the page already
    /// makes. This is the measurable half of the argument for embedding it rather than serving it from a
    /// route of its own.
    /// </summary>
    [Fact]
    public async Task The_measurements_cost_the_product_page_no_extra_query()
    {
        var api = CreateApi();
        await api.GetProductAsync(Shirt, Token);

        CommandCounter.Reset();
        var product = await api.GetProductAsync(Shirt, Token);
        var commands = CommandCounter.Count;

        Assert.NotNull(product.SizeGuide?.Content?.Table);

        TestContext.Current.TestOutputHelper!.WriteLine(
            $"The product page used {commands} SQL commands with the size guide's measurements in it.");

        Assert.True(commands <= 12, $"The product page issued {commands} SQL commands.");
    }

    // ---------------------------------------------------------------- fixture plumbing

    private async Task<SizeGuide> ReadGuideAsync(string name)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Set<SizeGuide>().AsNoTracking().FirstAsync(guide => guide.Name == name, Token);
    }

    private async Task WriteGuideAsync(string name, string? intro, string tableJson, string? tips)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var guide = await dbContext.Set<SizeGuide>().FirstAsync(candidate => candidate.Name == name, Token);
        guide.IntroHtml = intro;
        guide.TableJson = tableJson;
        guide.MeasuringTipsHtml = tips;
        await dbContext.SaveChangesAsync(Token);
    }

    private Task RestoreGuideAsync(SizeGuide original) =>
        WriteGuideAsync(original.Name, original.IntroHtml, original.TableJson, original.MeasuringTipsHtml);

    /// <summary>
    /// <c>IsDeleted</c> has a private setter and <c>SoftDelete</c> is one-way, so the switch is flipped
    /// in SQL — <c>IgnoreQueryFilters</c> would let EF read the row back but still not write the column.
    /// </summary>
    private Task SetGuideDeletedAsync(int guideId, bool isDeleted) =>
        ExecuteAsync(
            FormattableString.Invariant(
                $"UPDATE catalog.SizeGuides SET IsDeleted = {(isDeleted ? 1 : 0)}, DeletedAtUtc = ")
            + (isDeleted ? "SYSUTCDATETIME()" : "NULL")
            + FormattableString.Invariant($" WHERE Id = {guideId};"));

    private Task SetGuideImageAsync(int guideId, int? assetId) =>
        ExecuteAsync(
            "UPDATE catalog.SizeGuides SET ImageAssetId = "
            + (assetId?.ToString(CultureInfo.InvariantCulture) ?? "NULL")
            + FormattableString.Invariant($" WHERE Id = {guideId};"));

    private async Task SetProductGuideAsync(string slug, int? guideId)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var product = await dbContext.Products.FirstAsync(candidate => candidate.Slug == slug, Token);
        product.SizeGuideId = guideId;
        await dbContext.SaveChangesAsync(Token);
    }

    /// <summary>
    /// A metadata row only. The reader projects columns and never opens a file, so the rendition URL it
    /// builds comes provably from <c>PublicId</c> and <c>RenditionSetVersion</c> rather than from
    /// anything on disk.
    /// </summary>
    private async Task<int> CreateImageAssetAsync(
        MediaAssetStatus status,
        string? altText = "How to measure the bust, waist and hip.")
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var asset = new MediaAsset
        {
            MediaType = MediaAssetType.Image,
            Visibility = MediaVisibility.Public,
            Status = status,
            OriginalFileName = "size-guide-diagram.jpg",
            StorageKey = $"public/originals/{Guid.NewGuid():n}.jpg",
            ContentType = "image/jpeg",
            SizeBytes = 2048,
            Width = 1200,
            Height = 900,
            RenditionSetVersion = 1,

            // Unique index on Checksum (20260925072043_MediaChecksumUniqueIndex), so it has to be new.
            Checksum = [.. Guid.NewGuid().ToByteArray(), .. Guid.NewGuid().ToByteArray()],
            AltText = altText,
        };

        dbContext.Set<MediaAsset>().Add(asset);
        await dbContext.SaveChangesAsync(Token);
        return asset.Id;
    }

    private Task SetAssetDeletedAsync(int assetId, bool isDeleted) =>
        ExecuteAsync(
            FormattableString.Invariant(
                $"UPDATE media.MediaAssets SET IsDeleted = {(isDeleted ? 1 : 0)}, DeletedAtUtc = ")
            + (isDeleted ? "SYSUTCDATETIME()" : "NULL")
            + FormattableString.Invariant($" WHERE Id = {assetId};"));

    private Task DeleteAssetAsync(int assetId) =>
        ExecuteAsync(FormattableString.Invariant($"DELETE FROM media.MediaAssets WHERE Id = {assetId};"));

    private async Task SetCategoryActiveAsync(string slug, bool isActive)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var category = await dbContext.Categories.FirstAsync(candidate => candidate.Slug == slug, Token);
        category.IsActive = isActive;
        await dbContext.SaveChangesAsync(Token);
    }

    /// <summary>
    /// One statement, on a plain <see cref="System.Data.Common.DbCommand"/> rather than through
    /// <c>ExecuteSqlRawAsync</c>.
    /// <para>
    /// <c>ExecuteSqlRaw</c> runs the SQL through positional-placeholder substitution, so every
    /// <c>{</c> in a statement that writes JSON is read as a format placeholder: the surrogate test
    /// failed with <i>"Failure to parse near offset 108. Expected an ASCII digit"</i> before the
    /// database was ever reached. Doubling the braces would have worked and would have made every
    /// statement here a transcription puzzle.
    /// </para>
    /// </summary>
    private async Task ExecuteAsync(string sql)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = dbContext.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(Token);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }
}
