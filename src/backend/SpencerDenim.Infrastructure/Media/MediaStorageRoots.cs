using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Media;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Infrastructure.Media;

/// <summary>
/// The two uploads volumes of doc 03 §4, resolved once at startup: the public one the reverse proxy
/// serves from <c>/media</c>, and the private one it does not mount.
/// <para>
/// Nothing in the code knows a path. <c>Media:PublicRoot</c> and <c>Media:PrivateRoot</c> come from
/// configuration; a relative value is resolved against the content root, which is where a Docker volume
/// mount or an Azure file share lands, so the same build runs on a developer's laptop and on the VPS
/// without an absolute path anywhere (doc 03 §7).
/// </para>
/// <para>
/// <b>The separation is structural, not a convention.</b> Each container has its own root directory, the
/// container comes from the key's first segment, and startup refuses a configuration where one root is
/// the other or sits inside it. So there is no string a caller could pass to reach a private file
/// through the public root: the public root simply does not contain the private volume.
/// </para>
/// </summary>
public sealed class MediaStorageRoots
{
    private readonly string? _publicRoot;
    private readonly string? _privateRoot;

    public MediaStorageRoots(IOptions<MediaOptions> options, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);

        // The content root is where a Docker volume mount lands, so it is the right base for a relative
        // setting — but AddInfrastructure is also used outside a host (the integration fixture builds a
        // plain ServiceCollection to migrate and seed), and requiring IHostEnvironment there would make
        // every non-web consumer register a fake one. Asked for, not demanded.
        var contentRoot = services.GetService<IHostEnvironment>()?.ContentRootPath ?? AppContext.BaseDirectory;
        _publicRoot = Resolve(options.Value.PublicRoot, contentRoot);
        _privateRoot = Resolve(options.Value.PrivateRoot, contentRoot);

        if (_publicRoot is null || _privateRoot is null)
        {
            // Half-configured is a configuration mistake, not a "media is off" deployment.
            if (_publicRoot is not null || _privateRoot is not null)
            {
                throw new MediaStorageNotConfiguredException(
                    "Media:PublicRoot and Media:PrivateRoot must both be set, or both be empty. " +
                    "Setting only one would put private files on the volume the reverse proxy serves.");
            }

            return;
        }

        if (Contains(_publicRoot, _privateRoot) || Contains(_privateRoot, _publicRoot))
        {
            throw new MediaStorageNotConfiguredException(
                $"Media:PrivateRoot ('{_privateRoot}') and Media:PublicRoot ('{_publicRoot}') must be separate " +
                "directories, neither inside the other: doc 03 §4 requires the private uploads volume to be " +
                "unreachable from the volume the reverse proxy serves at /media.");
        }
    }

    /// <summary>Whether both roots are configured. False means media is switched off on this deployment.</summary>
    public bool IsConfigured => _publicRoot is not null && _privateRoot is not null;

    /// <summary>The configured public root, for logging and diagnostics.</summary>
    public string PublicRootPath => _publicRoot ?? string.Empty;

    /// <summary>The configured private root, for logging and diagnostics.</summary>
    public string PrivateRootPath => _privateRoot ?? string.Empty;

    /// <summary>
    /// Resolves a storage key to an absolute path under the root its container names.
    /// </summary>
    /// <exception cref="MediaStorageNotConfiguredException">Storage is off.</exception>
    /// <exception cref="InvalidStorageKeyException">
    /// The key is not valid, or — the belt to the grammar's braces — the combined path escaped the root.
    /// The grammar already makes that unreachable; this re-checks the actual resolved path, because a
    /// containment check on the real filesystem path is the thing that is actually true, and the cost is
    /// one string comparison per file.
    /// </exception>
    public string ResolvePath(string? storageKey)
    {
        if (!IsConfigured)
        {
            throw new MediaStorageNotConfiguredException();
        }

        if (!MediaStorageKey.Validate(storageKey, out var container, out var reason))
        {
            throw new InvalidStorageKeyException($"Invalid media storage key: {reason}", nameof(storageKey));
        }

        var root = container == MediaStorageContainer.Private ? _privateRoot! : _publicRoot!;
        var combined = Path.GetFullPath(Path.Combine(root, storageKey!.Replace('/', Path.DirectorySeparatorChar)));

        if (!Contains(root, combined))
        {
            throw new InvalidStorageKeyException(
                "The media storage key resolved outside its container root.",
                nameof(storageKey));
        }

        return combined;
    }

    private static string? Resolve(string configured, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var trimmed = configured.Trim();
        var absolute = Path.IsPathFullyQualified(trimmed) ? trimmed : Path.Combine(contentRoot, trimmed);

        // A trailing separator would make the containment comparison below inconsistent.
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolute));
    }

    /// <summary>Whether <paramref name="candidate"/> is <paramref name="root"/> or sits under it.</summary>
    private static bool Contains(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (string.Equals(root, candidate, comparison))
        {
            return true;
        }

        var prefix = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }
}
