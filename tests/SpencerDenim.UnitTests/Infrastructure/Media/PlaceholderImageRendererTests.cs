using SkiaSharp;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Media;
using SpencerDenim.Infrastructure.Persistence.Seeding;

namespace SpencerDenim.UnitTests.Infrastructure.Media;

/// <summary>
/// What the demo placeholders look like, and the property that matters most: <b>two different frames are
/// two different images</b>, with no font involved.
/// <para>
/// Almost every test here renders with <c>font: null</c> — the fontless path — because that is the
/// deployment target (a Linux container with no fontconfig and no font file) and it is where the real
/// defect lived: the first version of the renderer put the whole difference between frames in the text, so
/// on that host every placeholder encoded to identical bytes, hashed to one SHA-256, and
/// <c>UX_MediaAssets_Checksum_Ready</c> correctly refused the second one. Seeding died and took most of the
/// integration suite with it. Windows could never have caught it, because a font is always there; the
/// Linux CI job did.
/// </para>
/// </summary>
public sealed class PlaceholderImageRendererTests
{
    private static PlaceholderSubject Subject(
        string identity = "public/originals/demo/wide-leg-rinse-jeans-1.jpg",
        string productName = "Wide-Leg Rinse Jeans",
        string? colourName = "Indigo Rinse",
        string swatchHex = "#3A5A8C") =>
        new(identity, productName, colourName, swatchHex);

    [Fact]
    public void A_placeholder_is_a_flat_neutral_block_at_the_catalogues_declared_size()
    {
        var subject = Subject();
        using var bitmap = PlaceholderImageRenderer.Render(subject, font: null);

        Assert.Equal(1200, bitmap.Width);
        Assert.Equal(1600, bitmap.Height);

        // Flat ground wherever the diagonals, the inset rule and the identity block are not: one colour,
        // and a near-neutral light one. Asserted against the ground this identity gets rather than one
        // hard-coded hex, because the tint is part of what makes the frame distinct.
        var ground = PlaceholderImageRenderer.GroundColourFor(subject.Identity);

        Assert.Equal(ground, bitmap.GetPixel(300, 60));
        Assert.Equal(ground, bitmap.GetPixel(bitmap.Width - 300, 60));
        Assert.Equal(ground, bitmap.GetPixel(300, bitmap.Height - 60));
        Assert.Equal(ground, bitmap.GetPixel(bitmap.Width - 300, bitmap.Height - 60));

        // Light and genuinely neutral. The tolerance is tight on purpose: the first version of the tint
        // moved each channel independently by up to seven points, which at these values is a visible green
        // or pink cast and makes the placeholder look like a colour choice rather than an absence.
        Assert.InRange(ground.Red, (byte)0xD0, (byte)0xFF);
        Assert.True(Math.Abs(ground.Red - ground.Green) <= 6, $"{ground} is not neutral");
        Assert.True(Math.Abs(ground.Green - ground.Blue) <= 10, $"{ground} is not neutral");
        Assert.True(Math.Abs(ground.Red - ground.Blue) <= 12, $"{ground} is not neutral");
    }

    /// <summary>
    /// The two diagonals are the universal "no image" mark, and they are the thing that makes a placeholder
    /// recognisable at thumbnail size, where words would be unreadable even if there were any.
    /// </summary>
    [Fact]
    public void The_no_image_diagonals_are_drawn()
    {
        using var bitmap = PlaceholderImageRenderer.Render(Subject(), font: null);

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
        using var indigo = PlaceholderImageRenderer.Render(
            Subject(identity: "same-identity", colourName: "Indigo Rinse", swatchHex: "#3A5A8C"),
            font: null);

        using var black = PlaceholderImageRenderer.Render(
            Subject(identity: "same-identity", colourName: "True Black", swatchHex: "#101010"),
            font: null);

        var centre = new SKPointI(indigo.Width / 2, (int)((indigo.Height / 2f) - (150 * (1600 / 1200f))));

        Assert.NotEqual(indigo.GetPixel(centre.X, centre.Y), black.GetPixel(centre.X, centre.Y));
    }

    /// <summary>
    /// The identity block: a grid of small neutral squares whose pattern comes from the identity. This
    /// asserts the pattern is actually painted, so the distinctness below cannot be coming from the ground
    /// tint alone.
    /// </summary>
    [Fact]
    public void An_identity_block_is_painted_and_its_pattern_follows_the_identity()
    {
        using var first = PlaceholderImageRenderer.Render(Subject(identity: "identity-one"), font: null);
        using var second = PlaceholderImageRenderer.Render(Subject(identity: "identity-two"), font: null);

        // The block occupies design y 850..1090 of 1200, which is output y 1133..1453 of 1600.
        var firstCells = CellPattern(first);
        var secondCells = CellPattern(second);

        Assert.Contains(true, firstCells);
        Assert.Contains(false, firstCells);
        Assert.NotEqual(firstCells, secondCells);
    }

    /// <summary>
    /// The regression test for the CI failure, stated as the thing that actually has to be true: the same
    /// product name, the same colourway, the same swatch — different identity, <b>no font at all</b> — and
    /// the encoded bytes still differ. Before the identity block they did not.
    /// </summary>
    [Fact]
    public void Two_frames_with_different_identities_encode_to_different_bytes_with_no_font()
    {
        var first = Encode(Subject(identity: "public/originals/demo/wide-leg-rinse-jeans-4.jpg"));
        var second = Encode(Subject(identity: "public/originals/demo/tapered-stretch-chinos-4.jpg"));

        Assert.NotEqual(Sha256(first), Sha256(second));
    }

    /// <summary>
    /// The shape the bug actually took: the shared fabric-detail frame. It has no colourway and the same
    /// fallback swatch on every product, so before the identity block all forty-odd of them were one image.
    /// </summary>
    [Fact]
    public void Two_products_detail_frames_are_different_images_with_no_font()
    {
        var first = Encode(new PlaceholderSubject("public/originals/demo/a-4.jpg", "Product A", null, "#BFB9B0"));
        var second = Encode(new PlaceholderSubject("public/originals/demo/b-4.jpg", "Product B", null, "#BFB9B0"));

        Assert.NotEqual(Sha256(first), Sha256(second));
    }

    /// <summary>
    /// And not just two: <b>every</b> frame in the real demo catalogue, rendered with no font, hashes to a
    /// distinct value. That is the seeding run's actual precondition, checked against the real data rather
    /// than argued from how many bits the pattern carries. Rendered small — the design is scaled, so it is
    /// the same picture — which keeps a hundred and fifty renders inside a second or two.
    /// </summary>
    [Fact]
    public void Every_frame_in_the_demo_catalogue_is_a_distinct_image_with_no_font()
    {
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, placeholder) in DemoMediaPlaceholders.ByStorageKey)
        {
            var subject = new PlaceholderSubject(
                key,
                placeholder.ProductName,
                placeholder.ColourName,
                placeholder.ColourHex);

            using var bitmap = PlaceholderImageRenderer.Render(subject, 300, 400, font: null);
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
            var hash = Sha256(data.ToArray());

            Assert.False(
                byHash.TryGetValue(hash, out var clash),
                $"'{key}' renders to the same bytes as '{clash}'; seeding would trip UX_MediaAssets_Checksum_Ready.");

            byHash[hash] = key;
        }

        Assert.Equal(DemoMediaPlaceholders.ByStorageKey.Count, byHash.Count);
    }

    /// <summary>The same identity twice is the same file, which is what makes the generator idempotent.</summary>
    [Fact]
    public void The_same_identity_renders_the_same_bytes_every_time()
    {
        Assert.Equal(Sha256(Encode(Subject())), Sha256(Encode(Subject())));
    }

    [Fact]
    public void An_unparseable_swatch_colour_falls_back_instead_of_throwing()
    {
        using var bitmap = PlaceholderImageRenderer.Render(
            Subject(productName: "Product", colourName: "Colour", swatchHex: "not-a-colour"),
            font: null);

        Assert.Equal(1200, bitmap.Width);
    }

    /// <summary>
    /// The label is an enhancement, and this pins it <b>where a font exists</b> rather than failing the
    /// build where one does not. On a fontless host the render is still exercised — it just has to come out
    /// as the fontless picture, which is the honest statement of what "no label" means.
    /// </summary>
    [Fact]
    public void A_label_is_drawn_when_a_font_is_available_and_nothing_breaks_when_it_is_not()
    {
        var subject = Subject();
        using var plain = PlaceholderImageRenderer.Render(subject, font: null);

        var font = HostFont();

        if (font is null)
        {
            // Nothing to assert about a label. What can be asserted is that the fontless path renders, and
            // renders the same thing twice, which is what the rest of the pipeline relies on.
            using var again = PlaceholderImageRenderer.Render(subject, font: null);

            Assert.Equal(1200, again.Width);
            Assert.Equal(Sha256(Encode(subject)), Sha256(Encode(subject)));
            return;
        }

        try
        {
            using var labelled = PlaceholderImageRenderer.Render(subject, font);

            // Ink appears across the text band, where the fontless render has nothing but flat ground.
            // Counted over the whole band rather than sampled on one row: where the glyphs fall inside it
            // depends on the typeface, and a row-sampled assertion would be measuring the font's metrics.
            var plainInk = InkInTextBand(plain);
            var labelledInk = InkInTextBand(labelled);

            Assert.Equal(0, plainInk);
            Assert.True(labelledInk > 200, $"the labelled render has only {labelledInk} ink pixels in the text band");
        }
        finally
        {
            font.Regular.Dispose();

            if (!ReferenceEquals(font.Regular, font.Bold))
            {
                font.Bold.Dispose();
            }
        }
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

    // ---------------------------------------------------------------- helpers

    private static byte[] Encode(PlaceholderSubject subject)
    {
        using var bitmap = PlaceholderImageRenderer.Render(subject, font: null);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>
    /// Reads the forty cells of the identity block as filled-or-not, by sampling the middle of each one.
    /// </summary>
    private static bool[] CellPattern(SKBitmap bitmap)
    {
        const int columns = 10;
        const int rows = 4;

        var scaleX = bitmap.Width / 900f;
        var scaleY = bitmap.Height / 1200f;
        var pattern = new bool[columns * rows];
        var ground = bitmap.GetPixel((int)(450 * scaleX), (int)(820 * scaleY));

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var x = (int)((150 + (column * 60) + 30) * scaleX);
                var y = (int)((850 + (row * 60) + 30) * scaleY);
                pattern[(row * columns) + column] = Luminance(bitmap.GetPixel(x, y)) < Luminance(ground) - 8;
            }
        }

        return pattern;
    }

    /// <summary>
    /// A typeface from this host's font manager, or null where there is none. Deliberately not
    /// <c>PlaceholderTypefaceSource</c>: this asks whether the machine running the test has a font, not how
    /// the application resolves one.
    /// </summary>
    private static PlaceholderLabelFont? HostFont()
    {
        var typeface = SKFontManager.Default.MatchFamily("Arial", SKFontStyle.Normal)
            ?? SKFontManager.Default.MatchFamily("DejaVu Sans", SKFontStyle.Normal)
            ?? SKTypeface.Default;

        using (var probe = new SKFont(typeface, 40))
        {
            if (probe.MeasureText("PLACEHOLDER") <= 0)
            {
                typeface.Dispose();
                return null;
            }
        }

        return new PlaceholderLabelFont(typeface, typeface);
    }

    /// <summary>
    /// How many pixels in the text band are ink rather than ground.
    /// <para>
    /// The window is design x 60..380, y 400..805 of 900 × 1200: the left part of the flat band the
    /// renderer lays down behind the label. The diagonals are outside the band and the colourway swatch is
    /// a circle centred at x 450, so nothing in this window can darken it except a glyph — which is what
    /// makes "zero ink here" a meaningful statement about the fontless render.
    /// </para>
    /// </summary>
    private static int InkInTextBand(SKBitmap bitmap)
    {
        var scaleX = bitmap.Width / 900f;
        var scaleY = bitmap.Height / 1200f;

        var left = (int)(60 * scaleX);
        var right = (int)(380 * scaleX);
        var top = (int)(400 * scaleY);
        var bottom = (int)(805 * scaleY);

        var ground = Luminance(bitmap.GetPixel(left + 2, top + 2));
        var ink = 0;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                if (Luminance(bitmap.GetPixel(x, y)) < ground - 30)
                {
                    ink++;
                }
            }
        }

        return ink;
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
