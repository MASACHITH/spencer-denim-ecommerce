using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using SpencerDenim.Application.Media;
using SpencerDenim.Application.Media.Abstractions;

namespace SpencerDenim.Api.Endpoints.Media;

/// <summary>
/// Serves the public uploads volume at <c>/media/…</c> (doc 03 §4, ADM-18).
/// <para>
/// <b>Why the API serves this and not Caddy.</b> Doc 03 §4's diagram has the proxy serving the volume as
/// static files. That cannot satisfy this task: "404 for anything not Ready" and "a file that has been
/// deleted stops being served" are statements about <c>media.MediaAssets</c>, and a static file server
/// knows nothing about a database. So <c>/media</c> is an endpoint, and the deployment should proxy
/// <c>/media</c> to the API rather than mounting the volume in Caddy. The response is immutable and
/// carries a year's <c>max-age</c>, so the proxy or a CDN in front still absorbs the repeat traffic.
/// </para>
/// <para>
/// <b>What it will not do.</b> It never touches the private volume: a key that names it fails the
/// container check before anything is looked up, and the private root is a different directory that the
/// public root does not contain. It never trusts the URL as a path — the key goes through the grammar in
/// <c>MediaStorageKey</c> and then through a containment check on the resolved path. And every refusal is
/// the same 404, so nothing here distinguishes "no such file" from "that one is private" or "that one is
/// not published yet".
/// </para>
/// </summary>
internal static class MediaFileEndpoints
{
    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head];

    public static IEndpointRouteBuilder MapMediaFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/media/{**storageKey}", ReadMethods, ServeAsync)
            .AllowAnonymous()
            .WithTags("Media")
            .WithName("GetMediaFile")
            .WithSummary("Serve a public media file.")
            .WithDescription(
                "The path after /media is the asset's storage key: 'public/originals/…' for the original, " +
                "'public/renditions/{PublicId}/{thumb|card|large|zoom}.webp' for a rendition. Long cache " +
                "headers, a strong ETag and byte-range requests, so a browser can seek a video.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status206PartialContent)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ServeAsync(
        string storageKey,
        HttpContext httpContext,
        IPublicMediaFiles files,
        IFileStorage storage,
        IOptions<MediaOptions> options,
        CancellationToken cancellationToken)
    {
        var descriptor = await files.ResolveAsync(storageKey, cancellationToken);
        if (descriptor is null)
        {
            return TypedResults.NotFound();
        }

        var stream = await storage.OpenReadAsync(descriptor.StorageKey, cancellationToken);
        if (stream is null)
        {
            // Resolved a moment ago and gone now: a purge or a manual tidy-up in between.
            return TypedResults.NotFound();
        }

        var headers = httpContext.Response.Headers;

        // Every key contains the asset's PublicId and an asset's bytes never change under one key, so the
        // response is immutable: a browser that has it never asks again, not even with a revalidation.
        headers.CacheControl = string.Create(
            CultureInfo.InvariantCulture,
            $"public, max-age={options.Value.CacheMaxAgeSeconds}, immutable");

        // Belt and braces against a polyglot: the media type is from a fixed allow-list, and this stops a
        // browser second-guessing it.
        headers.XContentTypeOptions = "nosniff";

        // The stored filename is display data that came from a client, so it never reaches a header; the
        // key is this server's own.
        headers.ContentDisposition = "inline";

        return TypedResults.File(
            stream,
            descriptor.ContentType,
            lastModified: descriptor.LastModifiedUtc,
            entityTag: EntityTagHeaderValue.Parse(descriptor.ETag),

            // Byte ranges, so a <video> can seek instead of downloading 50 MB to jump to the middle
            // (doc 03 §3b). ASP.NET Core answers Range, If-Range and If-None-Match from the ETag and the
            // last-modified time given here.
            enableRangeProcessing: true);
    }
}
