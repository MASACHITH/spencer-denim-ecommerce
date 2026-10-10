using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Payments.PayHere;

namespace SpencerDenim.Api.Endpoints.Sales;

/// <summary>
/// PayHere's server-to-server notification (doc 11 P8b step 4): the one request that can pay an order without
/// a person or a browser in the loop, which is why almost everything about it is a refusal.
/// <para>
/// <b>Anonymous, because PayHere is.</b> There is no session and no token; the credential is the <c>md5sig</c>
/// in the body, computed over the merchant secret, and it is verified before anything is stored (see
/// <see cref="PayHereNotifyCommandHandler"/>). The route is the shop's own public address plus
/// <see cref="PayHereRoutes.NotifyPath"/>; the reverse proxy already sends <c>/api</c> here.
/// </para>
/// <para>
/// <b>The answers are bare status codes, and that is deliberate.</b> The body is empty for 200 and is the
/// host's generic status-code document (no detail, nothing from the request) for the refusals. 200 means "stored; stop calling" — it is
/// sent for a first delivery, a duplicate and a notification that names no payment of ours, so PayHere is
/// never told which. 400 means "not a valid notification" — malformed, wrong merchant, or a signature that
/// does not verify — with no reason in the body for a forger to learn from. 503 means "PayHere is not
/// configured here, try again". Nothing from the request is echoed.
/// </para>
/// <para>
/// <b>Nothing from the request is logged either</b> — no body, no field, no signature. A refusal is logged
/// as a category ("signature did not verify") and nothing else, because the payload is the attacker's and a
/// log line is where a card number or a guessable value would persist.
/// </para>
/// <para>
/// <b>No antiforgery and none needed</b>, the reasoning of the rest of <c>CheckoutEndpoints</c>: antiforgery
/// protects a browser session from a forged request riding on its cookie, and this request has no cookie, no
/// session and no browser. Its authenticity is the signature.
/// </para>
/// </summary>
internal static partial class PayHereEndpoints
{
    /// <summary>
    /// The request-body ceiling. A notification is a few hundred bytes of fields; anything near this is not
    /// PayHere. Set before the body is read, so an oversized request is refused without being buffered.
    /// </summary>
    private const long MaxBodyBytes = 16 * 1024;

    /// <summary>The fields read, once each. A field posted twice is ambiguous about what was signed.</summary>
    private static readonly string[] Fields =
    [
        "merchant_id",
        "order_id",
        "payment_id",
        "payhere_amount",
        "payhere_currency",
        "status_code",
        "md5sig",
        "status_message",
        "method",
    ];

    public static IEndpointRouteBuilder MapPayHereEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(PayHereRoutes.NotifyPath, NotifyAsync)
            .WithTags("Payments")
            .WithName("PayHereNotify")
            .WithSummary("PayHere's payment notification (PAY-03, doc 11 P8b step 4).")
            .WithDescription(
                "Called by PayHere's servers, not by a browser: application/x-www-form-urlencoded with "
                + "merchant_id, order_id, payment_id, payhere_amount, payhere_currency, status_code and "
                + "md5sig. The signature and the merchant ID are verified first, in constant time, and "
                + "nothing is stored for a request that fails either: 400. A verified notification is "
                + "stored once per PayHere payment ID and status code and then applied: 200, also for a "
                + "redelivery. 503 when PayHere is not configured. A refusal carries no reason and nothing "
                + "from the request.")
            .DisableAntiforgery()
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> NotifyAsync(
        HttpContext httpContext,
        ICommandHandler<PayHereNotifyCommand, PayHereNotifyResult> handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("SpencerDenim.Api.PayHereNotify");

        httpContext.Response.Headers.CacheControl = "no-store";

        var request = httpContext.Request;

        // The one content type PayHere sends. A multipart body would be parsed by ReadFormAsync too, with
        // different limits, and the point of a fixed shape is that there is nothing else to reason about.
        if (!request.HasFormContentType
            || !(request.ContentType ?? string.Empty).StartsWith(
                "application/x-www-form-urlencoded",
                StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var sizeLimit = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeLimit is { IsReadOnly: false })
        {
            sizeLimit.MaxRequestBodySize = MaxBodyBytes;
        }

        IFormCollection form;

        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException)
        {
            // A body over the ceiling, a malformed encoding, too many fields. Not logged with its content.
            LogUnreadable(logger);

            return exception is BadHttpRequestException bad
                ? TypedResults.StatusCode(bad.StatusCode)
                : TypedResults.BadRequest();
        }

        var fields = new Dictionary<string, string>(Fields.Length, StringComparer.Ordinal);

        foreach (var name in Fields)
        {
            if (!form.TryGetValue(name, out StringValues values) || values.Count == 0)
            {
                continue;
            }

            // Posted twice: which one did the signature cover? Not worth guessing at.
            if (values.Count > 1)
            {
                LogRefused(logger, "a field was posted more than once");

                return TypedResults.BadRequest();
            }

            fields[name] = values[0] ?? string.Empty;
        }

        var result = await handler.HandleAsync(new PayHereNotifyCommand(fields), cancellationToken);

        switch (result)
        {
            case PayHereNotifyResult.Accepted accepted:
                Log(logger, accepted);
                return TypedResults.Ok();

            case PayHereNotifyResult.NotConfigured:
                LogNotConfigured(logger);
                httpContext.Response.Headers.RetryAfter = "300";
                return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);

            case PayHereNotifyResult.Forged forged:
                LogRefused(
                    logger,
                    forged.Reason == PayHereRefusal.MerchantMismatch
                        ? "the merchant ID is not this shop's"
                        : "the signature did not verify");
                return TypedResults.BadRequest();

            default:
                LogRefused(logger, "it is not shaped like a notification");
                return TypedResults.BadRequest();
        }
    }

    private static void Log(ILogger logger, PayHereNotifyResult.Accepted accepted)
    {
        if (accepted.IsDuplicate || accepted.Outcome is null)
        {
            LogDuplicate(logger);
            return;
        }

        var outcome = accepted.Outcome;

        switch (outcome.Kind)
        {
            case PayHereOutcomeKind.Applied:
                LogApplied(logger, outcome.Detail);
                break;

            case PayHereOutcomeKind.Ignored:
                LogIgnored(logger, outcome.Detail);
                break;

            case PayHereOutcomeKind.Unmatched:
                LogUnmatched(logger, outcome.Detail);
                break;

            case PayHereOutcomeKind.GaveUp:
                LogFailed(logger, outcome.Detail, outcome.Error);
                break;

            default:
                LogRetry(logger, outcome.Detail, outcome.Error);
                break;
        }
    }

    [LoggerMessage(
        EventId = 3510,
        Level = LogLevel.Warning,
        Message = "A PayHere notification was refused and nothing was stored: {Reason}.")]
    private static partial void LogRefused(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 3511,
        Level = LogLevel.Warning,
        Message = "A request to the PayHere notify address could not be read, so it was refused.")]
    private static partial void LogUnreadable(ILogger logger);

    [LoggerMessage(
        EventId = 3512,
        Level = LogLevel.Error,
        Message = "A PayHere notification arrived but PayHere is not configured (merchant ID, secret or mode "
            + "missing), so it was not verified and PayHere has been asked to try again. Fix "
            + "Settings.Payments.PayHere in the admin panel.")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(
        EventId = 3513,
        Level = LogLevel.Information,
        Message = "A PayHere notification was already stored; this is a redelivery and nothing was done.")]
    private static partial void LogDuplicate(ILogger logger);

    [LoggerMessage(
        EventId = 3514,
        Level = LogLevel.Information,
        Message = "A PayHere notification was applied: {Detail}")]
    private static partial void LogApplied(ILogger logger, string detail);

    [LoggerMessage(
        EventId = 3515,
        Level = LogLevel.Information,
        Message = "A PayHere notification needed no action: {Detail}")]
    private static partial void LogIgnored(ILogger logger, string detail);

    [LoggerMessage(
        EventId = 3516,
        Level = LogLevel.Warning,
        Message = "A verified PayHere notification names no payment of this shop and was ignored: {Detail}")]
    private static partial void LogUnmatched(ILogger logger, string detail);

    [LoggerMessage(
        EventId = 3517,
        Level = LogLevel.Warning,
        Message = "A verified PayHere notification was stored but could not be applied: {Detail}")]
    private static partial void LogRetry(ILogger logger, string detail, Exception? exception);

    [LoggerMessage(
        EventId = 3518,
        Level = LogLevel.Error,
        Message = "A verified PayHere notification could not be applied and will not be retried: {Detail}")]
    private static partial void LogFailed(ILogger logger, string detail, Exception? exception);
}
