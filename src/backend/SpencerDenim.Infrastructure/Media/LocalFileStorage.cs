using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Infrastructure.Media;

/// <summary>
/// <see cref="IFileStorage"/> over a local disk volume: what doc 03 §4 mounts today, and what doc 03 §5
/// replaces with "Azure Blob Storage adapter — one new class + config".
/// <para>
/// Everything is addressed by storage key; the mapping from key to path is <see cref="MediaStorageRoots"/>,
/// which is also where the traversal guard lives. Nothing in this class concatenates a caller's string
/// into a path.
/// </para>
/// </summary>
public sealed class LocalFileStorage(MediaStorageRoots roots, ILogger<LocalFileStorage> logger) : IFileStorage
{
    /// <summary>
    /// 80 KiB. Large enough that a 50 MB video is ~640 syscalls rather than ~12 000, small enough to stay
    /// off the large-object heap (85 000 bytes).
    /// </summary>
    private const int CopyBufferBytes = 80 * 1024;

    public bool IsConfigured => roots.IsConfigured;

    public async Task<StoredFileInfo> WriteAsync(string storageKey, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var path = roots.ResolvePath(storageKey);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidStorageKeyException("The media storage key has no directory part.", nameof(storageKey));

        Directory.CreateDirectory(directory);

        // The temporary name is on the same volume as the destination, so the move below is a rename
        // rather than a copy: a reader either sees the previous file or the complete new one, never a
        // half-written one, and a failure part-way leaves nothing at the key.
        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");

        try
        {
            long written;
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(destination, CopyBufferBytes, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                written = destination.Length;
            }

            File.Move(temporaryPath, path, overwrite: true);

            return new StoredFileInfo(written, File.GetLastWriteTimeUtc(path));
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = roots.ResolvePath(storageKey);

        try
        {
            // FileShare.Read so a concurrent reader is fine; the writer above never touches this path
            // until its rename, and a rename over an open handle is what the temp-then-move dance is for.
            Stream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return Task.FromResult<Stream?>(stream);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public Task<StoredFileInfo?> GetInfoAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var info = new FileInfo(roots.ResolvePath(storageKey));

        return Task.FromResult<StoredFileInfo?>(
            info.Exists ? new StoredFileInfo(info.Length, info.LastWriteTimeUtc) : null);
    }

    public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = roots.ResolvePath(storageKey);

        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    public Task<int> DeletePrefixAsync(string keyPrefix, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A prefix is itself a key (the rendition folder of one asset), so it goes through the same
        // validation and the same containment check as a file.
        var path = roots.ResolvePath(keyPrefix);

        if (!Directory.Exists(path))
        {
            return Task.FromResult(0);
        }

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
            removed++;
        }

        Directory.Delete(path, recursive: true);
        return Task.FromResult(removed);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            // The upload has already failed; losing the temporary file too is a disk-space problem for
            // the purge job, not a reason to replace the original error with this one.
            MediaLog.TemporaryFileNotDeleted(logger, exception, path);
        }
        catch (UnauthorizedAccessException exception)
        {
            MediaLog.TemporaryFileNotDeleted(logger, exception, path);
        }
    }
}
