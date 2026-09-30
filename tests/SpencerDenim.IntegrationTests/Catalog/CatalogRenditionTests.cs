using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Catalog;

/// <summary>
/// The catalogue API links the rendition each slot needs, against the real seeded catalogue on SQL
/// Server with a real media volume behind it (doc 03 §3b, W1-04).
/// <para>
/// This is the regression suite for a gap that shipped: <c>DemoMediaPlaceholderGenerator</c> wrote 596
/// WebP renditions, <c>/media</c> served them with a year's cache, and not one payload pointed at them.
/// A listing page carried 32 full-size originals — about 1,780 KB where the card renditions were 330 KB
/// on disk the whole time. Every assertion below fails against that build.
/// </para>
/// <para>
/// The assertions are deliberately about the <i>shape</i> of the URL rather than a byte count: a test
/// that measured kilobytes would pass on a build that linked <c>zoom</c> everywhere, and the thing that
/// actually has to stay true is that each consumer names the size it draws.
/// </para>
/// </summary>
public sealed class CatalogRenditionTests(SqlServerDatabaseFixture sqlServer) : CatalogTestBase(sqlServer)
{
    private const string Renditions = "/renditions/";
    private const string Originals = "/originals/";

    [Fact]
    public async Task A_listing_links_the_card_rendition_for_every_product_photo()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?pageSize=24", Token);

        var images = page.Items
            .Select(item => item.ImageUrl)
            .Where(url => url is not null)
            .ToList();

        // The seeded catalogue gives every demo product a card photo; an empty list here would make
        // the assertions below vacuous and the regression invisible again.
        Assert.NotEmpty(images);

        Assert.All(images, url =>
        {
            Assert.Contains(Renditions, url, StringComparison.Ordinal);
            Assert.EndsWith("/card.webp", url, StringComparison.Ordinal);
            Assert.DoesNotContain(Originals, url, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_listing_hover_photo_takes_the_same_rendition_as_the_card()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?pageSize=24", Token);

        var hovers = page.Items
            .Select(item => item.HoverImageUrl)
            .Where(url => url is not null)
            .ToList();

        Assert.NotEmpty(hovers);
        Assert.All(hovers, url => Assert.EndsWith("/card.webp", url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_card_offers_a_srcset_of_renditions_and_never_the_zoom()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?pageSize=24", Token);

        var srcSets = page.Items
            .Select(item => item.ImageSrcSet)
            .Where(srcSet => srcSet is not null)
            .ToList();

        Assert.NotEmpty(srcSets);

        Assert.All(srcSets, srcSet =>
        {
            Assert.DoesNotContain(Originals, srcSet, StringComparison.Ordinal);

            // A card is a column of a grid on every breakpoint, so the 2000 px file is never its answer.
            Assert.DoesNotContain("/zoom.webp", srcSet, StringComparison.Ordinal);

            var candidates = srcSet!.Split(", ", StringSplitOptions.RemoveEmptyEntries);
            Assert.True(candidates.Length > 1, $"A one-candidate srcset is not a choice: '{srcSet}'.");

            Assert.All(candidates, candidate =>
            {
                var parts = candidate.Split(' ');
                Assert.Equal(2, parts.Length);
                Assert.Contains(Renditions, parts[0], StringComparison.Ordinal);
                Assert.EndsWith("w", parts[1], StringComparison.Ordinal);
                Assert.True(int.TryParse(parts[1][..^1], out var width) && width > 0, candidate);
            });
        });
    }

    [Fact]
    public async Task A_colour_swatch_takes_the_thumb_rendition()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?pageSize=24", Token);

        var swatches = page.Items
            .SelectMany(item => item.Colours)
            .Select(colour => colour.SwatchImageUrl)
            .Where(url => url is not null)
            .ToList();

        // A swatch that the seed left as a hex code only is fine; one that exists must be the small file.
        Assert.All(swatches, url => Assert.EndsWith("/thumb.webp", url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_product_gallery_asks_for_large_on_the_stage_thumb_on_the_rail_and_zoom_in_the_viewer()
    {
        var api = CreateApi();

        var slug = (await api.ListAsync("?pageSize=1", Token)).Items.Single().Slug;
        var product = await api.GetProductAsync(slug, Token);

        var photos = product.Images.Where(image => image.Role != ProductMediaRole.Video).ToList();
        Assert.NotEmpty(photos);

        Assert.All(photos, image =>
        {
            Assert.EndsWith("/large.webp", image.Url, StringComparison.Ordinal);
            Assert.DoesNotContain(Originals, image.Url, StringComparison.Ordinal);

            Assert.EndsWith("/thumb.webp", image.ThumbnailUrl, StringComparison.Ordinal);

            // The viewer is opened deliberately and magnifies past 1x, so it always names the largest
            // file that exists — the one choice a srcset must not be allowed to make on the shopper's
            // behalf. For the seeded placeholders, which are 1200 px, the largest that exists is
            // `large`: `zoom` would be the same pixels, so the encoder never wrote it and the URL
            // builder clamps to what it did. It is emphatically still a rendition, not the original.
            Assert.Contains(Renditions, image.ZoomUrl, StringComparison.Ordinal);
            Assert.EndsWith("/large.webp", image.ZoomUrl, StringComparison.Ordinal);

            Assert.DoesNotContain(Originals, image.SrcSet, StringComparison.Ordinal);
            Assert.Contains("/large.webp 1200w", image.SrcSet, StringComparison.Ordinal);
            Assert.DoesNotContain("/zoom.webp", image.SrcSet, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_department_hero_and_a_category_tile_take_the_sizes_their_layouts_draw()
    {
        var api = CreateApi();

        var departments = await api.GetDepartmentsAsync(Token);

        var heroes = departments
            .Select(department => department.HeroImageUrl)
            .Where(url => url is not null)
            .ToList();

        var tiles = departments
            .SelectMany(department => department.Categories)
            .Select(category => category.ImageUrl)
            .Where(url => url is not null)
            .ToList();

        // A banner is full-bleed and a tile is a grid cell, which is the whole reason they differ.
        Assert.All(heroes, url => Assert.EndsWith("/large.webp", url, StringComparison.Ordinal));
        Assert.All(tiles, url => Assert.EndsWith("/card.webp", url, StringComparison.Ordinal));
    }

    /// <summary>
    /// A card links a file that exists, or it links nothing.
    /// <para>
    /// The global query filter hides a soft-deleted asset, so a deleted card image always came back as
    /// <see langword="null"/> — but it says nothing about <c>Status</c>, and the rendition projections
    /// originally carried no status guard. A row left <c>Failed</c> by a processing error therefore
    /// produced a perfectly formed rendition URL for a file that was never written: a broken image on
    /// the listing, where the same row soft-deleted produced a clean empty frame. Two ways for an asset
    /// to have no file, two different answers. <c>HomeContentReader</c> had the guard from the start,
    /// which is what made the asymmetry visible.
    /// </para>
    /// <para>
    /// The seeded catalogue is changed and put back in a <c>finally</c>, the way
    /// <c>CatalogVisibilityTests</c> does; the assembly runs its classes sequentially, so nothing else
    /// observes the half-applied state.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_asset_that_is_not_ready_is_linked_no_differently_from_one_that_is_deleted()
    {
        var api = CreateApi();

        const string Slug = "relaxed-linen-blend-shirt";

        var before = (await api.ListAsync("?pageSize=96", Token)).Items.Single(item => item.Slug == Slug);
        Assert.NotNull(before.ImageUrl);
        Assert.Contains(Renditions, before.ImageUrl, StringComparison.Ordinal);

        await SetCardImageStatusAsync(Slug, MediaAssetStatus.Failed);
        try
        {
            var card = (await api.ListAsync("?pageSize=96", Token)).Items.Single(item => item.Slug == Slug);

            // Not a rendition URL, and emphatically not the original either: there is no file.
            Assert.Null(card.ImageUrl);
            Assert.Null(card.ImageSrcSet);
        }
        finally
        {
            await SetCardImageStatusAsync(Slug, MediaAssetStatus.Ready);
        }

        var restored = (await api.ListAsync("?pageSize=96", Token)).Items.Single(item => item.Slug == Slug);
        Assert.Equal(before.ImageUrl, restored.ImageUrl);
        Assert.Equal(before.ImageSrcSet, restored.ImageSrcSet);
    }

    /// <summary>
    /// The same rule on the product page, where an unusable slide is worse than a missing one: it would
    /// still take a thumbnail in the rail and a stop in the keyboard order (PDP-01).
    /// </summary>
    [Fact]
    public async Task A_gallery_drops_a_slide_whose_file_was_never_written()
    {
        var api = CreateApi();

        const string Slug = "relaxed-linen-blend-shirt";

        var before = await api.GetProductAsync(Slug, Token);
        Assert.NotEmpty(before.Images);

        await SetGalleryImageStatusAsync(Slug, MediaAssetStatus.Processing);
        try
        {
            var product = await api.GetProductAsync(Slug, Token);

            Assert.Equal(before.Images.Count - 1, product.Images.Count);
            Assert.All(product.Images, image => Assert.Contains(Renditions, image.Url, StringComparison.Ordinal));
        }
        finally
        {
            await SetGalleryImageStatusAsync(Slug, MediaAssetStatus.Ready);
        }

        var restored = await api.GetProductAsync(Slug, Token);
        Assert.Equal(before.Images.Count, restored.Images.Count);
    }

    private async Task SetCardImageStatusAsync(string slug, MediaAssetStatus status)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var assetId = await dbContext.Products
            .Where(product => product.Slug == slug)
            .Select(product => product.CardImageAssetId)
            .SingleAsync(Token);

        await dbContext.MediaAssets
            .Where(asset => asset.Id == assetId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(asset => asset.Status, status), Token);
    }

    private async Task SetGalleryImageStatusAsync(string slug, MediaAssetStatus status)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var assetId = await dbContext.ProductMedia
            .Where(media => media.Product!.Slug == slug)
            .OrderBy(media => media.SortOrder)
            .ThenBy(media => media.Id)
            .Select(media => media.MediaAssetId)
            .FirstAsync(Token);

        await dbContext.MediaAssets
            .Where(asset => asset.Id == assetId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(asset => asset.Status, status), Token);
    }

    [Fact]
    public async Task Nothing_a_listing_page_links_is_an_original()
    {
        var api = CreateApi();

        var page = await api.ListAsync("?pageSize=24", Token);

        // The blunt version of the bug: one assertion over every URL the page carries, so a slot added
        // later cannot quietly go back to serving the full-size file.
        var everyUrl = page.Items
            .SelectMany(item => new[] { item.ImageUrl, item.HoverImageUrl, item.ImageSrcSet }
                .Concat(item.Colours.Select(colour => colour.SwatchImageUrl)))
            .Where(url => url is not null)
            .ToList();

        Assert.NotEmpty(everyUrl);
        Assert.All(everyUrl, url => Assert.DoesNotContain(Originals, url, StringComparison.Ordinal));
    }
}
