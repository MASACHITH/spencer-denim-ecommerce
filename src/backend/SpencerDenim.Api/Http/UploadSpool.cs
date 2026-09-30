using Microsoft.Extensions.Options;
using SpencerDenim.Application.Media;

namespace SpencerDenim.Api.Http;

/// <summary>
/// Where an upload is written while it streams in, resolved once at startup.
/// <para>
/// Doc 03 §7 again: <c>Path.GetTempPath()</c> is a host resource, and on a Linux image it is frequently a
/// <c>tmpfs</c> — RAM, on a VPS where doc 03 §4 has already given SQL Server 2 GB of 8. A 50 MB video
/// spooled there is 50 MB of memory nobody budgeted. So the directory comes from
/// <c>Media:UploadSpoolPath</c>, a relative value is resolved against the content root, and the OS
/// temporary directory is only the default rather than the assumption.
/// </para>
/// </summary>
public sealed class UploadSpool
{
    public UploadSpool(IOptions<MediaOptions> options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var configured = options.Value.UploadSpoolPath;

        Directory = string.IsNullOrWhiteSpace(configured)
            ? Path.GetTempPath()
            : Path.GetFullPath(
                Path.IsPathFullyQualified(configured)
                    ? configured
                    : Path.Combine(environment.ContentRootPath, configured));

        // Created here rather than on the first upload, so a path that cannot be created is a startup
        // failure somebody is looking at, not a 500 on the first photograph the client tries to add.
        System.IO.Directory.CreateDirectory(Directory);
    }

    /// <summary>The absolute directory upload parts are spooled into.</summary>
    public string Directory { get; }
}
