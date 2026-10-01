using SkiaSharp;
using SpencerDenim.Infrastructure.Media.ClientAssets;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.UnitTests.Infrastructure.Media;

/// <summary>
/// Whether a logo file can carry a transparent background, read from its header.
/// <para>
/// One sentence in the import report hangs on this, and it has to be right in both directions: a
/// wordmark wrongly reported as opaque sends a designer looking for a problem that is not there, and one
/// wrongly reported as fine is the defect this check exists to catch. So "I do not know" is a real
/// answer here, and the cases that cannot be settled from a header return it rather than guessing.
/// </para>
/// </summary>
public sealed class ImageTransparencyTests : IDisposable
{
    /// <summary>One directory per instance, removed on dispose: a test that litters the host is a defect.</summary>
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sd-transparency-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_jpeg_can_never_be_transparent_whatever_it_is_called()
    {
        // The delivered case exactly: a JPEG carrying a .png name. The media pipeline reads the
        // signature and stores it as image/jpeg, which is why nothing in the report said so before.
        var path = WriteTemp("wordmark-dark.png", TestImages.Jpeg(64, 64));

        Assert.False(ImageTransparency.Of(path));
        Assert.Contains("JPEG", ImageTransparency.Describe(path), StringComparison.Ordinal);
    }

    [Fact]
    public void An_rgba_png_is_transparent_and_an_rgb_one_is_not()
    {
        var withAlpha = WriteTemp("alpha.png", Png(SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.True(ImageTransparency.Of(withAlpha));

        // Skia writes an RGB PNG (colour type 2) for an opaque bitmap.
        var opaque = WriteTemp("opaque.png", Png(SKColorType.Rgb888x, SKAlphaType.Opaque));
        Assert.False(ImageTransparency.Of(opaque));
    }

    [Fact]
    public void Anything_it_cannot_settle_from_a_header_says_so_rather_than_guessing()
    {
        Assert.Null(ImageTransparency.Of(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 }));
        Assert.Null(ImageTransparency.Of(WriteTemp("nothing.bin", [0x00, 0x01, 0x02, 0x03])));

        // A file that is not there is unknown, not false: a missing file is not a claim about alpha.
        Assert.Null(ImageTransparency.Of(Path.Combine(Path.GetTempPath(), $"sd-missing-{Guid.NewGuid():N}.png")));
    }

    [Fact]
    public void A_png_signature_is_recognised_for_what_it_is()
    {
        Assert.True(ImageTransparency.LooksLikePng(Png(SKColorType.Rgba8888, SKAlphaType.Unpremul)));
        Assert.False(ImageTransparency.LooksLikePng(TestImages.Jpeg(16, 16)));
    }

    private static byte[] Png(SKColorType colourType, SKAlphaType alphaType)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(8, 8, colourType, alphaType));
        bitmap.Erase(alphaType == SKAlphaType.Opaque ? SKColors.Teal : SKColors.Teal.WithAlpha(128));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private string WriteTemp(string name, byte[] content)
    {
        Directory.CreateDirectory(_root);

        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
