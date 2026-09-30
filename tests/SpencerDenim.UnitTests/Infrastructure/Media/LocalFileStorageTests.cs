using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Media;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Media;

namespace SpencerDenim.UnitTests.Infrastructure.Media;

/// <summary>
/// The local-disk adapter and, more importantly, the two-volume separation of doc 03 §4: the private
/// uploads volume is a different directory, and no key resolved against the public root can reach it.
/// </summary>
public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sd-media-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _publicRoot;
    private readonly string _privateRoot;

    public LocalFileStorageTests()
    {
        _publicRoot = Path.Combine(_root, "public-volume");
        _privateRoot = Path.Combine(_root, "private-volume");
        Directory.CreateDirectory(_publicRoot);
        Directory.CreateDirectory(_privateRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_file_written_by_key_comes_back_by_key()
    {
        var storage = Storage();
        const string key = "public/originals/2f1c0c8e-9a4b-4d31-8f2e-6b7c5a4d3e2f.jpg";
        var bytes = Encoding.UTF8.GetBytes("a picture of some jeans");

        var info = await storage.WriteAsync(key, new MemoryStream(bytes), TestContext.Current.CancellationToken);

        Assert.Equal(bytes.Length, info.SizeBytes);

        await using var read = await storage.OpenReadAsync(key, TestContext.Current.CancellationToken);
        Assert.NotNull(read);

        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, buffer.ToArray());
    }

    [Fact]
    public async Task A_key_lands_under_its_own_container_root_and_nowhere_else()
    {
        var storage = Storage();

        await storage.WriteAsync("public/originals/a.jpg", new MemoryStream([1]), TestContext.Current.CancellationToken);
        await storage.WriteAsync("private/originals/b.pdf", new MemoryStream([2]), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_publicRoot, "public", "originals", "a.jpg")));
        Assert.True(File.Exists(Path.Combine(_privateRoot, "private", "originals", "b.pdf")));

        // The proof that matters: nothing a private key wrote is anywhere under the public volume, which
        // is the directory the reverse proxy serves.
        var underPublic = Directory.EnumerateFiles(_publicRoot, "*", SearchOption.AllDirectories).ToList();
        Assert.DoesNotContain(underPublic, path => path.EndsWith("b.pdf", StringComparison.Ordinal));
        Assert.Single(underPublic);
    }

    [Fact]
    public async Task A_missing_file_reads_as_nothing_rather_than_throwing()
    {
        var storage = Storage();

        Assert.Null(await storage.OpenReadAsync("public/originals/gone.jpg", TestContext.Current.CancellationToken));
        Assert.Null(await storage.GetInfoAsync("public/originals/gone.jpg", TestContext.Current.CancellationToken));
        Assert.False(await storage.DeleteAsync("public/originals/gone.jpg", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_rendition_folder_is_deleted_whole()
    {
        var storage = Storage();
        var id = Guid.NewGuid();

        foreach (var rendition in MediaRenditions.Current)
        {
            await storage.WriteAsync(
                MediaStorageKey.ForRendition(id, rendition.Code),
                new MemoryStream([1, 2, 3]),
                TestContext.Current.CancellationToken);
        }

        var removed = await storage.DeletePrefixAsync(
            MediaStorageKey.RenditionFolderFor(id),
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaRenditions.Current.Count, removed);
        Assert.Null(await storage.GetInfoAsync(
            MediaStorageKey.ForRendition(id, MediaRenditions.Card),
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Writing goes to a temporary name on the same volume and is renamed into place, so a failure leaves
    /// nothing at the key and a reader never sees a half-written file. A stream that throws part-way is the
    /// only way to observe that from outside.
    /// </summary>
    [Fact]
    public async Task A_write_that_fails_part_way_leaves_nothing_behind()
    {
        var storage = Storage();
        const string key = "public/originals/half.jpg";

        await Assert.ThrowsAsync<IOException>(async () =>
            await storage.WriteAsync(key, new ThrowsHalfWayStream(), TestContext.Current.CancellationToken));

        Assert.Null(await storage.GetInfoAsync(key, TestContext.Current.CancellationToken));

        // Not even the temporary file: the directory the key names holds nothing at all.
        var directory = Path.Combine(_publicRoot, "public", "originals");
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData("public/../../etc/passwd")]
    [InlineData("../outside.jpg")]
    [InlineData("public/originals/../../../outside.jpg")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("public\\..\\..\\outside.jpg")]
    public async Task A_key_that_tries_to_leave_its_root_is_refused_by_storage_too(string key)
    {
        var storage = Storage();

        Assert.Throws<InvalidStorageKeyException>(() => Roots().ResolvePath(key));
        await Assert.ThrowsAsync<InvalidStorageKeyException>(async () =>
            await storage.WriteAsync(key, new MemoryStream([1]), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Doc 03 §4 puts the private volume outside anything the proxy serves. A configuration that nested one
    /// root inside the other would quietly undo that, so it fails at startup.
    /// </summary>
    [Fact]
    public void A_private_root_inside_the_public_root_is_refused_at_startup()
    {
        var nested = new MediaOptions
        {
            PublicRoot = _root,
            PrivateRoot = Path.Combine(_root, "private-volume"),
        };

        var exception = Assert.Throws<MediaStorageNotConfiguredException>(
            () => new MediaStorageRoots(Options.Create(nested), EmptyServices.Instance));

        Assert.Contains("separate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_same_directory_for_both_volumes_is_refused()
    {
        var same = new MediaOptions { PublicRoot = _root, PrivateRoot = _root };

        Assert.Throws<MediaStorageNotConfiguredException>(
            () => new MediaStorageRoots(Options.Create(same), EmptyServices.Instance));
    }

    [Fact]
    public void Configuring_only_one_volume_is_refused_rather_than_treated_as_off()
    {
        var half = new MediaOptions { PublicRoot = _publicRoot, PrivateRoot = string.Empty };

        Assert.Throws<MediaStorageNotConfiguredException>(
            () => new MediaStorageRoots(Options.Create(half), EmptyServices.Instance));
    }

    [Fact]
    public async Task With_no_volumes_configured_storage_says_so_instead_of_writing_somewhere()
    {
        var roots = new MediaStorageRoots(Options.Create(new MediaOptions()), EmptyServices.Instance);
        var storage = new LocalFileStorage(roots, NullLogger<LocalFileStorage>.Instance);

        Assert.False(storage.IsConfigured);
        await Assert.ThrowsAsync<MediaStorageNotConfiguredException>(async () =>
            await storage.WriteAsync("public/originals/a.jpg", new MemoryStream([1]), TestContext.Current.CancellationToken));
    }

    /// <summary>A relative setting resolves under the process base, so no configuration needs a full path.</summary>
    [Fact]
    public void A_relative_root_is_resolved_rather_than_used_as_written()
    {
        var relative = new MediaOptions { PublicRoot = "media/public", PrivateRoot = "media/private" };
        var roots = new MediaStorageRoots(Options.Create(relative), EmptyServices.Instance);

        Assert.True(roots.IsConfigured);
        Assert.True(Path.IsPathFullyQualified(roots.PublicRootPath));
        Assert.True(Path.IsPathFullyQualified(roots.PrivateRootPath));
    }

    private MediaStorageRoots Roots() => new(
        Options.Create(new MediaOptions { PublicRoot = _publicRoot, PrivateRoot = _privateRoot }),
        EmptyServices.Instance);

    private LocalFileStorage Storage() => new(Roots(), NullLogger<LocalFileStorage>.Instance);

    /// <summary>A service provider with nothing in it: the roots ask for a host environment, never require one.</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }

    private sealed class ThrowsHalfWayStream : Stream
    {
        private int _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= 1024)
            {
                throw new IOException("The upload connection dropped.");
            }

            _read += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
