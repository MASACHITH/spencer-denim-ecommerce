using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The ADM-10 admin product endpoints (W1-03), end to end against a real SQL Server (doc 06 §4).
/// <para>
/// <b>Why these cannot be unit tests.</b> Four of the things this task has to get right are decided by the
/// database and by nothing else: EF's insert ordering for a variant whose colourway is created in the same
/// request (the foreign key is the composite <c>(ProductColourId, ProductId)</c> against an alternate key,
/// doc 08 §3.2), <c>CK_Products_Slug</c> refusing a direct SQL write, <c>rowversion</c> losing a real race,
/// and a unique index firing after the handler's own check passed.
/// </para>
/// <para>
/// Everything a test creates it removes again in a <c>finally</c>: the fixture's database is shared by the
/// whole assembly and a dozen catalogue tests assert against the seeded catalogue, so a stray product
/// would fail somebody else's test with no clue where it came from.
/// </para>
/// </summary>
public sealed class AdminProductEndpointTests(SqlServerDatabaseFixture sqlServer) : IAsyncDisposable
{
    private const string ProductsUrl = "/api/admin/products";

    private static readonly string[] FullProductRights =
    [
        Permissions.Products.View,
        Permissions.Products.Create,
        Permissions.Products.Edit,
        Permissions.Products.Delete,
    ];

    private readonly Dictionary<string, ApiFactory> _factories = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- authorization --------------------------------------------------------------------------

    [Fact]
    public async Task Every_route_refuses_an_unauthenticated_caller()
    {
        var factory = await HostAsync();
        using var client = AdminCatalogApiClient.Anonymous(factory);

        foreach (var (method, path) in Routes())
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            using var response = await client.SendAsync(request, Token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Every_route_refuses_a_signed_in_user_with_no_product_permission()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-nothing", [], Token);

        foreach (var (method, path) in Routes())
        {
            var status = method == HttpMethod.Get
                ? (await client.GetAsync(path, Token)).Status
                : method == HttpMethod.Delete
                    ? (await client.DeleteAsync(path, null, Token)).Status
                    : method == HttpMethod.Post
                        ? (await client.PostAsync(path, new { }, Token)).Status
                        : (await client.PutAsync(path, new { }, Token)).Status;

            Assert.Equal(HttpStatusCode.Forbidden, status);
        }
    }

    /// <summary>
    /// Doc 11 P1's own table splits these four, so a viewer who could publish a product would make the
    /// Create, Edit and Delete permissions decorative.
    /// </summary>
    [Fact]
    public async Task View_create_edit_and_delete_are_four_separate_permissions()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);

        var viewer = await AdminCatalogApiClient.SignInAsync(
            factory, "w103-viewer", [Permissions.Products.View], Token);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(ProductsUrl, Token)).Status);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await viewer.PostAsync(ProductsUrl, Body(seed, "w103-perm"), Token)).Status);

        var creator = await AdminCatalogApiClient.SignInAsync(
            factory, "w103-creator", [Permissions.Products.View, Permissions.Products.Create], Token);

        var (status, body) = await creator.PostAsync(ProductsUrl, Body(seed, "w103-perm"), Token);
        Assert.Equal(HttpStatusCode.Created, status);
        var productId = body.GetProperty("id").GetInt32();

        try
        {
            var rowVersion = await RowVersionAsync(factory, productId);

            // Create does not imply Edit, and neither implies Delete.
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await creator.PutAsync(
                    $"{ProductsUrl}/{productId}/published", new { isPublished = true, rowVersion }, Token)).Status);

            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await creator.DeleteAsync($"{ProductsUrl}/{productId}", rowVersion, Token)).Status);
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
        }
    }

    [Fact]
    public async Task A_write_without_the_antiforgery_header_is_refused_with_a_readable_problem()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-forge", FullProductRights, Token);

        var status = await client.SendWithoutAntiforgeryAsync(
            HttpMethod.Post, ProductsUrl, Body(seed, "w103-forge"), Token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    // ---- the create round trip ------------------------------------------------------------------

    /// <summary>
    /// <b>The risk this whole task turns on.</b> A variant's foreign key is the composite
    /// <c>(ProductColourId, ProductId)</c> against <c>ProductColours (Id, ProductId)</c> — an alternate
    /// key, not a primary one (doc 08 §3.2) — so a request that adds a colourway and a variant of that
    /// colourway in one body depends on EF ordering the inserts and fixing the key up from the navigation.
    /// Nothing but a real database can say whether that works.
    /// </summary>
    [Fact]
    public async Task A_create_writes_the_whole_aggregate_in_one_transaction()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-create", FullProductRights, Token);

        var gallery = await AddImageAssetAsync(factory, "Front view");
        var hover = await AddImageAssetAsync(factory, "Back view");
        var (video, poster) = await AddVideoAssetAsync(factory);

        var body = Body(seed, "w103-create");
        body["colours"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = "Indigo Rinse",
                ["colourFamilyId"] = seed.ColourFamilyId,
                ["swatchHex"] = "#28415b",
                ["sortOrder"] = 0,
            },
            new Dictionary<string, object?>
            {
                ["name"] = "Jet Black",
                ["colourFamilyId"] = seed.ColourFamilyId,
                ["sortOrder"] = 1,
            },
        };
        body["variants"] = new object[]
        {
            Variant("Indigo Rinse", seed.SizeIds[0], "W103-IR-S"),
            Variant("Indigo Rinse", seed.SizeIds[1], "W103-IR-M"),
            Variant("Jet Black", seed.SizeIds[0], "W103-JB-S"),
        };
        body["media"] = new object[]
        {
            Media(gallery, null, "Gallery", 0, "Front view"),
            Media(hover, null, "Hover", 0, "Back view"),
            Media(video, null, "Video", 0, null),
        };

        var (status, created) = await client.PostAsync(ProductsUrl, body, Token);
        Assert.Equal(HttpStatusCode.Created, status);

        var productId = created.GetProperty("id").GetInt32();

        try
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var product = await dbContext.Products
                .AsNoTracking()
                .Include(candidate => candidate.Colours)
                .Include(candidate => candidate.Variants)
                .Include(candidate => candidate.Media)
                .Include(candidate => candidate.Stats)
                .Include(candidate => candidate.SearchIndex)
                .Include(candidate => candidate.CategoryLinks)
                .SingleAsync(candidate => candidate.Id == productId, Token);

            Assert.False(product.IsPublished);
            Assert.Equal(2, product.Colours.Count);
            Assert.Equal(3, product.Variants.Count);
            Assert.Equal(3, product.Media.Count);

            // The composite FK really resolved: every variant points at a colourway of this product.
            var colourIds = product.Colours.Select(colour => colour.Id).ToHashSet();
            Assert.All(product.Variants, variant => Assert.Contains(variant.ProductColourId, colourIds));
            Assert.All(product.Variants, variant => Assert.Equal(productId, variant.ProductId));

            // Doc 11 P1 step 13: the price summary, the search text and the card image all land in the
            // same transaction.
            Assert.NotNull(product.Stats);
            Assert.Equal(5490m, product.Stats!.MinListPrice);
            Assert.NotNull(product.SearchIndex);
            Assert.Contains("w103-ir-s", product.SearchIndex!.SearchText, StringComparison.Ordinal);

            var galleryAsset = await AssetIdAsync(dbContext, gallery);
            Assert.Equal(galleryAsset, product.CardImageAssetId);

            // Doc 11 P1 step 10: a stock row per variant, at zero.
            var stock = await dbContext.StockLevels
                .AsNoTracking()
                .Where(level => product.Variants.Select(variant => variant.Id).Contains(level.VariantId))
                .ToListAsync(Token);

            Assert.Equal(3, stock.Count);
            Assert.All(stock, level => Assert.Equal(0, level.OnHand));

            // Doc 08 §3.3: the usage rows the media library's delete guard reads.
            var usages = await dbContext.MediaUsages
                .AsNoTracking()
                .Where(usage =>
                    usage.OwnerType == MediaUsageOwnerTypes.Product
                    && usage.OwnerId == productId.ToString(CultureInfo.InvariantCulture))
                .Select(usage => usage.UsageRole)
                .ToListAsync(Token);

            Assert.Contains(MediaUsageRoles.Card, usages);
            Assert.Contains(MediaUsageRoles.Gallery, usages);
            Assert.Contains(MediaUsageRoles.Hover, usages);
            Assert.Contains(MediaUsageRoles.Video, usages);

            // And the guard really holds: the library refuses to delete a photograph a product uses.
            var librarian = await AdminCatalogApiClient.SignInAsync(
                factory, "w103-librarian", [Permissions.Media.View, Permissions.Media.Delete], Token);

            var delete = await librarian.DeleteAsync($"/api/admin/media/{gallery}", null, Token);
            Assert.Equal(HttpStatusCode.Conflict, delete.Status);
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
            await HardDeleteAssetsAsync(factory, [gallery, hover, video, poster]);
        }
    }

    [Fact]
    public async Task A_create_that_fails_a_unique_index_writes_nothing_at_all()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-atomic", FullProductRights, Token);

        var first = Body(seed, "w103-atomic-a");
        var (status, created) = await client.PostAsync(ProductsUrl, first, Token);
        Assert.Equal(HttpStatusCode.Created, status);
        var productId = created.GetProperty("id").GetInt32();

        try
        {
            // Same slug, different code: the handler's own check refuses it as a field error.
            var clash = Body(seed, "w103-atomic-b");
            clash["slug"] = first["slug"];

            var (clashStatus, problem) = await client.PostAsync(ProductsUrl, clash, Token);

            Assert.Equal(HttpStatusCode.BadRequest, clashStatus);
            Assert.True(problem.GetProperty("errors").TryGetProperty("slug", out _));

            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Assert.Equal(
                1,
                await dbContext.Products.CountAsync(
                    product => product.ProductCode.StartsWith("W103-ATOMIC"), Token));
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
        }
    }

    // ---- publish, edit, unpublish, remove -------------------------------------------------------

    [Fact]
    public async Task A_product_goes_on_the_shop_only_once_the_checklist_passes()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-publish", FullProductRights, Token);

        var gallery = await AddImageAssetAsync(factory, "Front view");

        var body = Body(seed, "w103-publish");
        body["colours"] = new object[] { Colour("Indigo Rinse", seed.ColourFamilyId) };
        body["variants"] = new object[] { Variant("Indigo Rinse", seed.SizeIds[0], "W103-PUB-S") };

        var (_, created) = await client.PostAsync(ProductsUrl, body, Token);
        var productId = created.GetProperty("id").GetInt32();

        try
        {
            // No photograph yet: doc 11 §7 G-01 refuses, and names what is missing.
            var refused = await client.PutAsync(
                $"{ProductsUrl}/{productId}/published",
                new { isPublished = true, rowVersion = await RowVersionAsync(factory, productId) },
                Token);

            Assert.Equal(HttpStatusCode.Conflict, refused.Status);
            Assert.Equal("publish_checklist_failed", refused.Body.GetProperty("code").GetString());
            Assert.Contains(
                "gallery photograph",
                refused.Body.GetProperty("detail").GetString()!,
                StringComparison.Ordinal);

            // The detail payload says the same thing before the button is pressed.
            var before = await client.GetAsync($"{ProductsUrl}/{productId}", Token);
            Assert.False(before.Body.GetProperty("canBePublished").GetBoolean());

            // Attach one and try again.
            body["media"] = new object[] { Media(gallery, null, "Gallery", 0, "Front view") };
            body["rowVersion"] = await RowVersionAsync(factory, productId);

            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.PutAsync($"{ProductsUrl}/{productId}", body, Token)).Status);

            var ready = await client.GetAsync($"{ProductsUrl}/{productId}", Token);
            Assert.True(ready.Body.GetProperty("canBePublished").GetBoolean());

            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.PutAsync(
                    $"{ProductsUrl}/{productId}/published",
                    new { isPublished = true, rowVersion = await RowVersionAsync(factory, productId) },
                    Token)).Status);

            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var published = await dbContext.Products
                .AsNoTracking()
                .SingleAsync(product => product.Id == productId, Token);

            Assert.True(published.IsPublished);
            Assert.NotNull(published.FirstPublishedAtUtc);
            Assert.NotNull(published.NewUntilUtc);

            // And the storefront can now open it, which is the only test of "published" that matters.
            using var shopper = AdminCatalogApiClient.Anonymous(factory);
            using var page = await shopper.GetAsync($"/api/catalog/products/{body["slug"]}", Token);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
            await HardDeleteAssetsAsync(factory, [gallery]);
        }
    }

    [Fact]
    public async Task A_published_products_slug_change_needs_confirming_and_then_moves_the_page()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-slug", FullProductRights, Token);

        var gallery = await AddImageAssetAsync(factory, "Front view");
        var body = Body(seed, "w103-slug");
        body["colours"] = new object[] { Colour("Indigo Rinse", seed.ColourFamilyId) };
        body["variants"] = new object[] { Variant("Indigo Rinse", seed.SizeIds[0], "W103-SLUG-S") };
        body["media"] = new object[] { Media(gallery, null, "Gallery", 0, "Front view") };

        var (_, created) = await client.PostAsync(ProductsUrl, body, Token);
        var productId = created.GetProperty("id").GetInt32();

        try
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.PutAsync(
                    $"{ProductsUrl}/{productId}/published",
                    new { isPublished = true, rowVersion = await RowVersionAsync(factory, productId) },
                    Token)).Status);

            var oldSlug = (string)body["slug"]!;
            body["slug"] = oldSlug + "-v2";
            body["rowVersion"] = await RowVersionAsync(factory, productId);

            var refused = await client.PutAsync($"{ProductsUrl}/{productId}", body, Token);
            Assert.Equal(HttpStatusCode.Conflict, refused.Status);
            Assert.Equal("slug_change_needs_confirmation", refused.Body.GetProperty("code").GetString());
            Assert.Equal(oldSlug, refused.Body.GetProperty("currentSlug").GetString());

            body["confirmSlugChange"] = true;
            body["rowVersion"] = await RowVersionAsync(factory, productId);

            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.PutAsync($"{ProductsUrl}/{productId}", body, Token)).Status);

            // The warning was telling the truth: the old address is gone, with no redirect.
            using var shopper = AdminCatalogApiClient.Anonymous(factory);
            using var gone = await shopper.GetAsync($"/api/catalog/products/{oldSlug}", Token);
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

            using var moved = await shopper.GetAsync($"/api/catalog/products/{oldSlug}-v2", Token);
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
            await HardDeleteAssetsAsync(factory, [gallery]);
        }
    }

    /// <summary>
    /// The W1-12 defect, proved against the real database rather than against a fake: a PUT with no media
    /// field at all leaves every row of <c>catalog.ProductMedia</c> exactly where it was, and leaves the
    /// usage rows the library's delete guard reads.
    /// </summary>
    [Fact]
    public async Task A_real_save_that_omits_media_leaves_the_rows_and_the_usages_untouched()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-media", FullProductRights, Token);

        var gallery = await AddImageAssetAsync(factory, "Front view");
        var hover = await AddImageAssetAsync(factory, "Back view");
        var (video, poster) = await AddVideoAssetAsync(factory);

        var body = Body(seed, "w103-media");
        body["colours"] = new object[] { Colour("Indigo Rinse", seed.ColourFamilyId) };
        body["variants"] = new object[] { Variant("Indigo Rinse", seed.SizeIds[0], "W103-MEDIA-S") };
        body["media"] = new object[]
        {
            Media(gallery, null, "Gallery", 0, "Front view"),
            Media(hover, null, "Hover", 0, "Back view"),
            Media(video, null, "Video", 0, null),
        };

        var (_, created) = await client.PostAsync(ProductsUrl, body, Token);
        var productId = created.GetProperty("id").GetInt32();

        try
        {
            var (mediaBefore, usagesBefore, cardBefore) = await MediaStateAsync(factory, productId);
            Assert.Equal(3, mediaBefore.Count);
            Assert.NotNull(cardBefore);

            // Not vacuous: a mutation that stopped writing usage rows altogether would make the
            // before/after comparison below true by both sides being empty.
            Assert.NotEmpty(usagesBefore);

            // The PUT a screen that is not editing the gallery would send: no media field at all.
            body.Remove("media");
            body["name"] = "W103 Media (renamed)";
            body["rowVersion"] = await RowVersionAsync(factory, productId);

            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.PutAsync($"{ProductsUrl}/{productId}", body, Token)).Status);

            var (mediaAfter, usagesAfter, cardAfter) = await MediaStateAsync(factory, productId);

            Assert.Equal(mediaBefore, mediaAfter);
            Assert.Equal(usagesBefore, usagesAfter);
            Assert.Equal(cardBefore, cardAfter);

            // And the opposite instruction is obeyed.
            body["media"] = Array.Empty<object>();
            body["rowVersion"] = await RowVersionAsync(factory, productId);

            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.PutAsync($"{ProductsUrl}/{productId}", body, Token)).Status);

            var (mediaCleared, usagesCleared, cardCleared) = await MediaStateAsync(factory, productId);
            Assert.Empty(mediaCleared);
            Assert.Empty(usagesCleared);
            Assert.Null(cardCleared);
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
            await HardDeleteAssetsAsync(factory, [gallery, hover, video, poster]);
        }
    }

    /// <summary>
    /// <b>What EF does when a variant leaves a product, against the real model.</b>
    /// <c>ProductVariants → Products</c> is <c>NO ACTION</c> (doc 08 §1.6), not a cascade, so severing that
    /// required relationship by taking the variant out of the collection is exactly the case EF Core can
    /// answer with "the association … has been severed" instead of a DELETE. No unit test can tell: the
    /// fake store just drops it from a list.
    /// <para>
    /// The same save also removes a colourway that owns a gallery row, which is the other half —
    /// <c>ProductMedia.ProductColourId</c> is a real foreign key with <c>NO ACTION</c>, so a row left
    /// pointing at a deleted colourway is a constraint violation rather than a tidy-up.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_edit_can_remove_a_variant_and_a_colour_that_owns_a_gallery_row()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-sever", FullProductRights, Token);

        var shared = await AddImageAssetAsync(factory, "Shared front view");
        var blackOnly = await AddImageAssetAsync(factory, "Jet Black, front");

        var body = Body(seed, "w103-sever");

        // An extra category link as well, so this save also exercises a cascade-deleted child. The store
        // defers CascadeDeleteTiming to SaveChanges, and that has to leave an ordinary cascade working.
        body["extraCategoryIds"] = new object[] { seed.ExtraCategoryId };
        body["colours"] = new object[]
        {
            Colour("Indigo Rinse", seed.ColourFamilyId),
            Colour("Jet Black", seed.ColourFamilyId),
        };
        body["variants"] = new object[]
        {
            Variant("Indigo Rinse", seed.SizeIds[0], "W103-SEV-IR-S"),
            Variant("Indigo Rinse", seed.SizeIds[1], "W103-SEV-IR-M"),
            Variant("Jet Black", seed.SizeIds[0], "W103-SEV-JB-S"),
        };
        body["media"] = new object[]
        {
            Media(shared, null, "Gallery", 0, "Shared front view"),
            Media(blackOnly, "Jet Black", "Gallery", 1, "Jet Black, front"),
        };

        var (_, created) = await client.PostAsync(ProductsUrl, body, Token);
        var productId = created.GetProperty("id").GetInt32();

        try
        {
            // Jet Black goes, with its own variant and its own gallery row; one Indigo Rinse size goes too;
            // and the extra category link goes with them.
            body["extraCategoryIds"] = Array.Empty<object>();
            body["colours"] = new object[] { Colour("Indigo Rinse", seed.ColourFamilyId) };
            body["variants"] = new object[] { Variant("Indigo Rinse", seed.SizeIds[0], "W103-SEV-IR-S") };
            body["media"] = new object[] { Media(shared, null, "Gallery", 0, "Shared front view") };
            body["rowVersion"] = await RowVersionAsync(factory, productId);

            var (status, problem) = await client.PutAsync($"{ProductsUrl}/{productId}", body, Token);

            Assert.True(
                status == HttpStatusCode.NoContent,
                $"the save was refused with {status}: {problem}");

            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var product = await dbContext.Products
                .AsNoTracking()
                .Include(candidate => candidate.Colours)
                .Include(candidate => candidate.Variants)
                .Include(candidate => candidate.Media)
                .Include(candidate => candidate.CategoryLinks)
                .SingleAsync(candidate => candidate.Id == productId, Token);

            Assert.Single(product.Colours);
            Assert.Single(product.Variants);
            Assert.Single(product.Media);
            Assert.Equal("W103-SEV-IR-S", product.Variants.Single().Sku);

            // The primary category's own link stays; the extra one is gone (doc 08 §3.2: the primary
            // category is stored in ProductCategories too, which is why a form cannot unlink it).
            Assert.Equal([seed.CategoryId], product.CategoryLinks.Select(link => link.CategoryId));

            // The variants really left the table rather than being orphaned with a dangling key.
            Assert.Equal(
                1,
                await dbContext.ProductVariants.CountAsync(variant => variant.ProductId == productId, Token));
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
            await HardDeleteAssetsAsync(factory, [shared, blackOnly]);
        }
    }

    /// <summary>
    /// The omit-media rule again, this time with a <b>colour-scoped</b> gallery row — the shape that needs
    /// <c>ProductMedia.ProductColour</c> to be loaded, and the one the first version of the
    /// whole-product test could not have caught.
    /// </summary>
    [Fact]
    public async Task A_save_that_omits_media_keeps_a_colour_scoped_gallery_row()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-scoped", FullProductRights, Token);

        var shared = await AddImageAssetAsync(factory, "Shared front view");
        var blackOnly = await AddImageAssetAsync(factory, "Jet Black, front");

        var body = Body(seed, "w103-scoped");
        body["colours"] = new object[]
        {
            Colour("Indigo Rinse", seed.ColourFamilyId),
            Colour("Jet Black", seed.ColourFamilyId),
        };
        body["variants"] = new object[] { Variant("Indigo Rinse", seed.SizeIds[0], "W103-SCOPED-S") };
        body["media"] = new object[]
        {
            Media(shared, null, "Gallery", 0, "Shared front view"),
            Media(blackOnly, "Jet Black", "Gallery", 1, "Jet Black, front"),
        };

        var (_, created) = await client.PostAsync(ProductsUrl, body, Token);
        var productId = created.GetProperty("id").GetInt32();

        try
        {
            var (before, _, _) = await MediaStateAsync(factory, productId);
            Assert.Equal(2, before.Count);

            body.Remove("media");
            body["name"] = "W103 Scoped (renamed)";
            body["rowVersion"] = await RowVersionAsync(factory, productId);

            var (status, problem) = await client.PutAsync($"{ProductsUrl}/{productId}", body, Token);
            Assert.True(status == HttpStatusCode.NoContent, $"refused with {status}: {problem}");

            var (after, _, _) = await MediaStateAsync(factory, productId);
            Assert.Equal(before, after);

            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // The colour-scoped row still names its colourway, rather than having quietly become a
            // whole-product row — which is what a null navigation during the reconciliation would do.
            Assert.Equal(
                1,
                await dbContext.ProductMedia.CountAsync(
                    row => row.ProductId == productId && row.ProductColourId != null, Token));
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
            await HardDeleteAssetsAsync(factory, [shared, blackOnly]);
        }
    }

    /// <summary>
    /// <b>A size added to a product that already exists has to get a stock row too.</b>
    /// <para>
    /// Doc 11 P1 step 10 says the system creates one for every new variant with on-hand 0, and it is not a
    /// nicety: a variant with no <c>inventory.StockLevels</c> row is invisible to the availability join
    /// <c>ProductCatalogReader</c> uses, so it reads as out of stock for ever and ADM-12 has nothing to
    /// update. The rows were originally created only on create, which left exactly this case broken, and no
    /// test with an in-memory store could tell.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Adding_a_size_to_an_existing_product_gives_it_a_stock_row_at_zero()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-newsize", FullProductRights, Token);

        var body = Body(seed, "w103-newsize");
        body["colours"] = new object[] { Colour("Indigo Rinse", seed.ColourFamilyId) };
        body["variants"] = new object[] { Variant("Indigo Rinse", seed.SizeIds[0], "W103-NEW-S") };

        var (_, created) = await client.PostAsync(ProductsUrl, body, Token);
        var productId = created.GetProperty("id").GetInt32();

        try
        {
            body["variants"] = new object[]
            {
                Variant("Indigo Rinse", seed.SizeIds[0], "W103-NEW-S"),
                Variant("Indigo Rinse", seed.SizeIds[1], "W103-NEW-M"),
            };
            body["rowVersion"] = await RowVersionAsync(factory, productId);

            var (status, problem) = await client.PutAsync($"{ProductsUrl}/{productId}", body, Token);
            Assert.True(status == HttpStatusCode.NoContent, $"refused with {status}: {problem}");

            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var stock = await dbContext.StockLevels
                .AsNoTracking()
                .Where(level => level.Variant!.ProductId == productId)
                .Select(level => new { level.VariantId, level.OnHand, level.Reserved })
                .ToListAsync(Token);

            Assert.Equal(2, stock.Count);
            Assert.All(stock, level => Assert.Equal(0, level.OnHand));
            Assert.All(stock, level => Assert.Equal(0, level.Reserved));

            // And the detail payload shows the new size with nothing in stock, rather than omitting it.
            var (_, detail) = await client.GetAsync($"{ProductsUrl}/{productId}", Token);
            Assert.Equal(2, detail.GetProperty("variants").GetArrayLength());
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
        }
    }

    /// <summary>
    /// <b>The read model can be handed straight back to the write model.</b>
    /// <para>
    /// <c>GET /products/{id}</c>, then <c>PUT</c> that same body back unchanged, and expect 204 with
    /// nothing moved. This is the test that did not exist anywhere, and its absence is why two of ADM-10's
    /// scope bullets shipped broken: every other integration test hand-builds its request body from
    /// scratch, so none of them could notice that the response and the request disagreed about how an enum
    /// is spelled.
    /// </para>
    /// <para>
    /// <c>Program.cs</c> registers a global <c>JsonStringEnumConverter</c>, so <c>badgeOverride</c> comes
    /// back as <c>OnlineExclusive</c> and <c>mediaRole</c> as <c>Gallery</c>. The request DTOs declared
    /// <c>int?</c>, so feeding either back answered <b>500</b> (the badge) or a raw <b>400</b> (the
    /// gallery). A round trip is the cheapest possible guard against that whole family, and it keeps
    /// guarding as fields are added.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_products_own_payload_can_be_sent_straight_back_unchanged()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-roundtrip", FullProductRights, Token);

        var gallery = await AddImageAssetAsync(factory, "Front view");
        var hover = await AddImageAssetAsync(factory, "Back view");
        var (video, poster) = await AddVideoAssetAsync(factory);

        // Every field that can carry a value carries one, including both enums and a colour-scoped
        // gallery row: a round trip over a payload full of nulls proves almost nothing.
        var body = Body(seed, "w103-roundtrip");
        body["extraCategoryIds"] = new object[] { seed.ExtraCategoryId };
        body["badgeOverride"] = "OnlineExclusive";
        body["shortDescription"] = "Everyday denim.";
        body["description"] = "<p>Everyday denim.</p>";
        body["careInstructions"] = "Machine wash cold.";
        body["compareAtPrice"] = 6990m;
        body["recommendedRank"] = 3;
        body["seoTitle"] = "Round trip | Colombo Wardrobe";
        body["seoDescription"] = "Everyday denim.";
        body["colours"] = new object[]
        {
            Colour("Indigo Rinse", seed.ColourFamilyId),
            Colour("Jet Black", seed.ColourFamilyId),
        };
        body["variants"] = new object[]
        {
            Variant("Indigo Rinse", seed.SizeIds[0], "W103-RT-IR-S"),
            Variant("Jet Black", seed.SizeIds[0], "W103-RT-JB-S"),
        };
        body["media"] = new object[]
        {
            Media(gallery, null, "Gallery", 0, "Front view"),
            Media(hover, "Jet Black", "Hover", 1, "Back view"),
            Media(video, null, "Video", 2, null),
        };

        var (createStatus, created) = await client.PostAsync(ProductsUrl, body, Token);
        Assert.Equal(HttpStatusCode.Created, createStatus);
        var productId = created.GetProperty("id").GetInt32();

        try
        {
            var (readStatus, detail) = await client.GetAsync($"{ProductsUrl}/{productId}", Token);
            Assert.Equal(HttpStatusCode.OK, readStatus);

            // The payload really does spell its enums as names, which is what makes this test meaningful.
            Assert.Equal("OnlineExclusive", detail.GetProperty("badgeOverride").GetString());
            Assert.Equal("Gallery", detail.GetProperty("media")[0].GetProperty("mediaRole").GetString());

            // Straight back, untouched but for the confirmation an unpublished product does not need and
            // a published one would.
            var echoed = JsonNode.Parse(detail.GetRawText())!.AsObject();
            echoed["confirmSlugChange"] = true;

            var (writeStatus, problem) = await client.PutRawAsync(
                $"{ProductsUrl}/{productId}", echoed.ToJsonString(), Token);

            Assert.True(
                writeStatus == HttpStatusCode.NoContent,
                $"the product's own payload was refused with {writeStatus}: {problem}");

            // Nothing moved: a round trip that quietly dropped the gallery or the badge would be worse
            // than a refusal.
            var (_, after) = await client.GetAsync($"{ProductsUrl}/{productId}", Token);

            Assert.Equal("OnlineExclusive", after.GetProperty("badgeOverride").GetString());
            Assert.Equal(3, after.GetProperty("media").GetArrayLength());
            Assert.Equal(2, after.GetProperty("colours").GetArrayLength());
            Assert.Equal(2, after.GetProperty("variants").GetArrayLength());
            Assert.Single(after.GetProperty("extraCategoryIds").EnumerateArray());
            Assert.Equal(
                detail.GetProperty("cardImageUrl").GetString(),
                after.GetProperty("cardImageUrl").GetString());
            Assert.Equal(
                detail.GetProperty("hoverImageUrl").GetString(),
                after.GetProperty("hoverImageUrl").GetString());
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
            await HardDeleteAssetsAsync(factory, [gallery, hover, video, poster]);
        }
    }

    /// <summary>
    /// Every assignable badge is storable and reads back as its own name (ADM-10's "badges").
    /// <para>
    /// Zero tests mentioned <c>badgeOverride</c> before this one, and the field answered 500 for every
    /// value an admin could choose.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("New")]
    [InlineData("Sale")]
    [InlineData("OnlineExclusive")]
    [InlineData("BestSeller")]
    public async Task Every_assignable_badge_can_be_saved_and_read_back(string badge)
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(
            factory, $"w103-badge-{badge}", FullProductRights, Token);

        var body = Body(seed, $"w103-badge-{badge}");
        body["badgeOverride"] = badge;

        var (status, created) = await client.PostAsync(ProductsUrl, body, Token);
        Assert.Equal(HttpStatusCode.Created, status);
        var productId = created.GetProperty("id").GetInt32();

        try
        {
            var (_, detail) = await client.GetAsync($"{ProductsUrl}/{productId}", Token);
            Assert.Equal(badge, detail.GetProperty("badgeOverride").GetString());

            // And through an edit, which is the other half of the field.
            body["badgeOverride"] = null;
            body["rowVersion"] = await RowVersionAsync(factory, productId);

            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.PutAsync($"{ProductsUrl}/{productId}", body, Token)).Status);

            var (_, cleared) = await client.GetAsync($"{ProductsUrl}/{productId}", Token);
            Assert.Equal(JsonValueKind.Null, cleared.GetProperty("badgeOverride").ValueKind);
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
        }
    }

    /// <summary>
    /// <c>LowStock</c> is a real <c>ProductBadge</c> that doc 08 §3.2 forbids storing, and the numeric
    /// spellings a lenient parse would accept. Each has to be a named field error rather than a 500 or an
    /// undefined value in the column.
    /// </summary>
    [Theory]
    [InlineData("LowStock")]
    [InlineData("1")]
    [InlineData("9")]
    [InlineData("255")]
    [InlineData("Badge")]
    public async Task A_badge_the_shop_cannot_store_is_a_named_field_error(string badge)
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-badbadge", FullProductRights, Token);

        var body = Body(seed, "w103-badbadge");
        body["badgeOverride"] = badge;

        var (status, problem) = await client.PostAsync(ProductsUrl, body, Token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(
            problem.GetProperty("errors").TryGetProperty("badgeOverride", out _),
            $"the refusal did not name the field: {problem}");
    }

    /// <summary>
    /// <b>Removing a size that sits in a shopper's bag.</b>
    /// <para>
    /// <c>FK_CartItems_ProductVariants_VariantId</c> is <c>NO ACTION</c> (doc 08 §1.6), so this answered
    /// <b>500</b> with a constraint name and no explanation — the fourth unguarded reference found on this
    /// task, and the first one a test rather than luck did not find. The bag line is deleted with the
    /// variant: doc 11 P1's edge cases name this exact case, P5 already copes with a line going away, and
    /// blocking instead would let any shopper who left a size in their bag stop an admin correcting the
    /// catalogue (see <c>VariantReferences</c>).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Removing_a_size_that_sits_in_a_shoppers_bag_takes_the_bag_line_with_it()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-bag", FullProductRights, Token);

        var body = Body(seed, "w103-bag");
        body["colours"] = new object[] { Colour("Indigo Rinse", seed.ColourFamilyId) };
        body["variants"] = new object[]
        {
            Variant("Indigo Rinse", seed.SizeIds[0], "W103-BAG-S"),
            Variant("Indigo Rinse", seed.SizeIds[1], "W103-BAG-M"),
        };

        var (_, created) = await client.PostAsync(ProductsUrl, body, Token);
        var productId = created.GetProperty("id").GetInt32();

        try
        {
            int variantId;
            int cartId;

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                variantId = await dbContext.ProductVariants
                    .AsNoTracking()
                    .Where(variant => variant.ProductId == productId && variant.Sku == "W103-BAG-S")
                    .Select(variant => variant.Id)
                    .SingleAsync(Token);

                // A real bag with a real line, so the real foreign key is in the way.
                var cart = new Cart { Token = Guid.NewGuid(), LastActivityAtUtc = DateTime.UtcNow };
                dbContext.Carts.Add(cart);
                await dbContext.SaveChangesAsync(Token);
                cartId = cart.Id;

                dbContext.CartItems.Add(new CartItem
                {
                    CartId = cartId,
                    VariantId = variantId,
                    Quantity = 1,
                    UnitPriceAtAdd = 5490m,
                });

                await dbContext.SaveChangesAsync(Token);
            }

            // Untick the size the bag holds.
            body["variants"] = new object[] { Variant("Indigo Rinse", seed.SizeIds[1], "W103-BAG-M") };
            body["rowVersion"] = await RowVersionAsync(factory, productId);

            var (status, problem) = await client.PutAsync($"{ProductsUrl}/{productId}", body, Token);

            Assert.True(status == HttpStatusCode.NoContent, $"refused with {status}: {problem}");

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                Assert.False(
                    await dbContext.ProductVariants.AnyAsync(variant => variant.Id == variantId, Token));

                Assert.False(
                    await dbContext.CartItems.AnyAsync(item => item.VariantId == variantId, Token));

                // The bag itself survives: only the line went.
                Assert.True(await dbContext.Carts.AnyAsync(cart => cart.Id == cartId, Token));

                // And the trail says a shopper's bag was changed, which is the one effect of this save
                // that nobody on the admin side can see afterwards.
                var audit = await dbContext.AuditLogs
                    .AsNoTracking()
                    .Where(entry =>
                        entry.EntityType == "Product"
                        && entry.EntityId == productId.ToString(CultureInfo.InvariantCulture))
                    .OrderByDescending(entry => entry.Id)
                    .Select(entry => entry.AfterJson)
                    .FirstAsync(Token);

                Assert.Contains("cartLinesRemoved", audit!, StringComparison.Ordinal);
            }

            await using (var cleanup = factory.Services.CreateAsyncScope())
            {
                var dbContext = cleanup.ServiceProvider.GetRequiredService<AppDbContext>();
                await dbContext.Database.ExecuteSqlRawAsync(
                    "DELETE FROM sales.CartItems WHERE CartId = {0}; DELETE FROM sales.Carts WHERE Id = {0};",
                    [cartId],
                    Token);
            }
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
        }
    }

    /// <summary>
    /// <b>The ordered-variant guard, against a real order line rather than a fake store.</b>
    /// <para>
    /// The guard had only ever met <c>FakeProductWriteStore</c> — the same fake-store coverage that missed
    /// the original 500 — so this inserts a genuine <c>sales.Orders</c> row and a genuine
    /// <c>sales.OrderLines</c> row through <c>SalesRowWriter</c>, the helper the sales schema tests use,
    /// and proves two things: the 409 names the SKU, and <c>FK_OrderLines_ProductVariants_VariantId</c> is
    /// never reached.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_variant_a_real_order_line_names_is_refused_rather_than_breaking_the_foreign_key()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-ordered", FullProductRights, Token);

        var body = Body(seed, "w103-ordered");
        body["colours"] = new object[] { Colour("Indigo Rinse", seed.ColourFamilyId) };
        body["variants"] = new object[]
        {
            Variant("Indigo Rinse", seed.SizeIds[0], "W103-ORD-S"),
            Variant("Indigo Rinse", seed.SizeIds[1], "W103-ORD-M"),
        };

        var (_, created) = await client.PostAsync(ProductsUrl, body, Token);
        var productId = created.GetProperty("id").GetInt32();
        var orderId = 0;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var writer = new SalesRowWriter(dbContext);

                var variantId = await dbContext.ProductVariants
                    .AsNoTracking()
                    .Where(variant => variant.ProductId == productId && variant.Sku == "W103-ORD-S")
                    .Select(variant => variant.Id)
                    .SingleAsync(Token);

                // The sales reference data is not in the shared seed — `SalesSchemaTestBase` creates its
                // own per run, with codes nothing else uses, and so does this. An order has real foreign
                // keys to both, so there is no way to make a real order line without them.
                var providerCode = SalesRowWriter.Unique("W103Prov");
                await writer.InsertWithoutIdentityAsync(
                    "sales.PaymentProviders",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Code"] = providerCode,
                        ["DisplayName"] = "W1-03 test provider",
                        ["IsEnabled"] = true,
                        ["DisplayOrder"] = (short)1,
                        ["CreatedAtUtc"] = DateTime.UtcNow,
                    },
                    Token);

                var deliveryRow = SalesRowWriter.DeliveryMethodRow();
                var deliveryMethodId = await writer.InsertAsync("sales.DeliveryMethods", deliveryRow, Token);

                orderId = await writer.InsertAsync(
                    "sales.Orders",
                    SalesRowWriter.OrderRow(
                        providerCode, deliveryMethodId, (string)deliveryRow["Code"]!),
                    Token);

                await writer.InsertAsync(
                    "sales.OrderLines",
                    SalesRowWriter.OrderLineRow(
                        orderId, productId, variantId, seed.DepartmentId, seed.CategoryId),
                    Token);
            }

            body["variants"] = new object[] { Variant("Indigo Rinse", seed.SizeIds[1], "W103-ORD-M") };
            body["rowVersion"] = await RowVersionAsync(factory, productId);

            var (status, problem) = await client.PutAsync($"{ProductsUrl}/{productId}", body, Token);

            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal("variant_not_deletable", problem.GetProperty("code").GetString());
            Assert.Contains(
                "W103-ORD-S",
                problem.GetProperty("orderedSkus").EnumerateArray().Select(sku => sku.GetString()));

            // Nothing was written: the variant and its line are both still there.
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                Assert.Equal(
                    2,
                    await dbContext.ProductVariants.CountAsync(
                        variant => variant.ProductId == productId, Token));

                Assert.True(await dbContext.OrderLines.AnyAsync(line => line.OrderId == orderId, Token));
            }
        }
        finally
        {
            if (orderId != 0)
            {
                await using var cleanup = factory.Services.CreateAsyncScope();
                var dbContext = cleanup.ServiceProvider.GetRequiredService<AppDbContext>();
                await dbContext.Database.ExecuteSqlRawAsync(
                    "DELETE FROM sales.OrderLines WHERE OrderId = {0}; DELETE FROM sales.Orders WHERE Id = {0};",
                    [orderId],
                    Token);
            }

            await HardDeleteAsync(factory, productId);
        }
    }

    /// <summary>
    /// <b>A published product cannot be edited into a state its own checklist forbids.</b>
    /// <para>
    /// The checklist of doc 11 §7 G-01 ran only on the publish <i>transition</i>, so <c>media: []</c> on a
    /// published product answered 204 and left it on the shop with no photographs and a null card image —
    /// and the storefront then served that product 200 with zero images. A shopper saw a live product with
    /// nothing to look at.
    /// </para>
    /// <para>
    /// The <c>media: []</c> instruction itself is right; doing it while the product is live is not. It is
    /// refused rather than auto-unpublished, because taking a product off the shop is its own deliberate
    /// act with its own audit row and a 204 that had quietly done it would be discovered days later.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_edit_cannot_leave_a_published_product_in_a_state_it_could_not_be_published_in()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-live", FullProductRights, Token);

        var gallery = await AddImageAssetAsync(factory, "Front view");

        var body = Body(seed, "w103-live");
        body["colours"] = new object[] { Colour("Indigo Rinse", seed.ColourFamilyId) };
        body["variants"] = new object[] { Variant("Indigo Rinse", seed.SizeIds[0], "W103-LIVE-S") };
        body["media"] = new object[] { Media(gallery, null, "Gallery", 0, "Front view") };

        var (_, created) = await client.PostAsync(ProductsUrl, body, Token);
        var productId = created.GetProperty("id").GetInt32();

        try
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.PutAsync(
                    $"{ProductsUrl}/{productId}/published",
                    new { isPublished = true, rowVersion = await RowVersionAsync(factory, productId) },
                    Token)).Status);

            // Clearing the gallery of a live product.
            body["media"] = Array.Empty<object>();
            body["rowVersion"] = await RowVersionAsync(factory, productId);

            var (status, problem) = await client.PutAsync($"{ProductsUrl}/{productId}", body, Token);

            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal("published_product_would_break", problem.GetProperty("code").GetString());
            Assert.Contains(
                "gallery photograph",
                problem.GetProperty("detail").GetString()!,
                StringComparison.Ordinal);

            // Nothing was written, and the product is still whole on the shop.
            var (_, unchanged) = await client.GetAsync($"{ProductsUrl}/{productId}", Token);
            Assert.True(unchanged.GetProperty("isPublished").GetBoolean());
            Assert.Equal(1, unchanged.GetProperty("media").GetArrayLength());
            Assert.NotNull(unchanged.GetProperty("cardImageUrl").GetString());

            // Switching every variant off is the same class of change, and is refused the same way.
            var offBody = Body(seed, "w103-live");
            offBody["colours"] = new object[] { Colour("Indigo Rinse", seed.ColourFamilyId) };
            offBody["variants"] = new object[]
            {
                SwitchedOff(Variant("Indigo Rinse", seed.SizeIds[0], "W103-LIVE-S")),
            };
            offBody["media"] = new object[] { Media(gallery, null, "Gallery", 0, "Front view") };
            offBody["rowVersion"] = await RowVersionAsync(factory, productId);

            var (offStatus, offProblem) = await client.PutAsync($"{ProductsUrl}/{productId}", offBody, Token);

            Assert.Equal(HttpStatusCode.Conflict, offStatus);
            Assert.Equal("published_product_would_break", offProblem.GetProperty("code").GetString());

            // And the way through is the honest one: take it off the shop, then make the change.
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.PutAsync(
                    $"{ProductsUrl}/{productId}/published",
                    new { isPublished = false, rowVersion = await RowVersionAsync(factory, productId) },
                    Token)).Status);

            body["rowVersion"] = await RowVersionAsync(factory, productId);
            var (afterStatus, afterProblem) = await client.PutAsync($"{ProductsUrl}/{productId}", body, Token);

            Assert.True(
                afterStatus == HttpStatusCode.NoContent,
                $"clearing the gallery of an unpublished product was refused with {afterStatus}: {afterProblem}");

            var (_, cleared) = await client.GetAsync($"{ProductsUrl}/{productId}", Token);
            Assert.False(cleared.GetProperty("isPublished").GetBoolean());
            Assert.Equal(0, cleared.GetProperty("media").GetArrayLength());
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
            await HardDeleteAssetsAsync(factory, [gallery]);
        }
    }

    [Fact]
    public async Task A_stale_row_version_loses_a_real_race_and_writes_nothing()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-race", FullProductRights, Token);

        var body = Body(seed, "w103-race");
        var (_, created) = await client.PostAsync(ProductsUrl, body, Token);
        var productId = created.GetProperty("id").GetInt32();

        try
        {
            var stale = await RowVersionAsync(factory, productId);

            body["shortDescription"] = "First writer wins.";
            body["rowVersion"] = stale;
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.PutAsync($"{ProductsUrl}/{productId}", body, Token)).Status);

            body["shortDescription"] = "Second writer must be refused.";
            body["rowVersion"] = stale;
            var refused = await client.PutAsync($"{ProductsUrl}/{productId}", body, Token);

            Assert.Equal(HttpStatusCode.Conflict, refused.Status);
            Assert.Equal("stale_row_version", refused.Body.GetProperty("code").GetString());

            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var product = await dbContext.Products
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == productId, Token);

            Assert.Equal("First writer wins.", product.ShortDescription);
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
        }
    }

    [Fact]
    public async Task Removing_a_product_takes_it_off_the_shop_and_keeps_its_variants()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-remove", FullProductRights, Token);

        var body = Body(seed, "w103-remove");
        body["colours"] = new object[] { Colour("Indigo Rinse", seed.ColourFamilyId) };
        body["variants"] = new object[] { Variant("Indigo Rinse", seed.SizeIds[0], "W103-REM-S") };

        var (_, created) = await client.PostAsync(ProductsUrl, body, Token);
        var productId = created.GetProperty("id").GetInt32();

        try
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.DeleteAsync(
                    $"{ProductsUrl}/{productId}", await RowVersionAsync(factory, productId), Token)).Status);

            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await dbContext.Products
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == productId, Token);

            Assert.True(row.IsDeleted);
            Assert.NotNull(row.DeletedAtUtc);

            // Doc 11 P1 step 15: the variants stay.
            Assert.Equal(
                1,
                await dbContext.ProductVariants
                    .IgnoreQueryFilters()
                    .CountAsync(variant => variant.ProductId == productId, Token));

            // And it is gone from the admin list and from the storefront alike.
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await client.GetAsync($"{ProductsUrl}/{productId}", Token)).Status);
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
        }
    }

    // ---- the W1-02 follow-up ---------------------------------------------------------------------

    /// <summary>
    /// The constraint this task carried from W1-02: the domain guard protects writes that go through the
    /// application, and nothing else. A support script or a restore from a spreadsheet can put a value in
    /// <c>catalog.Products.Slug</c> that makes <c>/p/{slug}</c> answer 400 — a product the listing links to
    /// and nobody can open.
    /// </summary>
    [Theory]
    [InlineData("Denim-JACKET")]
    [InlineData("denim jacket")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("café")]
    [InlineData("")]
    public async Task A_direct_sql_write_cannot_put_a_bad_slug_in_a_product(string slug)
    {
        sqlServer.SkipIfUnavailable();
        var factory = await HostAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var anchor = await dbContext.Products
            .AsNoTracking()
            .Select(product => new { product.DepartmentId, product.PrimaryCategoryId })
            .FirstAsync(Token);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.Products
                (ProductCode, Name, Slug, DepartmentId, PrimaryCategoryId, BasePrice, IsPublished, IsDeleted, CreatedAtUtc)
            VALUES ('W103-DIRECT', N'Direct write', {0}, {1}, {2}, 100.00, 0, 0, SYSUTCDATETIME());
            """,
            [slug, anchor.DepartmentId, anchor.PrimaryCategoryId],
            Token));

        Assert.Contains("CK_Products_Slug", exception.ToString(), StringComparison.Ordinal);

        Assert.False(
            await dbContext.Products.IgnoreQueryFilters()
                .AnyAsync(product => product.ProductCode == "W103-DIRECT", Token));
    }

    [Fact]
    public async Task A_direct_sql_write_with_a_good_slug_is_accepted()
    {
        sqlServer.SkipIfUnavailable();
        var factory = await HostAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var anchor = await dbContext.Products
            .AsNoTracking()
            .Select(product => new { product.DepartmentId, product.PrimaryCategoryId })
            .FirstAsync(Token);

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO catalog.Products
                    (ProductCode, Name, Slug, DepartmentId, PrimaryCategoryId, BasePrice, IsPublished, IsDeleted, CreatedAtUtc)
                VALUES ('W103-DIRECT-OK', N'Direct write', 'w103-direct-ok', {0}, {1}, 100.00, 0, 0, SYSUTCDATETIME());
                """,
                [anchor.DepartmentId, anchor.PrimaryCategoryId],
                Token);

            // The constraint is not simply refusing everything, which is the way a CHECK test passes for
            // the wrong reason.
            Assert.True(
                await dbContext.Products.AnyAsync(product => product.Slug == "w103-direct-ok", Token));
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "DELETE FROM catalog.Products WHERE ProductCode = 'W103-DIRECT-OK';", Token);
        }
    }

    // ---- the list and the form options ----------------------------------------------------------

    [Fact]
    public async Task The_list_finds_an_unpublished_product_by_name_code_and_sku()
    {
        var factory = await HostAsync();
        var seed = await ReferenceDataAsync(factory);
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-list", FullProductRights, Token);

        var body = Body(seed, "w103-list");
        body["name"] = "W103 Findable Jeans";
        body["colours"] = new object[] { Colour("Indigo Rinse", seed.ColourFamilyId) };
        body["variants"] = new object[] { Variant("Indigo Rinse", seed.SizeIds[0], "W103-FIND-S") };

        var (_, created) = await client.PostAsync(ProductsUrl, body, Token);
        var productId = created.GetProperty("id").GetInt32();

        try
        {
            foreach (var term in new[] { "Findable", "W103-LIST", "W103-FIND-S" })
            {
                var (status, page) = await client.GetAsync($"{ProductsUrl}?term={Uri.EscapeDataString(term)}", Token);

                Assert.Equal(HttpStatusCode.OK, status);
                Assert.Contains(
                    page.GetProperty("items").EnumerateArray(),
                    item => item.GetProperty("id").GetInt32() == productId);
            }

            // published=true must not find it: it has never been published.
            var (_, publishedOnly) = await client.GetAsync($"{ProductsUrl}?term=Findable&published=true", Token);
            Assert.Empty(publishedOnly.GetProperty("items").EnumerateArray());

            // A LIKE wildcard an admin typed is a literal, not a wildcard.
            var (_, wildcard) = await client.GetAsync($"{ProductsUrl}?term={Uri.EscapeDataString("%")}", Token);
            Assert.Equal(0, wildcard.GetProperty("totalCount").GetInt32());
        }
        finally
        {
            await HardDeleteAsync(factory, productId);
        }
    }

    [Fact]
    public async Task The_form_options_carry_everything_the_form_needs()
    {
        var factory = await HostAsync();
        var client = await AdminCatalogApiClient.SignInAsync(factory, "w103-options", FullProductRights, Token);

        var (status, options) = await client.GetAsync($"{ProductsUrl}/form-options", Token);

        Assert.Equal(HttpStatusCode.OK, status);

        foreach (var list in new[]
        {
            "departments", "categories", "ageGroups", "materials", "fits", "occasions",
            "colourFamilies", "sizes", "sizeGuides",
        })
        {
            Assert.NotEmpty(options.GetProperty(list).EnumerateArray());
        }

        // The indented display name is what makes a flat <select> readable at 320 px.
        Assert.Contains(
            options.GetProperty("categories").EnumerateArray(),
            category => category.GetProperty("displayName").GetString()!.Contains('›', StringComparison.Ordinal));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static IEnumerable<(HttpMethod Method, string Path)> Routes()
    {
        yield return (HttpMethod.Get, ProductsUrl);
        yield return (HttpMethod.Get, $"{ProductsUrl}/form-options");
        yield return (HttpMethod.Get, $"{ProductsUrl}/related-search");
        yield return (HttpMethod.Get, $"{ProductsUrl}/1");
        yield return (HttpMethod.Post, ProductsUrl);
        yield return (HttpMethod.Put, $"{ProductsUrl}/1");
        yield return (HttpMethod.Put, $"{ProductsUrl}/1/published");
        yield return (HttpMethod.Delete, $"{ProductsUrl}/1");
    }

    private async Task<ApiFactory> HostAsync(string environment = "Testing")
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        if (!_factories.TryGetValue(environment, out var factory))
        {
            factory = new ApiFactory(sqlServer.ConnectionString, environment);
            _factories[environment] = factory;
        }

        return factory;
    }

    /// <summary>Real ids out of the seeded reference data, so nothing here hard-codes an identity value.</summary>
    private static async Task<SeedIds> ReferenceDataAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var department = await dbContext.Departments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Slug == CatalogDepartments.WomenSlug, Token);

        // The deepest category in the department, so the search-text assertion has more than one path name
        // to find — but not *required* to be deep, because which levels the reference data seeds is not
        // this test's business.
        var category = await dbContext.Categories
            .AsNoTracking()
            .Where(candidate => candidate.DepartmentId == department.Id)
            .OrderByDescending(candidate => candidate.Depth)
            .ThenBy(candidate => candidate.Id)
            .FirstAsync(Token);

        var colourFamily = await dbContext.ColourFamilies
            .AsNoTracking()
            .OrderBy(family => family.Id)
            .FirstAsync(Token);

        var sizes = await dbContext.Sizes
            .AsNoTracking()
            .OrderBy(size => size.ScaleName)
            .ThenBy(size => size.SortOrder)
            .Select(size => size.Id)
            .Take(2)
            .ToListAsync(Token);

        var material = await dbContext.AttributeOptions
            .AsNoTracking()
            .Where(option => option.AttributeType == AttributeOptionType.Material)
            .OrderBy(option => option.Id)
            .Select(option => option.Id)
            .FirstAsync(Token);

        var extraCategory = await dbContext.Categories
            .AsNoTracking()
            .Where(candidate => candidate.DepartmentId == department.Id && candidate.Id != category.Id)
            .OrderBy(candidate => candidate.Id)
            .Select(candidate => candidate.Id)
            .FirstAsync(Token);

        return new SeedIds(department.Id, category.Id, extraCategory, colourFamily.Id, sizes, material);
    }

    private static Dictionary<string, object?> Body(SeedIds seed, string label) => new()
    {
        ["productCode"] = label.ToUpperInvariant(),
        ["name"] = label,
        ["slug"] = label.ToLowerInvariant(),
        ["departmentId"] = seed.DepartmentId,
        ["primaryCategoryId"] = seed.CategoryId,
        ["materialId"] = seed.MaterialId,
        ["basePrice"] = 5490m,
        ["colours"] = Array.Empty<object>(),
        ["variants"] = Array.Empty<object>(),
        ["relatedProducts"] = Array.Empty<object>(),
    };

    private static Dictionary<string, object?> Colour(string name, int colourFamilyId) => new()
    {
        ["name"] = name,
        ["colourFamilyId"] = colourFamilyId,
        ["sortOrder"] = 0,
    };

    /// <summary>The same variant row, switched off.</summary>
    private static Dictionary<string, object?> SwitchedOff(Dictionary<string, object?> variant)
    {
        variant["isActive"] = false;

        return variant;
    }

    private static Dictionary<string, object?> Variant(string colourName, int sizeId, string sku) => new()
    {
        ["colourName"] = colourName,
        ["sizeId"] = sizeId,
        ["sku"] = sku,
        ["isActive"] = true,
        ["sortOrder"] = 0,
    };

    private static Dictionary<string, object?> Media(
        Guid assetPublicId,
        string? colourName,
        string mediaRole,
        int sortOrder,
        string? altText) => new()
    {
        ["assetPublicId"] = assetPublicId,
        ["colourName"] = colourName,
        ["mediaRole"] = mediaRole,
        ["sortOrder"] = sortOrder,
        ["altTextOverride"] = altText,
    };

    private static async Task<string> RowVersionAsync(ApiFactory factory, int productId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rowVersion = await dbContext.Products
            .AsNoTracking()
            .Where(product => product.Id == productId)
            .Select(product => product.RowVersion)
            .SingleAsync(Token);

        return Convert.ToBase64String(rowVersion!);
    }

    private static async Task<(
        List<(int AssetId, ProductMediaRole Role, short SortOrder, string? Alt)> Media,
        List<string> Usages,
        int? CardImageAssetId)> MediaStateAsync(ApiFactory factory, int productId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var media = await dbContext.ProductMedia
            .AsNoTracking()
            .Where(row => row.ProductId == productId)
            .OrderBy(row => row.Id)
            .Select(row => new { row.MediaAssetId, row.MediaRole, row.SortOrder, row.AltTextOverride })
            .ToListAsync(Token);

        var usages = await dbContext.MediaUsages
            .AsNoTracking()
            .Where(usage =>
                usage.OwnerType == MediaUsageOwnerTypes.Product
                && usage.OwnerId == productId.ToString(CultureInfo.InvariantCulture))
            .Select(usage => usage.MediaAssetId + ":" + usage.UsageRole)
            .ToListAsync(Token);

        var card = await dbContext.Products
            .AsNoTracking()
            .Where(product => product.Id == productId)
            .Select(product => product.CardImageAssetId)
            .SingleAsync(Token);

        usages.Sort(StringComparer.Ordinal);

        return (
            media.Select(row => (row.MediaAssetId, row.MediaRole, row.SortOrder, row.AltTextOverride)).ToList(),
            usages,
            card);
    }

    private static async Task<int> AssetIdAsync(AppDbContext dbContext, Guid publicId) =>
        await dbContext.MediaAssets
            .AsNoTracking()
            .Where(asset => asset.PublicId == publicId)
            .Select(asset => asset.Id)
            .SingleAsync(Token);

    /// <summary>
    /// A library row for an image, inserted directly.
    /// <para>
    /// <b>Not through the upload endpoint, deliberately.</b> The contract these endpoints have with the
    /// media library is a row that is <c>Ready</c>, <c>Public</c> and of the right <c>MediaType</c>
    /// — nothing here reads a file. Driving a real multipart upload would test W1-04's pipeline (which
    /// <c>AdminMediaEndpointTests</c> already does, with its own volumes) and would make every product
    /// test depend on media storage being configured for this host, which it is not.
    /// </para>
    /// </summary>
    private static async Task<Guid> AddImageAssetAsync(ApiFactory factory, string altText) =>
        await AddAssetAsync(factory, MediaAssetType.Image, altText, posterAssetId: null);

    /// <summary>
    /// A library row for an MP4 and the poster row doc 08 §3.3's check constraint requires beside it
    /// (<c>MediaType = 2 ⇒ PosterAssetId NOT NULL</c>).
    /// </summary>
    private static async Task<(Guid Video, Guid Poster)> AddVideoAssetAsync(ApiFactory factory)
    {
        var poster = await AddAssetAsync(factory, MediaAssetType.Image, "Video poster", posterAssetId: null);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var posterId = await AssetIdAsync(dbContext, poster);

        var video = await AddAssetAsync(factory, MediaAssetType.Video, null, posterId);

        return (video, poster);
    }

    private static async Task<Guid> AddAssetAsync(
        ApiFactory factory,
        MediaAssetType mediaType,
        string? altText,
        int? posterAssetId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var publicId = Guid.NewGuid();
        var isVideo = mediaType == MediaAssetType.Video;

        dbContext.MediaAssets.Add(new MediaAsset
        {
            PublicId = publicId,
            MediaType = mediaType,
            Visibility = MediaVisibility.Public,
            Status = MediaAssetStatus.Ready,
            OriginalFileName = isVideo ? "w103.mp4" : "w103.png",
            StorageKey = $"public/{(isVideo ? "videos" : "images")}/{publicId:N}/original.{(isVideo ? "mp4" : "png")}",
            ContentType = isVideo ? "video/mp4" : "image/png",
            SizeBytes = 1024,
            Width = isVideo ? null : 640,
            Height = isVideo ? null : 480,
            DurationSeconds = isVideo ? 12.5m : null,
            PosterAssetId = posterAssetId,
            RenditionSetVersion = isVideo ? null : MediaRenditions.CurrentSetVersion,
            Checksum = System.Security.Cryptography.SHA256.HashData(publicId.ToByteArray()),
            AltText = altText,
        });

        await dbContext.SaveChangesAsync(Token);

        return publicId;
    }

    /// <summary>
    /// Removes a product for real, so the shared database is left as it was found. Soft-deleting would
    /// leave a row that the next run's unique-index checks still have to step around.
    /// </summary>
    private static async Task HardDeleteAsync(ApiFactory factory, int productId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Order matters: the usage rows and the stock rows have no cascade from the product, and the
        // variants are NO ACTION.
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM media.MediaUsages WHERE OwnerType = 'Product' AND OwnerId = CAST({0} AS varchar(100));
            DELETE FROM inventory.StockLevels WHERE VariantId IN (SELECT Id FROM catalog.ProductVariants WHERE ProductId = {0});
            DELETE FROM catalog.ProductVariants WHERE ProductId = {0};
            DELETE FROM catalog.RelatedProducts WHERE ProductId = {0} OR RelatedProductId = {0};
            DELETE FROM catalog.ProductMedia WHERE ProductId = {0};
            DELETE FROM catalog.ProductColours WHERE ProductId = {0};
            DELETE FROM catalog.ProductCategories WHERE ProductId = {0};
            DELETE FROM catalog.ProductStats WHERE ProductId = {0};
            DELETE FROM catalog.ProductSearchIndex WHERE ProductId = {0};
            DELETE FROM catalog.Products WHERE Id = {0};
            """,
            [productId],
            Token);
    }

    private static async Task HardDeleteAssetsAsync(ApiFactory factory, IReadOnlyList<Guid> publicIds)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var publicId in publicIds)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM media.MediaUsages
                WHERE MediaAssetId IN (SELECT Id FROM media.MediaAssets WHERE PublicId = {0});
                """,
                [publicId],
                Token);
        }

        // Videos before images, because CK_MediaAssets_VideoHasPoster will not let a video's PosterAssetId
        // be cleared and the poster is one of the images in this list. Deleting the video releases it.
        foreach (var isVideo in new[] { true, false })
        {
            foreach (var publicId in publicIds)
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "DELETE FROM media.MediaAssets WHERE PublicId = {0} AND MediaType = {1};",
                    [publicId, isVideo ? (byte)MediaAssetType.Video : (byte)MediaAssetType.Image],
                    Token);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories.Values)
        {
            await factory.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Real reference-data ids, read from the seeded database rather than assumed.</summary>
    private sealed record SeedIds(
        int DepartmentId,
        int CategoryId,
        int ExtraCategoryId,
        int ColourFamilyId,
        IReadOnlyList<int> SizeIds,
        int MaterialId);
}
