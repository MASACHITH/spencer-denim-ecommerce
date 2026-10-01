using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Media;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Application.Media.ClientAssets;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Infrastructure.Media;

/// <summary>
/// Registers the media library (ADM-18, W1-04). Called from
/// <see cref="DependencyInjection.AddInfrastructure"/>, the way every other adapter group is.
/// </summary>
public static class MediaRegistration
{
    public static IServiceCollection AddMediaLibrary(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<MediaOptions>()
            .BindConfiguration(MediaOptions.SectionName)

            // The ceilings are the database's check constraints (doc 08 §3.3). Configuration may lower
            // them; a value above them would have the file written and then the row refused, which is
            // exactly the half-done state this milestone exists to make impossible — so it fails at
            // startup, where somebody is looking, rather than at 2 a.m. on the first big upload.
            .Validate(
                options => options.MaxVideoBytes is > 0 and <= MediaLimits.MaxVideoBytes,
                $"Media:MaxVideoBytes must be between 1 and {MediaLimits.MaxVideoBytes} " +
                "(CK_MediaAssets_SizeBytes rejects a larger video).")
            .Validate(
                options => options.MaxPrivateBytes is > 0 and <= MediaLimits.MaxPrivateBytes,
                $"Media:MaxPrivateBytes must be between 1 and {MediaLimits.MaxPrivateBytes} " +
                "(CK_MediaAssets_SizeBytes rejects a larger private file).")
            .Validate(options => options.MaxImageBytes > 0, "Media:MaxImageBytes must be greater than zero.")
            .Validate(
                options => options.MaxImageMegapixels is > 0 and <= 500,
                "Media:MaxImageMegapixels must be between 1 and 500.")
            .Validate(
                options => options.RenditionQuality is >= 1 and <= 100,
                "Media:RenditionQuality must be between 1 and 100.")
            .Validate(
                options => options.OriginalReencodeQuality is >= 1 and <= 100,
                "Media:OriginalReencodeQuality must be between 1 and 100.")
            .Validate(
                options => options.CacheMaxAgeSeconds >= 0,
                "Media:CacheMaxAgeSeconds must be zero or more.")
            .ValidateOnStart();

        // Singleton: the roots are resolved once from configuration and the content root, and validating
        // that the two volumes are separate is a startup concern, not a per-request one.
        services.AddSingleton<MediaStorageRoots>();
        services.AddSingleton<SkiaImageProcessor>();

        // Singleton: resolving a typeface means touching the filesystem or the host's font manager, and the
        // answer cannot change while the process runs. Registered under its interface as well, which is
        // the seam that lets a test run the no-font path a Linux container actually has.
        services.AddSingleton<PlaceholderTypefaceSource>();
        services.AddSingleton<IPlaceholderLabelFont>(provider =>
            provider.GetRequiredService<PlaceholderTypefaceSource>());

        services.AddScoped<IFileStorage, LocalFileStorage>();
        services.AddScoped<IMediaService, MediaService>();
        services.AddScoped<IPublicMediaFiles, PublicMediaFiles>();

        services.AddScoped<DemoMediaPlaceholderGenerator>();

        // W1-13: the client's delivered photographs, video and logo. Scoped, like everything else that
        // writes through the pipeline, and resolved once per run of the import verb.
        services.AddSingleton(ClientAssets.ClientAssetHolds.Default);
        services.AddScoped<IClientAssetImporter, ClientAssets.ClientAssetImporter>();

        return services;
    }
}
