using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Catalog;
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
            // file — the one choice a srcset must not be allowed to make on the shopper's behalf.
            Assert.EndsWith("/zoom.webp", image.ZoomUrl, StringComparison.Ordinal);

            Assert.DoesNotContain(Originals, image.SrcSet, StringComparison.Ordinal);
            Assert.Contains("/large.webp 1200w", image.SrcSet, StringComparison.Ordinal);

            // Not "/zoom.webp": the seeded placeholders are 1200 px, so the zoom rendition is the same
            // pixels re-encoded and the builder drops it from the candidates rather than invite the
            // browser to spend bytes on no extra detail. MediaUrlBuilderTests pins both halves of that
            // rule on widths the seed does not happen to produce.
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
