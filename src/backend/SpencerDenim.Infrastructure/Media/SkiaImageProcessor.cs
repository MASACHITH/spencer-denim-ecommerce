using Microsoft.Extensions.Options;
using SkiaSharp;
using SpencerDenim.Application.Media;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Infrastructure.Media;

/// <summary>What the image header says, read before a single pixel is decoded.</summary>
/// <param name="Width">Width after orientation has been applied.</param>
/// <param name="Height">Height after orientation has been applied.</param>
/// <param name="HasNonDefaultOrientation">
/// True when the file relies on an EXIF orientation tag, which means it cannot simply have its metadata
/// stripped — see <see cref="ImageMetadataStripper"/>.
/// </param>
public sealed record ImageHeader(int Width, int Height, bool HasNonDefaultOrientation);

/// <summary>One encoded rendition, ready to store.</summary>
public sealed record EncodedRendition(MediaRendition Rendition, byte[] Bytes);

/// <summary>
/// The imaging half of the pipeline, on <b>SkiaSharp</b> (MIT).
/// <para>
/// <b>Why SkiaSharp.</b> Doc 03 §2 names it, and the reason it does is a licence: this project rejects
/// packages that moved to commercial terms (MediatR and AutoMapper are rejected in the same table), and
/// <c>SixLabors.ImageSharp</c> is under the Six Labors Split License, which is only free below a revenue
/// threshold — not a thing to hang a client's storefront on. SkiaSharp is MIT, published by Microsoft,
/// and wraps Google's Skia, which is the imaging library in Chrome and Android. Magick.NET (Apache-2.0)
/// would also be free, but it carries a far larger native surface — the whole of ImageMagick, its
/// delegate programs and its CVE history — for work that is four resizes and a WebP encode.
/// </para>
/// <para>
/// <b>Deployment consequence.</b> SkiaSharp is a native library, so the runtime asset has to match the
/// host. The <c>SkiaSharp</c> package carries the Windows and macOS builds; the Linux build ships
/// separately, and the project references <c>SkiaSharp.NativeAssets.Linux.NoDependencies</c> — the
/// variant that bundles a self-contained <c>libSkiaSharp.so</c> and needs no <c>libfontconfig1</c> or
/// <c>libfreetype6</c> in the image. That keeps the API container (doc 03 §4) a plain
/// <c>mcr.microsoft.com/dotnet/aspnet</c> base with no extra <c>apt-get</c> layer, which matters on a
/// 2-vCPU 8 GB VPS. It is one library across two package ids, not two dependencies.
/// </para>
/// <para>
/// <b>Metadata.</b> Every rendition is drawn from decoded pixels, so no rendition can carry EXIF, XMP or
/// IPTC: the GPS coordinates and camera serial in a client's photograph never leave this class. The
/// stored original is handled by <see cref="ImageMetadataStripper"/>, except when the source relies on an
/// EXIF orientation tag — then it is re-encoded here, upright, because honouring a tag that is about to
/// be deleted is not possible any other way.
/// </para>
/// </summary>
public sealed class SkiaImageProcessor(IOptions<MediaOptions> options)
{
    private readonly MediaOptions _options = options.Value;

    /// <summary>
    /// Reads the dimensions and orientation from the header. Returns <see langword="null"/> when the
    /// decoder will not open the file, or when it declares more pixels than
    /// <c>Media:MaxImageMegapixels</c>.
    /// <para>
    /// This is the decompression-bomb guard, and it is the reason the check happens here rather than
    /// after decoding: a 200 KB PNG can legally declare 40 000 × 40 000 pixels, which is 6.4 GB of RGBA
    /// once decoded. Nothing is decoded until this has passed.
    /// </para>
    /// </summary>
    public ImageHeader? ReadHeader(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data);

        if (codec is null)
        {
            return null;
        }

        var width = codec.Info.Width;
        var height = codec.Info.Height;

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        if ((long)width * height > (long)_options.MaxImageMegapixels * 1_000_000)
        {
            return null;
        }

        var origin = codec.EncodedOrigin;
        var swapsAxes = SwapsAxes(origin);

        return new ImageHeader(
            swapsAxes ? height : width,
            swapsAxes ? width : height,
            origin != SKEncodedOrigin.TopLeft);
    }

    /// <summary>
    /// Decodes the image and applies its EXIF orientation, so every consumer downstream sees pixels the
    /// right way up. The caller disposes the bitmap.
    /// </summary>
    /// <returns><see langword="null"/> when the file cannot be decoded.</returns>
    public SKBitmap? DecodeUpright(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data);

        if (codec is null)
        {
            return null;
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0
            || (long)info.Width * info.Height > (long)_options.MaxImageMegapixels * 1_000_000)
        {
            return null;
        }

        // Ask for a known pixel layout rather than whatever the file happens to use: a palette or a
        // 16-bit-per-channel source would otherwise reach the resampler in a format it handles differently.
        var target = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(target);

        try
        {
            // Success only. IncompleteInput means the file was cut short — an upload that dropped part
            // way, or a deliberately truncated one — and Skia would happily hand back the rows it managed
            // with grey below them. Publishing half a photograph is worse than refusing the upload, and
            // this is the only place that can tell: a truncated JPEG's header parses perfectly.
            if (codec.GetPixels(target, bitmap.GetPixels()) != SKCodecResult.Success)
            {
                bitmap.Dispose();
                return null;
            }
        }
        catch (Exception)
        {
            bitmap.Dispose();
            throw;
        }

        var origin = codec.EncodedOrigin;
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return bitmap;
        }

        using (bitmap)
        {
            return Orient(bitmap, origin);
        }
    }

    /// <summary>
    /// Encodes the WebP set of <see cref="MediaRenditions.Current"/>.
    /// <para>
    /// A rendition is never <b>up</b>scaled: its width is the smaller of the target and the source, so a
    /// 400 px logo does not become a blurry 2000 px <c>zoom</c>. All four codes are still produced, so
    /// every key in the convention answers and no consumer has to ask which ones exist.
    /// </para>
    /// </summary>
    public IReadOnlyList<EncodedRendition> EncodeRenditions(SKBitmap upright)
    {
        ArgumentNullException.ThrowIfNull(upright);

        // Only the renditions this source actually produces distinct pixels for. A 1200 px photograph
        // used to be written as `large` and then again, byte for byte, as `zoom`; MediaRenditions.
        // ProducedFor is the single rule that stops that, and MediaUrlBuilder reads the same rule so
        // nothing links a file this loop did not write.
        var produced = MediaRenditions.ProducedFor(MediaRenditions.Current, upright.Width);
        var results = new List<EncodedRendition>(produced.Count);

        foreach (var (rendition, width) in produced)
        {
            var height = Math.Max(1, (int)Math.Round(upright.Height * (double)width / upright.Width));

            byte[] bytes;
            if (width == upright.Width && height == upright.Height)
            {
                bytes = Encode(upright, SKEncodedImageFormat.Webp, _options.RenditionQuality);
            }
            else
            {
                using var resized = upright.Resize(
                    new SKImageInfo(width, height, upright.ColorType, upright.AlphaType),
                    new SKSamplingOptions(SKCubicResampler.Mitchell));

                if (resized is null)
                {
                    throw new InvalidOperationException(
                        $"Could not resize the image to {width}x{height} for the '{rendition.Code}' rendition.");
                }

                bytes = Encode(resized, SKEncodedImageFormat.Webp, _options.RenditionQuality);
            }

            results.Add(new EncodedRendition(rendition, bytes));
        }

        return results;
    }

    /// <summary>
    /// Re-encodes the upright pixels in the source's own format, for the one case
    /// <see cref="ImageMetadataStripper"/> cannot serve: a file whose EXIF says it must be rotated. The
    /// rotation is baked into the pixels and the metadata is gone, which is the only way to have both.
    /// </summary>
    public byte[] ReencodeOriginal(SKBitmap upright, MediaFileKind kind) => kind switch
    {
        MediaFileKind.Jpeg => Encode(upright, SKEncodedImageFormat.Jpeg, _options.OriginalReencodeQuality),
        MediaFileKind.Png => Encode(upright, SKEncodedImageFormat.Png, 100),
        MediaFileKind.Webp => Encode(upright, SKEncodedImageFormat.Webp, _options.OriginalReencodeQuality),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only JPEG, PNG and WebP can be re-encoded."),
    };

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var data = bitmap.Encode(format, quality);

        return data is null || data.Size == 0
            ? throw new InvalidOperationException($"SkiaSharp produced no output encoding {format}.")
            : data.ToArray();
    }

    private static bool SwapsAxes(SKEncodedOrigin origin) => origin
        is SKEncodedOrigin.LeftTop
        or SKEncodedOrigin.RightTop
        or SKEncodedOrigin.RightBottom
        or SKEncodedOrigin.LeftBottom;

    /// <summary>
    /// Draws the bitmap through the transform its EXIF origin asks for. The eight origins are the eight
    /// EXIF orientation values; four of them swap the axes, so the destination is transposed.
    /// </summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        var width = source.Width;
        var height = source.Height;
        var swap = SwapsAxes(origin);

        var result = new SKBitmap(new SKImageInfo(
            swap ? height : width,
            swap ? width : height,
            source.ColorType,
            source.AlphaType));

        try
        {
            using var canvas = new SKCanvas(result);
            canvas.SetMatrix(MatrixFor(origin, width, height));
            canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint: null);
            return result;
        }
        catch (Exception)
        {
            result.Dispose();
            throw;
        }
    }

    private static SKMatrix MatrixFor(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        // Mirrored horizontally.
        SKEncodedOrigin.TopRight => SKMatrix.Concat(
            SKMatrix.CreateTranslation(width, 0),
            SKMatrix.CreateScale(-1, 1)),

        // Rotated 180°.
        SKEncodedOrigin.BottomRight => SKMatrix.Concat(
            SKMatrix.CreateTranslation(width, height),
            SKMatrix.CreateScale(-1, -1)),

        // Mirrored vertically.
        SKEncodedOrigin.BottomLeft => SKMatrix.Concat(
            SKMatrix.CreateTranslation(0, height),
            SKMatrix.CreateScale(1, -1)),

        // Transposed: (x, y) → (y, x).
        SKEncodedOrigin.LeftTop => new SKMatrix
        {
            ScaleX = 0, SkewX = 1, TransX = 0,
            SkewY = 1, ScaleY = 0, TransY = 0,
            Persp2 = 1,
        },

        // Rotated 90° clockwise: (x, y) → (height − y, x).
        SKEncodedOrigin.RightTop => SKMatrix.Concat(
            SKMatrix.CreateTranslation(height, 0),
            SKMatrix.CreateRotationDegrees(90)),

        // Transverse: (x, y) → (height − y, width − x).
        SKEncodedOrigin.RightBottom => new SKMatrix
        {
            ScaleX = 0, SkewX = -1, TransX = height,
            SkewY = -1, ScaleY = 0, TransY = width,
            Persp2 = 1,
        },

        // Rotated 270° clockwise: (x, y) → (y, width − x).
        SKEncodedOrigin.LeftBottom => SKMatrix.Concat(
            SKMatrix.CreateTranslation(0, width),
            SKMatrix.CreateRotationDegrees(270)),

        _ => SKMatrix.Identity,
    };
}
