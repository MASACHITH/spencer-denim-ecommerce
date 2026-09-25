using System.Globalization;

namespace SpencerDenim.Domain.Media;

/// <summary>Which uploads volume a storage key lives on (doc 03 §4: two volumes, only one of them public).</summary>
public enum MediaStorageContainer : byte
{
    /// <summary>The public uploads volume, served from <c>/media</c> with long cache headers.</summary>
    Public = 1,

    /// <summary>
    /// The private uploads volume (customer bank slips). A separate volume that the reverse proxy does
    /// not mount; nothing on it is ever reachable through <c>/media</c> (doc 08 §3.3).
    /// </summary>
    Private = 2,
}

/// <summary>
/// The storage-key grammar of doc 08 §3.3 and the guard that stops a key being used as a file path.
/// <para>
/// A key is <b>never</b> caller-supplied data: the only variable part is a <see cref="Guid"/> this
/// server generated. <see cref="Validate"/> exists anyway, because the serving endpoint takes a key
/// off the URL and because a row could be edited directly in the database. It is a whitelist — the
/// character set, the segment shape and the container prefix all have to match — so <c>..</c>, a
/// backslash, a drive letter, a UNC path, a percent escape, a NUL byte, a control character, a
/// Windows device name and a hidden dot-file are all rejected by construction rather than by a
/// blacklist of the ones somebody thought of.
/// </para>
/// </summary>
public static class MediaStorageKey
{
    /// <summary>Prefix of every key on the public volume.</summary>
    public const string PublicPrefix = "public/";

    /// <summary>Prefix of every key on the private volume.</summary>
    public const string PrivatePrefix = "private/";

    /// <summary><c>media.MediaAssets.StorageKey</c> is <c>varchar(400)</c>.</summary>
    public const int MaxLength = 400;

    /// <summary>One path segment stays well inside every filesystem's 255-byte limit.</summary>
    public const int MaxSegmentLength = 100;

    private const string PublicOriginalsFolder = "public/originals";
    private const string PrivateOriginalsFolder = "private/originals";
    private const string RenditionsFolder = "public/renditions";

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// The key of an uploaded original. The public volume holds the sanitised original of a public
    /// asset; the private volume holds a bank slip, which is never served from <c>/media</c>.
    /// </summary>
    /// <param name="visibility">Chooses the volume, and therefore the prefix.</param>
    /// <param name="publicId">The asset's <c>PublicId</c>: the only variable part of the key.</param>
    /// <param name="extension">A lowercase extension with the leading dot, e.g. <c>.jpg</c>.</param>
    public static string ForOriginal(MediaVisibility visibility, Guid publicId, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        if (!IsSafeExtension(extension))
        {
            throw new ArgumentException($"'{extension}' is not a usable file extension.", nameof(extension));
        }

        var folder = visibility == MediaVisibility.Private ? PrivateOriginalsFolder : PublicOriginalsFolder;
        return string.Create(CultureInfo.InvariantCulture, $"{folder}/{publicId:D}{extension}");
    }

    /// <summary>
    /// The rendition key convention of doc 08 §3.3, which deliberately has no table:
    /// <c>public/renditions/{PublicId}/{code}.webp</c>.
    /// </summary>
    public static string ForRendition(Guid publicId, string renditionCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(renditionCode);

        if (!MediaRenditions.IsKnownCode(renditionCode))
        {
            throw new ArgumentException($"'{renditionCode}' is not one of the rendition codes.", nameof(renditionCode));
        }

        return string.Create(CultureInfo.InvariantCulture, $"{RenditionsFolder}/{publicId:D}/{renditionCode}.webp");
    }

    /// <summary>The folder holding every rendition of one asset, so a purge removes them together.</summary>
    public static string RenditionFolderFor(Guid publicId) =>
        string.Create(CultureInfo.InvariantCulture, $"{RenditionsFolder}/{publicId:D}");

    /// <summary>Validates a key against the grammar and says which volume it names.</summary>
    /// <returns>
    /// <see langword="true"/> when the key is usable. Otherwise <see langword="false"/>, and
    /// <paramref name="reason"/> says why — for a log, never for a response body.
    /// </returns>
    public static bool Validate(string? key, out MediaStorageContainer container, out string reason)
    {
        container = default;
        reason = string.Empty;

        if (string.IsNullOrEmpty(key))
        {
            reason = "The storage key is empty.";
            return false;
        }

        if (key.Length > MaxLength)
        {
            reason = $"The storage key is longer than {MaxLength} characters.";
            return false;
        }

        foreach (var character in key)
        {
            if (!IsAllowedCharacter(character))
            {
                reason = "The storage key contains a character outside [A-Za-z0-9._/-].";
                return false;
            }
        }

        if (key.StartsWith(PublicPrefix, StringComparison.Ordinal))
        {
            container = MediaStorageContainer.Public;
        }
        else if (key.StartsWith(PrivatePrefix, StringComparison.Ordinal))
        {
            container = MediaStorageContainer.Private;
        }
        else
        {
            reason = $"A storage key has to start with '{PublicPrefix}' or '{PrivatePrefix}'.";
            return false;
        }

        var segments = key.Split('/');
        if (segments.Length < 2)
        {
            reason = "A storage key needs at least one segment after the container.";
            return false;
        }

        foreach (var segment in segments)
        {
            if (!IsAllowedSegment(segment, out var segmentReason))
            {
                reason = segmentReason;
                return false;
            }
        }

        return true;
    }

    /// <summary>Convenience overload for call sites that only need a yes or no.</summary>
    public static bool IsValid(string? key) => Validate(key, out _, out _);

    /// <summary>
    /// Whether a key names the public volume. The serving endpoint asks this before anything else: a
    /// private key must not even be looked up, let alone opened (doc 08 §3.3).
    /// </summary>
    public static bool IsPublic(string? key) =>
        Validate(key, out var container, out _) && container == MediaStorageContainer.Public;

    /// <summary>
    /// A file extension that is safe to build a key from: a dot, then one to eight ASCII letters or
    /// digits. The extension never comes from the uploaded filename — it is chosen from the detected
    /// file signature — but a key builder that cannot be handed <c>.jpg/../../etc/passwd</c> is one
    /// fewer thing to reason about.
    /// </summary>
    public static bool IsSafeExtension(string? extension)
    {
        if (extension is null || extension.Length is < 2 or > 9 || extension[0] != '.')
        {
            return false;
        }

        for (var index = 1; index < extension.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(extension[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllowedCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '/';

    private static bool IsAllowedSegment(string segment, out string reason)
    {
        reason = string.Empty;

        if (segment.Length == 0)
        {
            reason = "The storage key has an empty segment (a leading, trailing or doubled slash).";
            return false;
        }

        if (segment.Length > MaxSegmentLength)
        {
            reason = $"A storage key segment is longer than {MaxSegmentLength} characters.";
            return false;
        }

        // "." and ".." are the traversal cases. Any other leading dot is a hidden file, and a trailing
        // dot is silently dropped by Windows, so two different keys would name one file.
        if (segment[0] == '.' || segment[^1] == '.')
        {
            reason = "A storage key segment cannot start or end with a dot.";
            return false;
        }

        var stem = segment.Split('.')[0];
        foreach (var reserved in ReservedDeviceNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"'{stem}' is a reserved device name.";
                return false;
            }
        }

        return true;
    }
}
