using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace SpencerDenim.Api.Http;

/// <summary>One file part of a multipart upload, spooled to a temporary file.</summary>
/// <param name="FieldName">The multipart field name, e.g. <c>file</c> or <c>poster</c>.</param>
/// <param name="FileName">The client's filename. Display data; it never becomes a path or a type.</param>
/// <param name="Content">A seekable stream over the spooled bytes, disposed with the reader.</param>
public sealed record UploadedFilePart(string FieldName, string FileName, Stream Content);

/// <summary>Why a multipart body could not be read.</summary>
public enum MultipartReadError
{
    None = 0,

    /// <summary>Not a multipart request, or no boundary. 400.</summary>
    NotMultipart,

    /// <summary>Past the byte ceiling for the endpoint. 413.</summary>
    TooLarge,

    /// <summary>Malformed multipart framing, or more parts than the endpoint allows. 400.</summary>
    Malformed,
}

/// <summary>
/// Reads a staff upload straight off the request body.
/// <para>
/// <b>Why not <c>IFormFile</c>.</b> Binding a form buffers the whole body before a single check has run,
/// which is exactly what a hostile 4 GB request wants; and it would pull the antiforgery middleware into
/// the model-binding path, where a rejection is a bare 400 rather than the ProblemDetails every other
/// rejection on these endpoints returns. Reading the parts here means the byte ceiling is enforced
/// <i>while</i> the body streams — the connection is dropped at the limit, not after — and the token is
/// checked from the request header before the body is touched at all.
/// </para>
/// <para>
/// Each file part is spooled to a temporary file opened with <see cref="FileOptions.DeleteOnClose"/>, so
/// nothing survives a failure, a cancelled request or a crashed handler; the service downstream then has
/// the seekable stream it needs to read a signature, hash the bytes and decode an image without three
/// copies in memory.
/// </para>
/// </summary>
public sealed class MultipartUpload : IAsyncDisposable
{
    /// <summary>Longest a text field may be. Alt text is capped at 250 characters in the database.</summary>
    private const int MaxFieldLength = 4096;

    /// <summary>More parts than this is not a form anybody meant to send.</summary>
    private const int MaxParts = 12;

    private const int CopyBufferBytes = 80 * 1024;

    private readonly List<UploadedFilePart> _files = [];
    private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);

    private MultipartUpload()
    {
    }

    /// <summary>Why the read failed, or <see cref="MultipartReadError.None"/>.</summary>
    public MultipartReadError Error { get; private init; }

    /// <summary>A sentence naming the problem, safe to return to a staff member.</summary>
    public string ErrorMessage { get; private init; } = string.Empty;

    /// <summary>The file parts, in the order they arrived.</summary>
    public IReadOnlyList<UploadedFilePart> Files => _files;

    /// <summary>The named file part, or null.</summary>
    public UploadedFilePart? File(string fieldName) =>
        _files.FirstOrDefault(file => string.Equals(file.FieldName, fieldName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The named text field, or null.</summary>
    public string? Field(string name) => _fields.GetValueOrDefault(name);

    /// <summary>
    /// Reads the body. Never throws for a malformed or oversized request: the caller turns
    /// <see cref="Error"/> into a status code.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <param name="maxBytes">
    /// The total the body may carry. Kestrel is told the same number, so an oversized upload is usually
    /// already refused by the time this sees it; this is the second of the three size defences (the third
    /// is the per-type ceiling in the media service, which is the one the database agrees with).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<MultipartUpload> ReadAsync(
        HttpRequest request,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.HasFormContentType
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || string.IsNullOrWhiteSpace(contentType.Boundary.Value))
        {
            return Failed(
                MultipartReadError.NotMultipart,
                "Send the file as a multipart/form-data request.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value!;
        var upload = new MultipartUpload();

        try
        {
            var reader = new MultipartReader(boundary, request.Body) { BodyLengthLimit = maxBytes };
            var parts = 0;

            while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
            {
                if (++parts > MaxParts)
                {
                    await upload.DisposeAsync();
                    return Failed(MultipartReadError.Malformed, $"A media upload may carry at most {MaxParts} parts.");
                }

                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                {
                    continue;
                }

                var name = disposition.Name.Value;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (disposition.FileName.HasValue || disposition.FileNameStar.HasValue)
                {
                    var fileName = (disposition.FileNameStar.HasValue
                        ? disposition.FileNameStar.Value
                        : disposition.FileName.Value) ?? string.Empty;

                    upload._files.Add(new UploadedFilePart(
                        name,
                        HeaderUtilities.RemoveQuotes(fileName).Value ?? string.Empty,
                        await SpoolAsync(section.Body, cancellationToken)));
                }
                else
                {
                    upload._fields[name] = await ReadFieldAsync(section.Body, cancellationToken);
                }
            }

            return upload;
        }
        catch (InvalidDataException exception)
        {
            await upload.DisposeAsync();

            // MultipartReader raises this both for bad framing and for exceeding BodyLengthLimit; the
            // message is the only thing that tells them apart, and the two answers differ (400 vs 413).
            return exception.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase)
                ? Failed(MultipartReadError.TooLarge, $"The upload is larger than the {maxBytes} byte limit.")
                : Failed(MultipartReadError.Malformed, "The multipart request could not be read.");
        }
        catch (BadHttpRequestException)
        {
            // Kestrel's own MaxRequestBodySize tripped.
            await upload.DisposeAsync();
            return Failed(MultipartReadError.TooLarge, $"The upload is larger than the {maxBytes} byte limit.");
        }
        catch (Exception)
        {
            await upload.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var file in _files)
        {
            await file.Content.DisposeAsync();
        }

        _files.Clear();
    }

    private static MultipartUpload Failed(MultipartReadError error, string message) =>
        new() { Error = error, ErrorMessage = message };

    private static async Task<Stream> SpoolAsync(Stream body, CancellationToken cancellationToken)
    {
        // DeleteOnClose: the file is unlinked when the handle closes, whatever happens next — a rejected
        // upload, a cancelled request or an unhandled exception leaves nothing in the temp directory.
        var path = Path.Combine(Path.GetTempPath(), $"sd-media-{Guid.NewGuid():N}.tmp");

        var file = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        try
        {
            await body.CopyToAsync(file, CopyBufferBytes, cancellationToken);
            await file.FlushAsync(cancellationToken);
            file.Position = 0;
            return file;
        }
        catch (Exception)
        {
            await file.DisposeAsync();
            throw;
        }
    }

    private static async Task<string> ReadFieldAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var limited = new byte[1024];

        while (buffer.Length <= MaxFieldLength)
        {
            var read = await body.ReadAsync(limited, cancellationToken);
            if (read == 0)
            {
                break;
            }

            buffer.Write(limited, 0, read);
        }

        var text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        return text.Length <= MaxFieldLength ? text : text[..MaxFieldLength];
    }
}
