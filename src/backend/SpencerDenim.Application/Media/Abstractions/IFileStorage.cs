namespace SpencerDenim.Application.Media.Abstractions;

/// <summary>What storage knows about a file it holds.</summary>
/// <param name="SizeBytes">Length in bytes.</param>
/// <param name="LastModifiedUtc">Last write time, used for the <c>Last-Modified</c> header.</param>
public sealed record StoredFileInfo(long SizeBytes, DateTimeOffset LastModifiedUtc);

/// <summary>
/// The file-storage port of doc 03 §3b ("Uploads go through <c>IMediaService</c> →
/// <c>IFileStorage</c> (local volume now, Azure Blob later)") and the one-class-change row of doc 03 §5.
/// <para>
/// Everything is addressed by a <b>relative storage key</b> (<c>SpencerDenim.Domain.Media.MediaStorageKey</c>),
/// never by a path. The key's first segment — <c>public/</c> or <c>private/</c> — chooses the volume, so
/// the two volumes of doc 03 §4 are separate roots behind one port and a private key can never resolve
/// under the public root. No member takes or returns a filesystem path, a root, a URL or a host name,
/// which is what lets an Azure Blob adapter replace this one without any caller changing (doc 03 §7:
/// "no code may depend on the host").
/// </para>
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Whether storage is configured at all. When it is false every operation throws
    /// <see cref="MediaStorageNotConfiguredException"/>: the API still starts (a developer who only
    /// wants the catalogue should not have to set up volumes), and the media endpoints answer with a
    /// ProblemDetails saying so rather than with a stack trace.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Writes <paramref name="content"/> at <paramref name="storageKey"/>, replacing whatever is there.
    /// <para>
    /// <b>Atomic:</b> the bytes go to a temporary name on the same volume and are moved into place once
    /// they are all written, so a reader never sees a half-written file and a failure part-way leaves
    /// nothing behind at the key.
    /// </para>
    /// </summary>
    /// <returns>What was written, so a caller does not have to stat the file again.</returns>
    Task<StoredFileInfo> WriteAsync(string storageKey, Stream content, CancellationToken cancellationToken);

    /// <summary>Opens the file for reading, or returns <see langword="null"/> when there is none.</summary>
    /// <remarks>The stream supports seeking, which is what the range-request handler needs.</remarks>
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>What storage knows about the file, or <see langword="null"/> when there is none.</summary>
    Task<StoredFileInfo?> GetInfoAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Deletes the file. Returns whether there was one to delete.</summary>
    Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every file under a key prefix — the rendition folder of one asset. Returns how many went.
    /// </summary>
    Task<int> DeletePrefixAsync(string keyPrefix, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when media storage is used without <c>Media__PublicRoot</c> / <c>Media__PrivateRoot</c> being
/// configured. Deliberately not a startup failure: see <see cref="IFileStorage.IsConfigured"/>.
/// </summary>
public sealed class MediaStorageNotConfiguredException : InvalidOperationException
{
    public MediaStorageNotConfiguredException()
        : base("Media storage is not configured. Set the Media__PublicRoot and Media__PrivateRoot environment variables.")
    {
    }

    public MediaStorageNotConfiguredException(string message)
        : base(message)
    {
    }

    public MediaStorageNotConfiguredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a storage key does not match the grammar. A bug or a tampered row, never ordinary input:
/// every caller that takes a key from outside validates it first and answers 404.
/// </summary>
public sealed class InvalidStorageKeyException : ArgumentException
{
    public InvalidStorageKeyException()
        : base("The storage key is not valid.")
    {
    }

    public InvalidStorageKeyException(string message)
        : base(message)
    {
    }

    public InvalidStorageKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public InvalidStorageKeyException(string message, string paramName)
        : base(message, paramName)
    {
    }
}
