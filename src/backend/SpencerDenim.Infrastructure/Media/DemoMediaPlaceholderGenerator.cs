using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;

namespace SpencerDenim.Infrastructure.Media;

/// <summary>
/// Fills in the files behind the demo catalogue's media rows, so a development or staging storefront
/// shows something instead of a page of grey boxes.
/// <para>
/// The catalogue milestone seeded <c>MediaAssets</c> rows pointing at <c>public/originals/demo/…</c> and
/// said so in its own class documentation: "No files are uploaded here (W1-04 owns the media pipeline)."
/// This is that. Every original and every rendition is drawn by <see cref="PlaceholderImageRenderer"/> and
/// is unmistakably a placeholder — see that class for exactly what one looks like. <b>No invented product
/// photography is produced anywhere in this solution.</b>
/// </para>
/// <para>
/// <b>When it runs.</b> Only behind <c>Seed__IncludeDemoCatalogue</c> (doc 08 §9: development and staging
/// only), and only when media storage is configured; with no volumes there is nowhere to put the files and
/// the seeder says so once. It is idempotent per file: an asset whose original and four renditions are all
/// present is skipped, so a restart costs a few hundred existence checks rather than a few hundred
/// encodes, and a half-finished run finishes on the next start.
/// </para>
/// <para>
/// <b>Why it is parallel.</b> Drawing and encoding five images is about a quarter of a second per asset,
/// and the demo catalogue has well over a hundred of them, which would be most of a minute of blocked
/// startup on the first boot of a fresh database. The rendering and the file writes fan out over up to
/// four workers; the <c>DbContext</c> is not thread-safe, so every row change is applied afterwards, on
/// this thread, in one save.
/// </para>
/// </summary>
public sealed class DemoMediaPlaceholderGenerator(
    AppDbContext dbContext,
    IFileStorage storage,
    SkiaImageProcessor imageProcessor,
    IPlaceholderLabelFont typefaces,
    ILogger<DemoMediaPlaceholderGenerator> logger)
{
    /// <summary>What one run did, so the seeder can report it the way it reports row counts.</summary>
    /// <param name="Generated">Assets whose files were drawn and written.</param>
    /// <param name="AlreadyPresent">Assets that already had every file.</param>
    public sealed record Result(int Generated, int AlreadyPresent);

    /// <summary>The bytes that were written, so the row can be brought in line with them.</summary>
    /// <param name="SizeBytes">Length of the stored original.</param>
    /// <param name="Checksum">SHA-256 of the stored original.</param>
    /// <param name="Width">Pixel width of the stored original.</param>
    /// <param name="Height">Pixel height of the stored original.</param>
    public sealed record GeneratedFiles(long SizeBytes, byte[] Checksum, int Width, int Height);

    /// <summary>Draws whatever is missing for every demo asset in the database.</summary>
    public async Task<Result> RunAsync(CancellationToken cancellationToken)
    {
        if (!storage.IsConfigured)
        {
            MediaLog.DemoPlaceholdersSkipped(logger);
            return new Result(0, 0);
        }

        var assets = await dbContext.MediaAssets
            .Where(asset => asset.StorageKey.StartsWith(DemoMediaPlaceholders.StorageKeyPrefix))
            .ToListAsync(cancellationToken);

        var pending = new List<MediaAsset>();
        var alreadyPresent = 0;

        foreach (var asset in assets)
        {
            if (await HasEveryFileAsync(asset, cancellationToken))
            {
                alreadyPresent++;
            }
            else
            {
                pending.Add(asset);
            }
        }

        if (pending.Count == 0)
        {
            return new Result(0, alreadyPresent);
        }

        if (typefaces.CanDrawText)
        {
            MediaLog.PlaceholderFontResolved(logger, typefaces.Source);
        }
        else
        {
            MediaLog.DemoPlaceholdersHaveNoFont(logger);
        }

        var written = new ConcurrentDictionary<int, GeneratedFiles>();

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4),
                CancellationToken = cancellationToken,
            },
            async (asset, token) =>
            {
                var placeholder = DemoMediaPlaceholders.ByStorageKey.GetValueOrDefault(asset.StorageKey);

                written[asset.Id] = await GenerateAsync(
                    asset.PublicId,
                    asset.StorageKey,
                    placeholder?.ProductName ?? asset.Title ?? "Demo product",
                    placeholder?.ColourName,
                    placeholder?.ColourHex ?? "#BFB9B0",
                    token);
            });

        foreach (var asset in pending)
        {
            Apply(
                asset,
                written[asset.Id],
                DemoMediaPlaceholders.ByStorageKey.GetValueOrDefault(asset.StorageKey)?.AltText);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        MediaLog.DemoPlaceholdersGenerated(logger, pending.Count, alreadyPresent);

        return new Result(pending.Count, alreadyPresent);
    }

    /// <summary>
    /// Draws and stores one asset's original and rendition set. Touches no <c>DbContext</c>, so several
    /// may run at once.
    /// </summary>
    public async Task<GeneratedFiles> GenerateAsync(
        Guid publicId,
        string storageKey,
        string productName,
        string? colourName,
        string swatchHex,
        CancellationToken cancellationToken)
    {
        // The identity is the storage key, which carries a unique index: that is what makes two frames
        // two different images even when no glyph can be drawn (see PlaceholderImageRenderer).
        using var bitmap = PlaceholderImageRenderer.Render(
            new PlaceholderSubject(storageKey, productName, colourName, swatchHex),
            typefaces.Font);

        // The demo rows were seeded with a .jpg key, and they keep it: the point of this task is to put a
        // file behind the key the catalogue already published, not to renumber the catalogue.
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
        var originalBytes = encoded.ToArray();

        await WriteAsync(storageKey, originalBytes, cancellationToken);

        foreach (var rendition in imageProcessor.EncodeRenditions(bitmap))
        {
            await WriteAsync(
                MediaStorageKey.ForRendition(publicId, rendition.Rendition.Code),
                rendition.Bytes,
                cancellationToken);
        }

        return new GeneratedFiles(
            originalBytes.LongLength,
            SHA256.HashData(originalBytes),
            bitmap.Width,
            bitmap.Height);
    }

    /// <summary>
    /// Brings a row in line with the bytes that were actually written: the size, the real SHA-256, the real
    /// dimensions and the rendition set version, and last of all the status. The catalogue seeder has to
    /// invent a size and a checksum, because at that point there is no file; a row still describing the
    /// invention afterwards would be a lie that the ETag and any future integrity check would both read.
    /// </summary>
    public static void Apply(MediaAsset asset, GeneratedFiles files, string? altText)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(files);

        asset.ContentType = "image/jpeg";
        asset.SizeBytes = files.SizeBytes;
        asset.Checksum = files.Checksum;
        asset.Width = files.Width;
        asset.Height = files.Height;
        asset.RenditionSetVersion = MediaRenditions.CurrentSetVersion;

        if (altText is not null)
        {
            asset.AltText = altText;
        }

        // Last, and only once everything above is true: Ready is what the serving endpoint acts on.
        asset.Status = MediaAssetStatus.Ready;
    }

    private async Task<bool> HasEveryFileAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        if (asset.Status != MediaAssetStatus.Ready
            || await storage.GetInfoAsync(asset.StorageKey, cancellationToken) is null)
        {
            return false;
        }

        foreach (var rendition in MediaRenditions.Current)
        {
            var key = MediaStorageKey.ForRendition(asset.PublicId, rendition.Code);
            if (await storage.GetInfoAsync(key, cancellationToken) is null)
            {
                return false;
            }
        }

        return true;
    }

    private async Task WriteAsync(string storageKey, byte[] bytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(bytes, writable: false);
        await storage.WriteAsync(storageKey, buffer, cancellationToken);
    }
}
