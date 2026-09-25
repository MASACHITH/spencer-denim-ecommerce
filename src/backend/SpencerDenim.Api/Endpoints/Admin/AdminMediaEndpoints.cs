using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Media;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Application.Media.Contracts;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The media library's write side (ADM-18, W1-04): upload an image, upload a video with its poster, read
/// an asset and its "used in…" list, and soft-delete one that nothing uses.
/// <para>
/// <b>Authorization</b> is the scheme W1-01 established and nothing new: every route declares
/// <c>RequirePermission(...)</c> (doc 03 §3a), which resolves to the policy registered for that permission
/// at startup. The permissions are <c>Media.Create</c>, <c>Media.View</c> and <c>Media.Delete</c>, which are
/// the module × action grid doc 08 §3.1 describes.
/// </para>
/// <para>
/// <b>Antiforgery</b> is validated by hand, exactly as <c>AdminAuthEndpoints.Logout</c> does, and the
/// framework's automatic form-binding check is switched off with <c>DisableAntiforgery</c> for two
/// reasons. The check still happens — it is the first thing each mutating handler does — but this way the
/// rejection is a ProblemDetails a client can read rather than an empty 400; and the token is taken from
/// the <c>X-XSRF-TOKEN</c> header <i>before</i> the body is touched, where the framework's version would
/// have bound the whole multipart form first, which for a 50 MB video is the upload this endpoint is
/// trying to refuse.
/// </para>
/// <para>
/// <b>Not here.</b> The guest bank-slip upload (doc 08 §3.3 N10) is W3 work and is deliberately absent:
/// the private volume exists, <c>IFileStorage</c> writes to it and the tests prove it is unreachable from
/// <c>/media</c>, but no endpoint writes to it yet. Nor is there a library listing — that belongs with
/// ADM-18's admin screen, which is a separate task.
/// </para>
/// </summary>
internal static class AdminMediaEndpoints
{
    private const string AntiforgeryHeader = "X-XSRF-TOKEN";

    public static IEndpointRouteBuilder MapAdminMediaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/media").WithTags("Admin Media");

        api.MapPost("/images", UploadImageAsync)
            .RequirePermission(Permissions.Media.Create)
            .DisableAntiforgery()
            .WithName("AdminUploadMediaImage")
            .WithSummary("Upload an image to the media library.")
            .WithDescription(
                "multipart/form-data: 'file' (JPEG, PNG or WebP, checked by file signature), and optional " +
                "'altText' and 'title' fields. The original is stored with its metadata stripped and the " +
                "WebP thumb/card/large/zoom renditions are generated.")
            .Produces<MediaAssetDto>(StatusCodes.Status201Created)
            .Produces<MediaAssetDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapPost("/videos", UploadVideoAsync)
            .RequirePermission(Permissions.Media.Create)
            .DisableAntiforgery()
            .WithName("AdminUploadMediaVideo")
            .WithSummary("Upload an MP4 (H.264) and its poster image.")
            .WithDescription(
                "multipart/form-data: 'file' (MP4, H.264, at most 50 MB) and 'poster' (an image), plus " +
                "optional 'altText' and 'title'. Nothing is converted on the server (doc 03 §3b), and a " +
                "video cannot exist without a poster (doc 08 §3.3).")
            .Produces<MediaAssetDto>(StatusCodes.Status201Created)
            .Produces<MediaAssetDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapGet("/{publicId:guid}", GetAsync)
            .RequirePermission(Permissions.Media.View)
            .WithName("AdminGetMediaAsset")
            .WithSummary("One asset with its usage list.")
            .Produces<MediaAssetDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/{publicId:guid}/usages", GetUsagesAsync)
            .RequirePermission(Permissions.Media.View)
            .WithName("AdminGetMediaAssetUsages")
            .WithSummary("Where an asset is used (doc 08 §3.3).")
            .Produces<IReadOnlyList<MediaUsageDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapDelete("/{publicId:guid}", DeleteAsync)
            .RequirePermission(Permissions.Media.Delete)
            .WithName("AdminDeleteMediaAsset")
            .WithSummary("Soft-delete an asset that nothing uses.")
            .WithDescription("409 with the usage list when something still uses it. Files are purged after 30 days.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> UploadImageAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IMediaService media,
        IOptions<MediaOptions> options,
        CancellationToken cancellationToken)
    {
        if (await ValidateAntiforgeryAsync(httpContext, antiforgery) is { } antiforgeryProblem)
        {
            return antiforgeryProblem;
        }

        var limits = options.Value;
        AllowLargeBody(httpContext, limits.MaxRequestBytes);

        await using var upload = await MultipartUpload.ReadAsync(httpContext.Request, limits.MaxRequestBytes, cancellationToken);
        if (ToProblem(upload) is { } readProblem)
        {
            return readProblem;
        }

        var file = upload.File("file");
        if (file is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "No file",
                "The request has no 'file' part.",
                "file_missing");
        }

        var outcome = await media.UploadImageAsync(
            new UploadImageRequest(
                new MediaUploadFile(file.Content, file.FileName),
                MediaVisibility.Public,
                upload.Field("altText"),
                upload.Field("title"),
                CurrentUserId(httpContext.User)),
            cancellationToken);

        return ToResult(outcome);
    }

    private static async Task<IResult> UploadVideoAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IMediaService media,
        IOptions<MediaOptions> options,
        CancellationToken cancellationToken)
    {
        if (await ValidateAntiforgeryAsync(httpContext, antiforgery) is { } antiforgeryProblem)
        {
            return antiforgeryProblem;
        }

        var limits = options.Value;
        AllowLargeBody(httpContext, limits.MaxRequestBytes);

        await using var upload = await MultipartUpload.ReadAsync(httpContext.Request, limits.MaxRequestBytes, cancellationToken);
        if (ToProblem(upload) is { } readProblem)
        {
            return readProblem;
        }

        var file = upload.File("file");
        if (file is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "No file",
                "The request has no 'file' part.",
                "file_missing");
        }

        var poster = upload.File("poster");
        if (poster is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Poster required",
                "A video needs a poster image in the same request: add a 'poster' part. " +
                "Nothing on this server extracts a frame from the video (doc 03 §3b).",
                "poster_required");
        }

        var outcome = await media.UploadVideoAsync(
            new UploadVideoRequest(
                new MediaUploadFile(file.Content, file.FileName),
                new MediaUploadFile(poster.Content, poster.FileName),
                upload.Field("altText"),
                upload.Field("title"),
                CurrentUserId(httpContext.User)),
            cancellationToken);

        return ToResult(outcome);
    }

    private static async Task<IResult> GetAsync(Guid publicId, IMediaService media, CancellationToken cancellationToken)
    {
        var asset = await media.GetAsync(publicId, cancellationToken);

        return asset is null
            ? Problem(StatusCodes.Status404NotFound, "Media asset not found", $"No media asset has the id '{publicId}'.", "not_found")
            : TypedResults.Ok(asset);
    }

    private static async Task<IResult> GetUsagesAsync(Guid publicId, IMediaService media, CancellationToken cancellationToken)
    {
        var asset = await media.GetAsync(publicId, cancellationToken);

        return asset is null
            ? Problem(StatusCodes.Status404NotFound, "Media asset not found", $"No media asset has the id '{publicId}'.", "not_found")
            : TypedResults.Ok(asset.Usages);
    }

    private static async Task<IResult> DeleteAsync(
        Guid publicId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IMediaService media,
        CancellationToken cancellationToken)
    {
        if (await ValidateAntiforgeryAsync(httpContext, antiforgery) is { } antiforgeryProblem)
        {
            return antiforgeryProblem;
        }

        var outcome = await media.DeleteAsync(publicId, CurrentUserId(httpContext.User), cancellationToken);

        return outcome switch
        {
            MediaDeleteOutcome.Deleted => TypedResults.NoContent(),

            MediaDeleteOutcome.InUse inUse => TypedResults.Problem(
                title: "Media asset is in use",
                detail: $"This asset is used in {inUse.Usages.Count} place(s), so it cannot be deleted. " +
                        "Remove it from them first.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "in_use",
                    ["usages"] = inUse.Usages,
                }),

            _ => Problem(
                StatusCodes.Status404NotFound,
                "Media asset not found",
                $"No media asset has the id '{publicId}'.",
                "not_found"),
        };
    }

    /// <summary>
    /// Raises Kestrel's body limit for this request only. The server default is 30 MB, which is below the
    /// 50 MB a video plus its poster may legitimately be (doc 03 §3b), so without this the endpoint would
    /// refuse a valid upload with a connection reset rather than an answer. It is per request, so no other
    /// endpoint is loosened.
    /// </summary>
    private static void AllowLargeBody(HttpContext httpContext, long maxBytes)
    {
        var feature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = maxBytes;
        }
    }

    /// <summary>
    /// Checks the antiforgery token from the request header, before the body is read.
    /// <para>
    /// The header is required explicitly rather than left to <c>ValidateRequestAsync</c>: with the header
    /// absent, the default token store falls back to reading the form, and a multipart form is the body
    /// this handler is about to stream itself. Asking first keeps the two off each other's ground.
    /// </para>
    /// </summary>
    private static async Task<ProblemHttpResult?> ValidateAntiforgeryAsync(HttpContext httpContext, IAntiforgery antiforgery)
    {
        if (!httpContext.Request.Headers.TryGetValue(AntiforgeryHeader, out var token) || string.IsNullOrWhiteSpace(token))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Missing antiforgery token",
                $"Send the antiforgery token in the {AntiforgeryHeader} header. " +
                "GET /api/admin/auth/antiforgery-token issues one.",
                "invalid_antiforgery_token");
        }

        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid antiforgery token",
                "The antiforgery token is missing, expired or does not match the session cookie.",
                "invalid_antiforgery_token");
        }
    }

    private static ProblemHttpResult? ToProblem(MultipartUpload upload) => upload.Error switch
    {
        MultipartReadError.None => null,
        MultipartReadError.TooLarge => Problem(
            StatusCodes.Status413PayloadTooLarge, "Upload too large", upload.ErrorMessage, "too_large"),
        MultipartReadError.NotMultipart => Problem(
            StatusCodes.Status400BadRequest, "Not a multipart upload", upload.ErrorMessage, "not_multipart"),
        _ => Problem(
            StatusCodes.Status400BadRequest, "Malformed upload", upload.ErrorMessage, "malformed_multipart"),
    };

    private static IResult ToResult(MediaUploadOutcome outcome) => outcome switch
    {
        MediaUploadOutcome.Created created => TypedResults.Created(
            $"/api/admin/media/{created.Asset.PublicId.ToString("D", CultureInfo.InvariantCulture)}",
            created.Asset),

        // 200, not 201: nothing was created. The body is the asset that already held these bytes.
        MediaUploadOutcome.Duplicate duplicate => TypedResults.Ok(duplicate.Asset),

        MediaUploadOutcome.Rejected rejected => Problem(
            StatusFor(rejected.Reason),
            TitleFor(rejected.Reason),
            rejected.Message,
            CodeFor(rejected.Reason)),

        _ => throw new InvalidOperationException($"Unhandled upload outcome {outcome.GetType().Name}."),
    };

    private static int StatusFor(MediaUploadRejection reason) => reason switch
    {
        MediaUploadRejection.Empty => StatusCodes.Status400BadRequest,
        MediaUploadRejection.PosterRequired => StatusCodes.Status400BadRequest,
        MediaUploadRejection.TooLarge => StatusCodes.Status413PayloadTooLarge,
        MediaUploadRejection.UnsupportedType => StatusCodes.Status415UnsupportedMediaType,
        MediaUploadRejection.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        MediaUploadRejection.StorageNotConfigured => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static string TitleFor(MediaUploadRejection reason) => reason switch
    {
        MediaUploadRejection.Empty => "Empty file",
        MediaUploadRejection.PosterRequired => "Poster required",
        MediaUploadRejection.TooLarge => "File too large",
        MediaUploadRejection.UnsupportedType => "Unsupported file type",
        MediaUploadRejection.Unprocessable => "File could not be processed",
        MediaUploadRejection.StorageNotConfigured => "Media storage is not configured",
        _ => "Media storage failed",
    };

    private static string CodeFor(MediaUploadRejection reason) => reason switch
    {
        MediaUploadRejection.Empty => "empty_file",
        MediaUploadRejection.PosterRequired => "poster_required",
        MediaUploadRejection.TooLarge => "too_large",
        MediaUploadRejection.UnsupportedType => "unsupported_type",
        MediaUploadRejection.Unprocessable => "unprocessable",
        MediaUploadRejection.StorageNotConfigured => "storage_not_configured",
        _ => "storage_failure",
    };

    /// <summary>
    /// An RFC 9457 body with a machine-readable <c>code</c> next to the sentence, so the admin screen can
    /// branch on the reason without parsing English.
    /// </summary>
    private static ProblemHttpResult Problem(int statusCode, string title, string detail, string code) =>
        TypedResults.Problem(
            title: title,
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code });

    private static int? CurrentUserId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
}
