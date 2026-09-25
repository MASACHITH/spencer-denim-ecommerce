using SkiaSharp;
using SpencerDenim.Application.Media;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Media;
using SpencerDenim.Infrastructure.Persistence.Seeding;

namespace SpencerDenim.UnitTests.Infrastructure.Media;

/// <summary>
/// The size ceilings of doc 08 §3.3 and the demo placeholders, which are the two things a reader is most
/// likely to get wrong by eye.
/// </summary>
public sealed class MediaOptionsTests
{
    [Fact]
    public void The_video_and_private_ceilings_are_the_ones_the_check_constraint_enforces()
    {
        Assert.Equal(50 * 1024 * 1024, MediaLimits.MaxVideoBytes);
        Assert.Equal(5 * 1024 * 1024, MediaLimits.MaxPrivateBytes);
    }

    [Fact]
    public void The_effective_ceiling_follows_the_visibility_and_the_type()
    {
        var options = new MediaOptions();

        Assert.Equal(options.MaxImageBytes, options.MaxBytesFor(MediaVisibility.Public, MediaAssetType.Image));
        Assert.Equal(MediaLimits.MaxVideoBytes, options.MaxBytesFor(MediaVisibility.Public, MediaAssetType.Video));
        Assert.Equal(MediaLimits.MaxPrivateBytes, options.MaxBytesFor(MediaVisibility.Private, MediaAssetType.Image));
        Assert.Equal(MediaLimits.MaxPrivateBytes, options.MaxBytesFor(MediaVisibility.Private, MediaAssetType.Document));
    }

    /// <summary>
    /// Configuration may lower a ceiling and must never raise it past the check constraint: the file would
    /// be written and the row then refused, which is the half-done state the pipeline exists to prevent.
    /// </summary>
    [Fact]
    public void Configuration_cannot_raise_a_ceiling_past_the_check_constraint()
    {
        var options = new MediaOptions
        {
            MaxVideoBytes = long.MaxValue,
            MaxPrivateBytes = long.MaxValue,
        };

        Assert.Equal(MediaLimits.MaxVideoBytes, options.MaxBytesFor(MediaVisibility.Public, MediaAssetType.Video));
        Assert.Equal(MediaLimits.MaxPrivateBytes, options.MaxBytesFor(MediaVisibility.Private, MediaAssetType.Image));
    }

    /// <summary>A video carries its poster in the same request, so the body ceiling has to cover both.</summary>
    [Fact]
    public void The_request_ceiling_leaves_room_for_a_video_and_its_poster()
    {
        var options = new MediaOptions();

        Assert.True(options.MaxRequestBytes > MediaLimits.MaxVideoBytes + options.MaxImageBytes - 1);
    }
}

/// <summary>
/// What the demo placeholders look like. The rule they exist under is that nobody — client, tester or
/// developer — could mistake one for a Spencer Denim photograph.
/// </summary>
public sealed class PlaceholderImageRendererTests
{
    [Fact]
    public void A_placeholder_is_a_flat_neutral_block_at_the_catalogues_declared_size()
    {
        using var bitmap = PlaceholderImageRenderer.Render("Wide-Leg Rinse Jeans", "Indigo Rinse", "#3A5A8C");

        Assert.Equal(1200, bitmap.Width);
        Assert.Equal(1600, bitmap.Height);

        // Flat warm grey wherever the two diagonals and the inset rule are not: nothing photographic.
        // (The very corners sit on the diagonals, which is the point of them.)
        var ground = new SKColor(0xE8, 0xE6, 0xE1);
        Assert.Equal(ground, bitmap.GetPixel(300, 60));
        Assert.Equal(ground, bitmap.GetPixel(bitmap.Width - 300, 60));
        Assert.Equal(ground, bitmap.GetPixel(300, bitmap.Height - 60));
        Assert.Equal(ground, bitmap.GetPixel(bitmap.Width - 300, bitmap.Height - 60));
    }

    /// <summary>
    /// The two diagonals are the universal "no image" mark, and they are the thing that makes a placeholder
    /// recognisable at thumbnail size where the words are unreadable.
    /// </summary>
    [Fact]
    public void The_no_image_diagonals_are_drawn()
    {
        using var bitmap = PlaceholderImageRenderer.Render("Wide-Leg Rinse Jeans", "Indigo Rinse", "#3A5A8C");

        // Both diagonals, sampled well clear of the text band. y = x * 4/3 on one, the mirror on the other.
        var topLeftToBottomRight = SampleDarkest(bitmap, 120, 160, radius: 6);
        var topRightToBottomLeft = SampleDarkest(bitmap, bitmap.Width - 120, 160, radius: 6);
        var ground = bitmap.GetPixel(600, 160);

        Assert.True(
            Luminance(topLeftToBottomRight) < Luminance(ground) - 8,
            $"the first diagonal should be darker than the ground: {topLeftToBottomRight} vs {ground}");
        Assert.True(
            Luminance(topRightToBottomLeft) < Luminance(ground) - 8,
            $"the second diagonal should be darker than the ground: {topRightToBottomLeft} vs {ground}");
    }

    [Fact]
    public void The_colourway_swatch_is_painted_so_two_frames_of_one_product_differ()
    {
        using var indigo = PlaceholderImageRenderer.Render("Wide-Leg Rinse Jeans", "Indigo Rinse", "#3A5A8C");
        using var black = PlaceholderImageRenderer.Render("Wide-Leg Rinse Jeans", "True Black", "#101010");

        var centre = new SKPointI(indigo.Width / 2, (int)((indigo.Height / 2f) - (150 * (1600 / 1200f))));

        Assert.NotEqual(indigo.GetPixel(centre.X, centre.Y), black.GetPixel(centre.X, centre.Y));
    }

    [Fact]
    public void An_unparseable_swatch_colour_falls_back_instead_of_throwing()
    {
        using var bitmap = PlaceholderImageRenderer.Render("Product", "Colour", "not-a-colour");

        Assert.Equal(1200, bitmap.Width);
    }

    [Fact]
    public void The_renderer_can_draw_text_on_this_host()
    {
        // Not an assertion about the product so much as a guard on the test below: where this is false the
        // placeholders carry no labels, and the seeder logs that rather than pretending otherwise.
        Assert.True(
            PlaceholderImageRenderer.CanDrawText,
            "no usable font on this machine; the labelled-placeholder assertions cannot be meaningful here");
    }

    /// <summary>Every demo asset gets alt text that says it is a placeholder (doc 08 §3.3 wants alt text).</summary>
    [Fact]
    public void The_alt_text_says_it_is_a_placeholder()
    {
        var colourway = DemoMediaPlaceholders.AltTextFor("Wide-Leg Rinse Jeans", "Indigo Rinse");
        var detail = DemoMediaPlaceholders.AltTextFor("Wide-Leg Rinse Jeans", colourName: null);

        Assert.StartsWith("Placeholder image", colourway, StringComparison.Ordinal);
        Assert.Contains("Indigo Rinse", colourway, StringComparison.Ordinal);
        Assert.StartsWith("Placeholder image", detail, StringComparison.Ordinal);

        // The column is nvarchar(250).
        Assert.True(colourway.Length <= 250);
        Assert.True(detail.Length <= 250);
    }

    [Fact]
    public void Every_demo_key_is_a_valid_public_storage_key_with_a_placeholder_behind_it()
    {
        Assert.NotEmpty(DemoMediaPlaceholders.ByStorageKey);

        foreach (var (key, placeholder) in DemoMediaPlaceholders.ByStorageKey)
        {
            Assert.True(MediaStorageKey.IsPublic(key), key);
            Assert.StartsWith(DemoMediaPlaceholders.StorageKeyPrefix, key, StringComparison.Ordinal);
            Assert.StartsWith("Placeholder image", placeholder.AltText, StringComparison.Ordinal);
            Assert.True(placeholder.AltText.Length <= 250);
        }
    }

    private static SKColor SampleDarkest(SKBitmap bitmap, int x, int y, int radius)
    {
        var darkest = bitmap.GetPixel(x, y);

        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                var pixel = bitmap.GetPixel(x + dx, y + dy);
                if (Luminance(pixel) < Luminance(darkest))
                {
                    darkest = pixel;
                }
            }
        }

        return darkest;
    }

    private static double Luminance(SKColor colour) =>
        (0.2126 * colour.Red) + (0.7152 * colour.Green) + (0.0722 * colour.Blue);
}
