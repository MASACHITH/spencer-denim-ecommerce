using SkiaSharp;

namespace SpencerDenim.Infrastructure.Media;

/// <summary>
/// Draws the demo catalogue's stand-in images.
/// <para>
/// <b>What one looks like.</b> A flat warm-grey 3:4 block (<c>#E8E6E1</c>), a thin inset rule, and two
/// faint diagonals corner to corner — the universal "there is no picture here" mark. Centred on it, in
/// grey: the word <b>PLACEHOLDER</b>, the line <i>NO PRODUCT PHOTOGRAPH</i>, the product's name, the
/// colourway's name, and a small dot filled with that colourway's swatch colour so two frames of one
/// product are not identical. Along the bottom: <i>Spencer Denim — demo data, not a real product image</i>.
/// There is no garment, no model, no texture and no photographic content of any kind, because none has
/// been supplied and inventing some would be worse than a grey box.
/// </para>
/// <para>
/// <b>Fonts.</b> The text is the point, so the renderer checks that a typeface actually measures before
/// it claims to have drawn any: a Linux container built on
/// <c>SkiaSharp.NativeAssets.Linux.NoDependencies</c> has no fontconfig and may have no font at all, and
/// Skia answers that by drawing nothing rather than by failing. <see cref="CanDrawText"/> says whether
/// this host can, so the seeder can log what to install instead of writing out a hundred blank rectangles
/// and calling them labelled.
/// </para>
/// </summary>
public static class PlaceholderImageRenderer
{
    /// <summary>Width of a generated placeholder. 3:4, which is the aspect ratio the storefront reserves.</summary>
    public const int Width = 900;

    /// <summary>Height of a generated placeholder.</summary>
    public const int Height = 1200;

    private static readonly SKColor Background = new(0xE8, 0xE6, 0xE1);
    private static readonly SKColor Rule = new(0xC7, 0xC1, 0xB8);
    private static readonly SKColor Diagonal = new(0xD8, 0xD3, 0xCB);
    private static readonly SKColor StrongInk = new(0x4A, 0x45, 0x3E);
    private static readonly SKColor Ink = new(0x6B, 0x66, 0x5E);
    private static readonly SKColor FaintInk = new(0x8C, 0x86, 0x7D);

    /// <summary>Candidate families, in the order they are tried, before Skia's own default.</summary>
    private static readonly string[] FontCandidates =
        ["Arial", "Helvetica", "Liberation Sans", "DejaVu Sans", "Noto Sans", "Segoe UI"];

    /// <summary>
    /// Whether this host has a typeface that actually draws. False on a minimal Linux image with no fonts
    /// installed; the placeholders are then blocks and a swatch with no words on them, and the seeder says
    /// so in the log.
    /// </summary>
    public static bool CanDrawText
    {
        get
        {
            using var typeface = ResolveTypeface(bold: false);
            using var font = new SKFont(typeface, 40);
            return font.MeasureText("PLACEHOLDER") > 0;
        }
    }

    /// <summary>Draws one placeholder. The caller disposes the bitmap.</summary>
    /// <param name="productName">Printed under the heading.</param>
    /// <param name="colourName">The colourway, or null for the shared detail frame.</param>
    /// <param name="swatchHex">A <c>#rrggbb</c> colour for the dot; anything unparseable falls back to grey.</param>
    public static SKBitmap Render(string productName, string? colourName, string swatchHex)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));

        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(Background);

            using var line = new SKPaint { Color = Diagonal, IsAntialias = true, StrokeWidth = 2, Style = SKPaintStyle.Stroke };
            canvas.DrawLine(0, 0, Width, Height, line);
            canvas.DrawLine(Width, 0, 0, Height, line);

            using var rule = new SKPaint { Color = Rule, IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
            canvas.DrawRect(new SKRect(24, 24, Width - 24, Height - 24), rule);

            // A band behind the text, so the diagonals do not run through the words.
            using var band = new SKPaint { Color = Background, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawRect(new SKRect(48, (Height / 2f) - 210, Width - 48, (Height / 2f) + 210), band);

            DrawSwatch(canvas, swatchHex);
            DrawText(canvas, productName, colourName);

            return bitmap;
        }
        catch (Exception)
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void DrawSwatch(SKCanvas canvas, string swatchHex)
    {
        var colour = SKColor.TryParse(swatchHex, out var parsed) ? parsed : new SKColor(0xBF, 0xB9, 0xB0);

        using var fill = new SKPaint { Color = colour, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var outline = new SKPaint { Color = Rule, IsAntialias = true, StrokeWidth = 2, Style = SKPaintStyle.Stroke };

        var centre = new SKPoint(Width / 2f, (Height / 2f) - 150);
        canvas.DrawCircle(centre, 34, fill);
        canvas.DrawCircle(centre, 34, outline);
    }

    private static void DrawText(SKCanvas canvas, string productName, string? colourName)
    {
        using var bold = ResolveTypeface(bold: true);
        using var regular = ResolveTypeface(bold: false);

        using var strong = new SKPaint { Color = StrongInk, IsAntialias = true };
        using var normal = new SKPaint { Color = Ink, IsAntialias = true };
        using var faint = new SKPaint { Color = FaintInk, IsAntialias = true };

        var middle = Height / 2f;

        Write(canvas, "PLACEHOLDER", Width / 2f, middle - 60, new SKFont(bold, 62), strong);
        Write(canvas, "NO PRODUCT PHOTOGRAPH", Width / 2f, middle - 10, new SKFont(regular, 26), normal);
        Write(canvas, Shorten(productName, 34), Width / 2f, middle + 70, new SKFont(bold, 34), strong);
        Write(canvas, colourName is null ? "Fabric detail frame" : Shorten(colourName, 34), Width / 2f, middle + 120, new SKFont(regular, 30), normal);
        Write(canvas, "Spencer Denim — demo data, not a real product image", Width / 2f, Height - 70, new SKFont(regular, 22), faint);
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

    private static SKTypeface ResolveTypeface(bool bold)
    {
        var weight = bold ? SKFontStyle.Bold : SKFontStyle.Normal;

        foreach (var family in FontCandidates)
        {
            var typeface = SKFontManager.Default.MatchFamily(family, weight);
            if (typeface is not null)
            {
                return typeface;
            }
        }

        return SKTypeface.Default;
    }
}
