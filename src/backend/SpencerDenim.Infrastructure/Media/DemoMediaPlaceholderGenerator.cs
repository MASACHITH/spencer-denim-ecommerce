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
/// The catalogue milestone seeded <c>MediaAssets</c> rows pointing at
/// <c>public/originals/demo/…</c> and said so in its own class documentation: "No files are uploaded here
/// (W1-04 owns the media pipeline)." This is that. Every original and every rendition is drawn by
/// <see cref="PlaceholderImageRenderer"/> and is unmistakably a placeholder — see that class for exactly
/// what one looks like. <b>No invented product photography is produced anywhere in this solution.</b>
/// </para>
/// <para>
/// <b>When it runs.</b> Only behind <c>Seed__IncludeDemoCatalogue</c> (doc 08 §9: development and staging
/// only), and only when media storage is configured; with no volumes there is nowhere to put the files
/// and the seeder says so once. It is idempotent per file: an asset whose original and four renditions
/// are all present is skipped, so a restart costs a handful of existence checks rather than a hundred
/// encodes, and a half-finished run finishes on the next start.
/// </para>
/// </summary>
public sealed class DemoMediaPlaceholderGenerator(
    AppDbContext dbContext,
    IFileStorage storage,
    SkiaImageProcessor imageProcessor,
    ILogger<DemoMediaPlaceholderGenerator> logger)
{
    /// <summary>What one run did, so the seeder can report it the way it reports row counts.</summary>
    /// <param name="Generated">Assets whose files were drawn and written.</param>
    /// <param name="AlreadyPresent">Assets that already had every file.</param>
    public sealed record Result(int Generated, int AlreadyPresent);

    /// <summary>
    /// Draws whatever is missing for every demo asset in the database.
    /// </summary>
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

        if (assets.Count == 0)
        {
            return new Result(0, 0);
        }

        if (!PlaceholderImageRenderer.CanDrawText)
        {
            MediaLog.DemoPlaceholdersHaveNoFont(logger);
        }

        var generated = 0;
        var alreadyPresent = 0;

        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await HasEveryFileAsync(asset, cancellationToken))
            {
                alreadyPresent++;
                continue;
            }

            var placeholder = DemoMediaPlaceholders.ByStorageKey.GetValueOrDefault(asset.StorageKey);

            await GenerateAsync(
                asset,
                placeholder?.ProductName ?? asset.Title ?? "Demo product",
                placeholder?.ColourName,
                placeholder?.ColourHex ?? "#BFB9B0",
                placeholder?.AltText,
                cancellationToken);

            generated++;
        }

        if (generated > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            MediaLog.DemoPlaceholdersGenerated(logger, generated, alreadyPresent);
        }

        return new Result(generated, alreadyPresent);
    }

    /// <summary>
    /// Draws and stores one asset's original and rendition set, and brings the row's metadata in line with
    /// the bytes that were actually written: the size, the real SHA-256, the real dimensions and the
    /// rendition set version. The catalogue seeder had to invent those figures because there was no file;
    /// once there is one, a row that still described the invention would be a lie the delete guard, the
    /// ETag and any future integrity check would all read.
    /// <para>The caller saves; this only stages the change, so one run is one round trip.</para>
    /// </summary>
    public async Task GenerateAsync(
        MediaAsset asset,
        string productName,
        string? colourName,
        string swatchHex,
        string? altText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        using var bitmap = PlaceholderImageRenderer.Render(productName, colourName, swatchHex);

        // The demo rows were seeded with a .jpg key, and they keep it: the point of this task is to put a
        // file behind the key the catalogue already published, not to renumber the catalogue.
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
        var originalBytes = encoded.ToArray();

        await WriteAsync(asset.StorageKey, originalBytes, cancellationToken);

        foreach (var rendition in imageProcessor.EncodeRenditions(bitmap))
        {
            await WriteAsync(
                MediaStorageKey.ForRendition(asset.PublicId, rendition.Rendition.Code),
                rendition.Bytes,
                cancellationToken);
        }

        asset.Status = MediaAssetStatus.Ready;
        asset.ContentType = "image/jpeg";
        asset.SizeBytes = originalBytes.LongLength;
        asset.Width = bitmap.Width;
        asset.Height = bitmap.Height;
        asset.Checksum = SHA256.HashData(originalBytes);
        asset.RenditionSetVersion = MediaRenditions.CurrentSetVersion;

        if (altText is not null)
        {
            asset.AltText = altText;
        }
    }

    private async Task<bool> HasEveryFileAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        if (await storage.GetInfoAsync(asset.StorageKey, cancellationToken) is null)
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
