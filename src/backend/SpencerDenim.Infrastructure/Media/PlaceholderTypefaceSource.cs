using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using SpencerDenim.Application.Media;

namespace SpencerDenim.Infrastructure.Media;

/// <summary>
/// Where the demo placeholders' label typeface comes from, or that there is none.
/// <para>
/// An interface for one implementation, which normally would not earn its keep — except that the whole
/// defect behind it was behaviour that only happens on a host with no font, and a Windows machine cannot
/// be made into one. This is the seam that lets the integration suite run the fontless path, which is the
/// deployment path, on any developer's machine.
/// </para>
/// </summary>
public interface IPlaceholderLabelFont
{
    /// <summary>Whether a typeface was found that actually draws.</summary>
    bool CanDrawText { get; }

    /// <summary>Where it came from, for one log line at startup.</summary>
    string Source { get; }

    /// <summary>The label font, or null when this deployment has none.</summary>
    PlaceholderLabelFont? Font { get; }
}

/// <summary>
/// Resolves the typefaces the demo placeholders are labelled with — <b>if</b> this deployment has any.
/// <para>
/// Doc 03 §7: "no code may depend on the host." A font is a host-provided resource, and the API's
/// deployment target is a Linux container built on <c>SkiaSharp.NativeAssets.Linux.NoDependencies</c>,
/// which has no fontconfig and, on a plain <c>dotnet/aspnet</c> base image, no font file at all. So this
/// class asks, in order:
/// <list type="number">
/// <item><description><c>Media:PlaceholderFontPath</c> — a TrueType or OpenType file the deployment
/// supplies. That is the only route that works everywhere, and it is configuration, not an
/// assumption.</description></item>
/// <item><description>the host's font manager, for a developer machine that happens to have
/// fonts.</description></item>
/// <item><description>nothing, and the placeholders carry no words. They are still unique, still
/// obviously not photographs, and their alt text still says "Placeholder image" — see
/// <see cref="PlaceholderImageRenderer"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Why no font is embedded in the assembly.</b> The obvious move is to reuse what the storefront
/// already ships: Inter, via <c>@fontsource/inter</c>, under the SIL Open Font License, which does permit
/// bundling and redistribution. It cannot be reused. That package contains only <c>.woff</c> and
/// <c>.woff2</c> files, and Skia reads neither — measured against
/// <c>inter-latin-400-normal.woff2</c> and <c>.woff</c> with SkiaSharp 4.152.1, both of which make
/// <c>SKTypeface.FromFile</c> return null. Shipping a label font would therefore mean committing a new
/// TrueType binary and its licence to the repository for the sake of words on a development-only image,
/// which is not worth it now that the image is distinct without them. The configuration key is left so a
/// deployment that does want labels — staging, for a client review — can drop a file in and set one
/// variable, with no code change.
/// </para>
/// </summary>
public sealed class PlaceholderTypefaceSource : IPlaceholderLabelFont, IDisposable
{
    /// <summary>Candidate families, in order, on a host whose font manager has anything.</summary>
    private static readonly string[] FontCandidates =
        ["Arial", "Helvetica", "Liberation Sans", "DejaVu Sans", "Noto Sans", "Segoe UI"];

    private readonly SKTypeface? _regular;
    private readonly SKTypeface? _bold;

    public PlaceholderTypefaceSource(
        IOptions<MediaOptions> options,
        IServiceProvider services,
        ILogger<PlaceholderTypefaceSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        var configured = options.Value.PlaceholderFontPath;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var contentRoot = services.GetService<IHostEnvironment>()?.ContentRootPath ?? AppContext.BaseDirectory;
            var path = Path.IsPathFullyQualified(configured)
                ? configured
                : Path.Combine(contentRoot, configured);

            _regular = LoadFromFile(path, logger);
            _bold = _regular;
            Source = _regular is null ? "none" : $"the file named by Media:PlaceholderFontPath ({path})";
        }

        if (_regular is null)
        {
            (_regular, _bold, var family) = FromHostFontManager();
            Source = family is null ? "none" : $"the host font manager ({family})";
        }

        // Measured, not assumed: Skia will hand back a typeface that draws nothing, and the whole point of
        // this class is to know which state we are in rather than to write out blank rectangles and call
        // them labelled.
        CanDrawText = Measures(_regular) && Measures(_bold);

        if (!CanDrawText)
        {
            Dispose();
            _regular = null;
            _bold = null;
            Source = "none";
        }
    }

    /// <summary>Whether a typeface was found that actually draws.</summary>
    public bool CanDrawText { get; }

    /// <summary>Where the typeface came from, for one log line at startup.</summary>
    public string Source { get; private set; } = "none";

    /// <summary>The label font, or null when this deployment has none.</summary>
    public PlaceholderLabelFont? Font =>
        CanDrawText && _regular is not null && _bold is not null
            ? new PlaceholderLabelFont(_regular, _bold)
            : null;

    public void Dispose()
    {
        _regular?.Dispose();

        if (!ReferenceEquals(_regular, _bold))
        {
            _bold?.Dispose();
        }
    }

    private static SKTypeface? LoadFromFile(string path, ILogger logger)
    {
        if (!File.Exists(path))
        {
            MediaLog.PlaceholderFontFileMissing(logger, path);
            return null;
        }

        var typeface = SKTypeface.FromFile(path);
        if (typeface is null)
        {
            // The likeliest cause by far: a .woff or .woff2, which Skia does not read.
            MediaLog.PlaceholderFontFileUnreadable(logger, path);
        }

        return typeface;
    }

    /// <summary>
    /// Asks the host's font manager, and treats it failing as "this host has no fonts" rather than as a
    /// reason not to start. On a container with no fontconfig the managed call is expected to return null,
    /// but it reaches a native library that is built differently on every platform, and the one platform
    /// this cannot be tried on is the one it deploys to. A font is an enhancement; nothing about it is
    /// worth a failed startup.
    /// </summary>
    private static (SKTypeface? Regular, SKTypeface? Bold, string? Family) FromHostFontManager()
    {
        try
        {
            return QueryHostFontManager();
        }
        catch (Exception)
        {
            return (null, null, null);
        }
    }

    private static (SKTypeface? Regular, SKTypeface? Bold, string? Family) QueryHostFontManager()
    {
        foreach (var family in FontCandidates)
        {
            var regular = SKFontManager.Default.MatchFamily(family, SKFontStyle.Normal);
            if (regular is null)
            {
                continue;
            }

            var bold = SKFontManager.Default.MatchFamily(family, SKFontStyle.Bold) ?? regular;
            return (regular, bold, family);
        }

        var fallback = SKTypeface.Default;
        if (Measures(fallback))
        {
            return (fallback, fallback, "Skia's default");
        }

        fallback.Dispose();
        return (null, null, null);
    }

    private static bool Measures(SKTypeface? typeface)
    {
        if (typeface is null)
        {
            return false;
        }

        try
        {
            using var font = new SKFont(typeface, 40);
            return font.MeasureText("PLACEHOLDER") > 0;
        }
        catch (Exception)
        {
            // Same reasoning as FromHostFontManager: a typeface that cannot even be measured is one this
            // deployment does not have, not a reason to refuse to start.
            return false;
        }
    }
}
