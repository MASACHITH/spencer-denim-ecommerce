using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using SpencerDenim.Domain.Branding;

namespace SpencerDenim.Infrastructure.Media;

/// <summary>What one placeholder stands in for.</summary>
/// <param name="Identity">
/// The string that makes this frame's pixels its own — the asset's storage key, which carries a unique
/// index in the database. See <see cref="PlaceholderImageRenderer"/> for why that matters.
/// </param>
/// <param name="ProductName">Printed under the heading, when a font is available.</param>
/// <param name="ColourName">The colourway, or null for the shared fabric-detail frame.</param>
/// <param name="SwatchHex">A <c>#rrggbb</c> colour for the dot; anything unparseable falls back to grey.</param>
public sealed record PlaceholderSubject(string Identity, string ProductName, string? ColourName, string SwatchHex);

/// <summary>The typefaces to label a placeholder with, when the deployment has any.</summary>
/// <param name="Regular">Body weight.</param>
/// <param name="Bold">Heading weight; may be the same file.</param>
public sealed record PlaceholderLabelFont(SKTypeface Regular, SKTypeface Bold);

/// <summary>
/// Draws the demo catalogue's stand-in images.
/// <para>
/// <b>What one looks like.</b> A flat, near-neutral 3:4 block in the warm grey family, a thin inset rule,
/// and two faint diagonals corner to corner — the universal "there is no picture here" mark. Centred on
/// it: a dot filled with the colourway's swatch colour, and, when the deployment has a font, the word
/// <b>PLACEHOLDER</b>, the line <i>NO PRODUCT PHOTOGRAPH</i>, the product's name and the colourway's name.
/// Across the lower third, a grid of small neutral squares — an identity block, described below. Along the
/// bottom: <i>&lt;the store's name&gt; — demo data, not a real product image</i>. There is no garment, no model, no
/// texture and no photographic content of any kind, because none has been supplied and inventing some
/// would be worse than a grey box.
/// </para>
/// <para>
/// <b>The identity block, and why it exists.</b> The ground tint and the pattern of filled cells are
/// derived from a SHA-256 of <see cref="PlaceholderSubject.Identity"/>, so two different frames are
/// different <i>images</i> — different bytes — with no glyph drawn at all. That is not decoration. The
/// first version of this class put the difference between frames entirely in the text, so on a host with
/// no font every product's placeholder encoded to byte-identical JPEG, every one hashed to the same
/// SHA-256, and <c>UX_MediaAssets_Checksum_Ready</c> correctly refused the second one — seeding died and
/// took most of the integration suite with it. The index was right; two different products producing one
/// image was the bug. It was caught by the Linux CI job and could not be caught on Windows, where a font
/// is always there.
/// </para>
/// <para>
/// <b>No host font is required.</b> Doc 03 §7: "no code may depend on the host." A Linux container built
/// on <c>SkiaSharp.NativeAssets.Linux.NoDependencies</c> has no fontconfig and may have no font file at
/// all, which is the deployment target, not a test-environment quirk — and Skia answers a missing font by
/// drawing nothing rather than by failing. So the label is an <b>enhancement</b>: the caller passes a
/// <see cref="PlaceholderLabelFont"/> when one could be resolved (see
/// <see cref="PlaceholderTypefaceSource"/>, which reads <c>Media:PlaceholderFontPath</c> first and the
/// host's font manager second), and passes null otherwise. Nothing about the image's distinctness, its
/// unmistakability or its correctness depends on the answer.
/// </para>
/// </summary>
public static class PlaceholderImageRenderer
{
    /// <summary>
    /// Width of a generated placeholder. 3:4, which is the aspect ratio the storefront reserves, and the
    /// size the catalogue seeder records on the row before the file exists.
    /// </summary>
    public const int Width = 1200;

    /// <summary>Height of a generated placeholder.</summary>
    public const int Height = 1600;

    /// <summary>
    /// The layout is written once in this coordinate space and the canvas is scaled to the requested
    /// output size, so changing that size does not mean re-tuning every font size and offset — and a test
    /// can render the same design small and cheap.
    /// </summary>
    private const float DesignWidth = 900f;

    private const float DesignHeight = 1200f;

    /// <summary>Columns in the identity block.</summary>
    private const int IdentityColumns = 10;

    /// <summary>Rows in the identity block. Ten by four is forty cells, so forty bits of pattern.</summary>
    private const int IdentityRows = 4;

    private static readonly SKColor BaseGround = new(0xE8, 0xE6, 0xE1);
    private static readonly SKColor Rule = new(0xC7, 0xC1, 0xB8);
    private static readonly SKColor Diagonal = new(0xD8, 0xD3, 0xCB);
    private static readonly SKColor IdentityInk = new(0xB4, 0xAE, 0xA5);
    private static readonly SKColor StrongInk = new(0x4A, 0x45, 0x3E);
    private static readonly SKColor Ink = new(0x6B, 0x66, 0x5E);
    private static readonly SKColor FaintInk = new(0x8C, 0x86, 0x7D);

    /// <summary>
    /// How far the ground may move in <b>lightness</b> — the same shift in all three channels, so the card
    /// gets a little paler or a little deeper without acquiring a colour.
    /// </summary>
    private const int GroundLightnessRange = 9;

    /// <summary>
    /// How far a single channel may then wander on its own. Deliberately tiny: an independent ±7 per
    /// channel, which is what the first version did, puts up to fourteen points between red and green and
    /// at these light values that is a visible green or pink cast — a placeholder that looks like a
    /// colour choice rather than an absence. The pattern below carries the entropy; this only has to move
    /// the bytes.
    /// </summary>
    private const int GroundChannelWobble = 2;

    /// <summary>Draws one placeholder at the catalogue's declared size. The caller disposes the bitmap.</summary>
    /// <param name="subject">What the frame stands in for.</param>
    /// <param name="font">Typefaces for the label, or null on a deployment that has none.</param>
    public static SKBitmap Render(PlaceholderSubject subject, PlaceholderLabelFont? font = null) =>
        Render(subject, Width, Height, font);

    /// <summary>
    /// Draws one placeholder at an explicit size. The design is scaled, so a smaller render is the same
    /// picture — which is what lets a test check every frame in the demo catalogue for distinctness
    /// without paying for a hundred and fifty full-size encodes.
    /// </summary>
    public static SKBitmap Render(PlaceholderSubject subject, int width, int height, PlaceholderLabelFont? font)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(subject.Identity));
        var ground = TintGround(hash);

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(ground);
            canvas.Scale(width / DesignWidth, height / DesignHeight);

            using var line = new SKPaint { Color = Diagonal, IsAntialias = true, StrokeWidth = 2, Style = SKPaintStyle.Stroke };
            canvas.DrawLine(0, 0, DesignWidth, DesignHeight, line);
            canvas.DrawLine(DesignWidth, 0, 0, DesignHeight, line);

            using var rule = new SKPaint { Color = Rule, IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
            canvas.DrawRect(new SKRect(24, 24, DesignWidth - 24, DesignHeight - 24), rule);

            // Bands of flat ground behind the text and the identity block, so the diagonals do not run
            // through either and the block's cells sit on a known colour.
            using var band = new SKPaint { Color = ground, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawRect(new SKRect(48, (DesignHeight / 2f) - 210, DesignWidth - 48, (DesignHeight / 2f) + 210), band);
            canvas.DrawRect(new SKRect(130, 830, DesignWidth - 130, 1100), band);

            DrawSwatch(canvas, subject.SwatchHex);
            DrawIdentityBlock(canvas, hash);

            if (font is not null)
            {
                DrawLabel(canvas, subject, font);
            }

            return bitmap;
        }
        catch (Exception)
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The ground colour this identity gets: the base warm grey, nudged a few points per channel. Public
    /// so a test can assert the flat areas of a render without hard-coding one hex value.
    /// </summary>
    public static SKColor GroundColourFor(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return TintGround(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static SKColor TintGround(byte[] hash)
    {
        var lightness = (hash[0] % ((GroundLightnessRange * 2) + 1)) - GroundLightnessRange;

        return new SKColor(
            Shift(BaseGround.Red, lightness, hash[1]),
            Shift(BaseGround.Green, lightness, hash[2]),
            Shift(BaseGround.Blue, lightness, hash[3]));
    }

    private static byte Shift(byte channel, int lightness, byte entropy)
    {
        var wobble = (entropy % ((GroundChannelWobble * 2) + 1)) - GroundChannelWobble;
        return (byte)Math.Clamp(channel + lightness + wobble, 0, 255);
    }

    /// <summary>
    /// Forty cells, filled or not from forty bits of the hash. Squares, in one flat neutral ink: a
    /// deliberate machine-made marker, which is exactly the impression a placeholder should give.
    /// </summary>
    private static void DrawIdentityBlock(SKCanvas canvas, byte[] hash)
    {
        const float left = 150f;
        const float top = 850f;
        const float cell = 60f;
        const float inset = 10f;

        using var fill = new SKPaint { Color = IdentityInk, IsAntialias = true, Style = SKPaintStyle.Fill };

        for (var row = 0; row < IdentityRows; row++)
        {
            for (var column = 0; column < IdentityColumns; column++)
            {
                var index = (row * IdentityColumns) + column;
                if (!IsSet(hash, index))
                {
                    continue;
                }

                var x = left + (column * cell);
                var y = top + (row * cell);
                canvas.DrawRect(new SKRect(x + inset, y + inset, x + cell - inset, y + cell - inset), fill);
            }
        }
    }

    /// <summary>Bit <paramref name="index"/> of the hash, starting at byte 4 so the tint bytes are not reused.</summary>
    private static bool IsSet(byte[] hash, int index) =>
        ((hash[4 + (index / 8)] >> (index % 8)) & 1) == 1;

    private static void DrawSwatch(SKCanvas canvas, string swatchHex)
    {
        var colour = SKColor.TryParse(swatchHex, out var parsed) ? parsed : new SKColor(0xBF, 0xB9, 0xB0);

        using var fill = new SKPaint { Color = colour, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var outline = new SKPaint { Color = Rule, IsAntialias = true, StrokeWidth = 2, Style = SKPaintStyle.Stroke };

        var centre = new SKPoint(DesignWidth / 2f, (DesignHeight / 2f) - 150);
        canvas.DrawCircle(centre, 34, fill);
        canvas.DrawCircle(centre, 34, outline);
    }

    private static void DrawLabel(SKCanvas canvas, PlaceholderSubject subject, PlaceholderLabelFont font)
    {
        using var strong = new SKPaint { Color = StrongInk, IsAntialias = true };
        using var normal = new SKPaint { Color = Ink, IsAntialias = true };
        using var faint = new SKPaint { Color = FaintInk, IsAntialias = true };

        var middle = DesignHeight / 2f;

        Write(canvas, "PLACEHOLDER", DesignWidth / 2f, middle - 60, new SKFont(font.Bold, 62), strong);
        Write(canvas, "NO PRODUCT PHOTOGRAPH", DesignWidth / 2f, middle - 10, new SKFont(font.Regular, 26), normal);
        Write(canvas, Shorten(subject.ProductName, 34), DesignWidth / 2f, middle + 70, new SKFont(font.Bold, 34), strong);
        Write(
            canvas,
            subject.ColourName is null ? "Fabric detail frame" : Shorten(subject.ColourName, 34),
            DesignWidth / 2f,
            middle + 120,
            new SKFont(font.Regular, 30),
            normal);
        Write(
            canvas,
            $"{StoreBrand.Name} — demo data, not a real product image",
            DesignWidth / 2f,
            DesignHeight - 70,
            new SKFont(font.Regular, 22),
            faint);
    }

    private static void Write(SKCanvas canvas, string text, float x, float y, SKFont font, SKPaint paint)
    {
        using (font)
        {
            canvas.DrawText(text, x, y, SKTextAlign.Center, font, paint);
        }
    }

    private static string Shorten(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}
