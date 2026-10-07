using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Application.Media.Contracts;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Customer;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Api.Endpoints.Sales;

/// <summary>
/// The customer's payment slip: uploading it, and the two routes that are allowed to read it back (PAY-01,
/// doc 11 P7 step 3, doc 08 §3.3 N10).
/// <para>
/// <b>A slip is the most sensitive file this shop stores</b> — a bank document naming a customer, their
/// account and what they paid — so three rules hold on every route here and are worth stating together:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>It is never reachable by its own identity.</b> Every route names an <i>order</i>. The asset's token
/// is in the order payload so a page can tell whether a slip exists and what type it is, and that is all
/// it is for; a route keyed on the asset would serve any private file in the system to anybody who could
/// produce its id.
/// </description></item>
/// <item><description>
/// <b>It is never cached.</b> <c>no-store</c> on every response, including the file itself, so it does not
/// sit in a shared proxy or in a browser's disk cache on a shop computer. That is the opposite of
/// <c>/media</c>, which serves immutable public files with a year-long cache.
/// </description></item>
/// <item><description>
/// <b>It is served as an attachment with <c>nosniff</c>.</b> A PDF is a programmable container and nothing
/// on this server parses one; a browser that renders it inline on this origin would be running a
/// stranger's document beside an admin session. Downloading is the only thing staff need to do with it.
/// </description></item>
/// </list>
/// <para>
/// <b>The upload carries no antiforgery token and needs none</b>, the same reasoning as the rest of
/// <c>CheckoutEndpoints</c>: there is no customer session for a forged request to ride on (ACC-01 is
/// W3-01), and the order's token is the only credential — which an attacker able to forge the request
/// would already need to have, and which alone lets them read the whole order anyway. The <b>staff</b>
/// route is a GET and changes nothing.
/// </para>
/// </summary>
internal static class PaymentSlipEndpoints
{
    /// <summary>
    /// The request-body ceiling for a slip upload.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not <c>MediaOptions.MaxRequestBytes</c>, which is about 65 MB.</b> That figure is
    /// sized for a video and its poster on an authenticated admin route; this is an unauthenticated route
    /// reachable with nothing but an order token, and raising its limit to 65 MB would let anybody holding
    /// one make the server spool 65 MB to disk before a single check ran. 5 MB is doc 08 §3.3's own slip
    /// limit, and the slack is for the multipart framing around it rather than for the file.
    /// <para>
    /// This is the outermost of the three size defences; <c>MediaOptions.MaxBytesFor</c> and
    /// <c>CK_MediaAssets_SizeBytes</c> are the other two, and all three agree on 5 MB for the file itself.
    /// </para>
    /// </remarks>
    private const long MaxSlipRequestBytes = MediaLimits.MaxPrivateBytes + (64 * 1024);

    public static IEndpointRouteBuilder MapPaymentSlipEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/orders/{publicId:guid}/payment-slip", UploadAsync)
            .WithTags("Checkout")
            .WithName("UploadOrderPaymentSlip")
            .WithSummary("Upload the payment slip for a bank transfer (PAY-01, doc 11 P7 step 3).")
            .WithDescription(
                "Multipart, with the file in a part named 'file'. A photo (JPEG, PNG or WebP) or a PDF, up "
                + "to 5 MB, checked by the file's contents and not its name. A new upload replaces the "
                + "previous one. Limited to five an hour per order. The file is stored privately and is "
                + "never served from /media.")
            .DisableAntiforgery()
            .Produces<SlipUploadedDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        endpoints.MapGet("/api/orders/{publicId:guid}/payment-slip", ServeForCustomerAsync)
            .WithTags("Checkout")
            .WithName("GetOrderPaymentSlip")
            .WithSummary("The slip on this order, for the customer who uploaded it.")
            .WithDescription(
                "Reached only through the order's own link, like the order itself. Served as a download "
                + "with no caching. 404 when the order has no slip, and 404 for a token that names nothing "
                + "— the two are indistinguishable on purpose.")
            .Produces<IResult>(StatusCodes.Status200OK, "application/octet-stream")
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet("/api/admin/orders/bank-transfers/{publicId:guid}/slip", ServeForStaffAsync)
            .RequirePermission(Permissions.Orders.View)
            .WithTags("Admin Bank Transfers")
            .WithName("AdminGetBankTransferSlip")
            .WithSummary("The slip on this order, for staff reviewing it (doc 11 P7 step 4).")
            .WithDescription(
                "Orders.View. The same file as the customer's route, through the permission instead of the "
                + "order token. Served as a download with no caching, so it does not stay in a shop "
                + "computer's cache.")
            .Produces<IResult>(StatusCodes.Status200OK, "application/octet-stream")
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> UploadAsync(
        HttpContext httpContext,
        ICommandHandler<SubmitBankTransferSlipCommand, SubmitBankTransferSlipResult> handler,
        UploadSpool spool,
        Guid publicId,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        // Lowered, not raised: Kestrel's default is 30 MB and a slip is 5. Done before the body is touched.
        var feature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = MaxSlipRequestBytes;
        }

        await using var upload = await MultipartUpload.ReadAsync(
            httpContext.Request,
            MaxSlipRequestBytes,
            spool.Directory,
            cancellationToken);

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
                "Choose the photo or PDF of your payment slip. The request has no 'file' part.",
                "file_missing");
        }

        var result = await handler.HandleAsync(
            new SubmitBankTransferSlipCommand(
                publicId,
                new MediaUploadFile(file.Content, file.FileName),
                httpContext.Connection.RemoteIpAddress?.ToString()),
            cancellationToken);

        switch (result)
        {
            case SubmitBankTransferSlipResult.Stored stored:
                return TypedResults.Ok(new SlipUploadedDto(
                    stored.SlipPublicId,
                    stored.FileName,
                    stored.ContentType,
                    stored.UploadedAtUtc));

            case SubmitBankTransferSlipResult.NotFound:
                return TypedResults.NotFound();

            case SubmitBankTransferSlipResult.Rejected rejected:
                return Problem(
                    StatusForRejection(rejected.Reason),
                    "That file cannot be used",
                    rejected.Message,
                    "slip_rejected");

            case SubmitBankTransferSlipResult.TooManyUploads tooMany:
                httpContext.Response.Headers.RetryAfter =
                    ((int)tooMany.Window.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

                return Problem(
                    StatusCodes.Status429TooManyRequests,
                    "Too many uploads",

                    // Specific for the order's own limit, vague for the address's: a customer knows what
                    // they have been doing with their own order, and a script learns nothing useful from
                    // being told which of the two limits it hit (doc 08 §3.3 N10).
                    tooMany.Limit == SlipUploadPermit.OrderLimitReached
                        ? "You have sent several slips for this order in the last hour. If something is "
                          + "wrong with the one we have, please contact the shop."
                        : "Too many uploads have been made recently. Please try again shortly.",
                    "too_many_uploads");

            default:
                throw new InvalidOperationException($"Unhandled slip result {result.GetType().Name}.");
        }
    }

    private static Task<IResult> ServeForCustomerAsync(
        HttpContext httpContext,
        IBankTransferReader reader,
        IFileStorage storage,
        Guid publicId,
        CancellationToken cancellationToken) =>
        ServeAsync(httpContext, reader, storage, publicId, cancellationToken);

    private static Task<IResult> ServeForStaffAsync(
        HttpContext httpContext,
        IBankTransferReader reader,
        IFileStorage storage,
        Guid publicId,
        CancellationToken cancellationToken) =>
        ServeAsync(httpContext, reader, storage, publicId, cancellationToken);

    /// <summary>
    /// Streams the order's slip, or a bare 404.
    /// </summary>
    /// <remarks>
    /// One body for both routes, so the customer's and the staff member's view of a slip cannot diverge —
    /// the difference between them is the guard on the route, which is exactly where doc 11 P7's
    /// permissions table puts it.
    /// <para>
    /// <b>No <c>ETag</c>, no <c>Last-Modified</c> and no range support</b>, unlike <c>/media</c>. All three
    /// are caching and seeking aids for a public immutable file; here they would invite a store, and a
    /// 5 MB download needs none of them.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ServeAsync(
        HttpContext httpContext,
        IBankTransferReader reader,
        IFileStorage storage,
        Guid publicId,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        if (await reader.FindSlipAsync(publicId, cancellationToken) is not { } slip)
        {
            return TypedResults.NotFound();
        }

        var stream = await storage.OpenReadAsync(slip.StorageKey, cancellationToken);
        if (stream is null)
        {
            // Resolved a moment ago and gone now: the retention purge, or a volume that is not mounted.
            return TypedResults.NotFound();
        }

        var headers = httpContext.Response.Headers;
        headers.XContentTypeOptions = "nosniff";

        // Attachment, never inline. A PDF rendered inline on this origin is a stranger's document running
        // beside an admin session, and nothing staff do with a slip needs it in the page.
        headers.ContentDisposition = "attachment";

        return TypedResults.File(stream, slip.ContentType, enableRangeProcessing: false);
    }

    private static int StatusForRejection(MediaUploadRejection reason) => reason switch
    {
        MediaUploadRejection.Empty => StatusCodes.Status400BadRequest,
        MediaUploadRejection.TooLarge => StatusCodes.Status413PayloadTooLarge,
        MediaUploadRejection.UnsupportedType => StatusCodes.Status415UnsupportedMediaType,
        MediaUploadRejection.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        MediaUploadRejection.StorageNotConfigured => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

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

    private static ProblemHttpResult Problem(int status, string title, string detail, string code) =>
        TypedResults.Problem(
            title: title,
            detail: detail,
            statusCode: status,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code });

    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, private";
        response.Headers.Vary = "Cookie";
    }
}

/// <summary>What the page needs to show a slip back after an upload.</summary>
/// <param name="SlipPublicId">The asset's token. It identifies the slip; it does not open it.</param>
/// <param name="FileName">The sanitised filename, so the page can say which file is on the order.</param>
/// <param name="ContentType">Image or PDF.</param>
/// <param name="UploadedAtUtc">When it arrived.</param>
public sealed record SlipUploadedDto(
    Guid SlipPublicId,
    string FileName,
    string ContentType,
    DateTime UploadedAtUtc);
