using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Sales.Payments.PayPal;

namespace SpencerDenim.Api.Endpoints.Sales;

/// <summary>
/// PayPal's public endpoints (PAY-02; doc 11 P8): the three a buyer's browser is sent through, and the one
/// PayPal's servers call.
/// <para>
/// <b>The three browser routes are GETs, reached by redirect, and each ends in a redirect.</b> Start creates
/// (or finds) the PayPal order and sends the buyer to approve it; return is where PayPal sends them back, and
/// runs claim, capture and apply; cancel is where PayPal sends a buyer who backs out. They are anonymous
/// because the order link is the buyer's only credential, exactly as for the other order routes. <b>None of
/// them is proof of payment</b> — the return runs the capture, and only PayPal's capture response or a
/// verified webhook can settle an order. A redirect's <c>pay</c> hint only picks the sentence the order page
/// shows; the page reads the order's real status from the server.
/// </para>
/// <para>
/// <b>The webhook</b> takes the body raw, because PayPal's signature is over its exact bytes, and answers the
/// codes doc 08 §3.5 DB13 fixes: 503 when the signature could not be checked (nothing stored, PayPal resends),
/// 400 when it is invalid (nothing stored, a log line), 200 for a verified event whether it is new or a
/// duplicate.
/// </para>
/// <para>
/// Every response is <c>no-store</c>: the address carries the order token and must not sit in a cache.
/// </para>
/// </summary>
internal static class PayPalEndpoints
{
    /// <summary>A notification is a few KB; this is generous and stops anyone making us read a large body.</summary>
    private const int MaxWebhookBytes = 256 * 1024;

    public static IEndpointRouteBuilder MapPayPalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/payments/paypal").WithTags("PayPal");

        api.MapGet("/start/{publicId:guid}", StartAsync)
            .WithName("PayPalStart")
            .WithSummary("Send the buyer to PayPal to pay an order (PAY-02, doc 11 P8 step 1).")
            .WithDescription(
                "Anonymous: the order link's token is the credential. Finds or opens the order's PayPal "
                + "payment, creates the PayPal order if it has none yet (idempotently) and answers 302 to "
                + "PayPal's approval page. If the order can no longer be paid, is already being processed, "
                + "or PayPal is not available, answers 302 to the order page with a 'pay' hint. 404 for a "
                + "token that names nothing.")
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        api.MapGet("/return/{publicId:guid}", ReturnAsync)
            .WithName("PayPalReturn")
            .WithSummary("Where PayPal sends the buyer after they approve (PAY-02, doc 11 P8 steps 3 to 6).")
            .WithDescription(
                "Runs claim, capture and apply, then answers 302 to the order page with a 'pay' hint. The "
                + "return is never proof of payment: the capture response decides, and the order page shows "
                + "the order's real status. The 'token' PayPal appends is ignored.")
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        api.MapGet("/cancel/{publicId:guid}", CancelAsync)
            .WithName("PayPalCancel")
            .WithSummary("Where PayPal sends a buyer who backs out (PAY-02, doc 11 P8 step 9).")
            .WithDescription(
                "Closes the abandoned attempt so the next one starts at the current rate, then answers 302 to "
                + "the order page. Touches only an Initiated payment.")
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        api.MapPost("/webhook", WebhookAsync)
            .WithName("PayPalWebhook")
            .WithSummary("PayPal's notifications (PAY-02, doc 08 §3.5 DB13).")
            .WithDescription(
                "Called by PayPal, not by a browser. Anonymous and unauthenticated: the signature is the "
                + "authentication, checked with PayPal before anything is stored. 200 for a verified event "
                + "(new or already stored), 400 for headers that cannot be PayPal's (refused before any "
                + "throttle permit is spent), an invalid signature or a body that is not an event (nothing "
                + "stored), 429 when this address or the endpoint has used its budget (Retry-After says when), "
                + "503 when the signature could not be checked (nothing stored; PayPal resends).")
            .DisableAntiforgery()
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> StartAsync(
        HttpContext httpContext,
        IPayPalCheckoutFlow flow,
        PayPalEndpointThrottle throttle,
        Guid publicId,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        if (!throttle.TryAcquireCustomer(publicId, Address(httpContext)))
        {
            return TooManyRequests(httpContext, PayPalEndpointThrottle.CustomerRetryAfter);
        }

        return await flow.StartAsync(publicId, cancellationToken) switch
        {
            PayPalStartResult.Redirect redirect => TypedResults.Redirect(redirect.ApprovalUrl),
            PayPalStartResult.Back back => TypedResults.Redirect(PayPalLinks.OrderPagePath(publicId, back.Hint)),
            _ => TypedResults.NotFound(),
        };
    }

    private static async Task<IResult> ReturnAsync(
        HttpContext httpContext,
        IPayPalCheckoutFlow flow,
        PayPalEndpointThrottle throttle,
        Guid publicId,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        if (!throttle.TryAcquireCustomer(publicId, Address(httpContext)))
        {
            return TooManyRequests(httpContext, PayPalEndpointThrottle.CustomerRetryAfter);
        }

        // For the log only; bounded, because the browser controls it.
        var token = httpContext.Request.Query["token"].ToString();
        token = token.Length > 100 ? token[..100] : token;

        return Page(publicId, await flow.ReturnAsync(publicId, string.IsNullOrEmpty(token) ? null : token, cancellationToken));
    }

    private static async Task<IResult> CancelAsync(
        HttpContext httpContext,
        IPayPalCheckoutFlow flow,
        PayPalEndpointThrottle throttle,
        Guid publicId,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        if (!throttle.TryAcquireCustomer(publicId, Address(httpContext)))
        {
            return TooManyRequests(httpContext, PayPalEndpointThrottle.CustomerRetryAfter);
        }

        return Page(publicId, await flow.CancelAsync(publicId, cancellationToken));
    }

    private static async Task<IResult> WebhookAsync(
        HttpContext httpContext,
        IPayPalWebhookHandler handler,
        PayPalEndpointThrottle throttle,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        var headers = new PayPalWebhookHeaders(
            Header(httpContext, "PAYPAL-AUTH-ALGO"),
            Header(httpContext, "PAYPAL-CERT-URL"),
            Header(httpContext, "PAYPAL-TRANSMISSION-ID"),
            Header(httpContext, "PAYPAL-TRANSMISSION-SIG"),
            Header(httpContext, "PAYPAL-TRANSMISSION-TIME"));

        // Refused before anything is spent (#166). The throttle's endpoint-wide bucket is also what PayPal's
        // real deliveries draw on, and pending captures settle only by webhook, so a request that cannot be a
        // PayPal notification must not cost a permit: about two junk POSTs a second used to empty the bucket and
        // earn the real ones a 429. The same 400 the gateway would have given, minus reading the body.
        if (!headers.LooksLikePayPal())
        {
            return TypedResults.StatusCode(StatusCodes.Status400BadRequest);
        }

        if (!throttle.TryAcquireWebhook(Address(httpContext)))
        {
            return TooManyRequests(httpContext, PayPalEndpointThrottle.WebhookRetryAfter);
        }

        // Lowered before the body is touched: nobody should be able to make this route read a large one.
        var feature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = MaxWebhookBytes;
        }

        string body;

        try
        {
            body = await ReadBodyAsync(httpContext.Request, cancellationToken);
        }
        catch (BadHttpRequestException)
        {
            // Over the limit, or the connection broke mid-body.
            return TypedResults.StatusCode(StatusCodes.Status400BadRequest);
        }

        return await handler.HandleAsync(headers, body, cancellationToken) switch
        {
            PayPalWebhookResult.Accepted or PayPalWebhookResult.Duplicate => TypedResults.Ok(),
            PayPalWebhookResult.VerificationUnavailable => TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable),
            _ => TypedResults.StatusCode(StatusCodes.Status400BadRequest),
        };
    }

    /// <summary>The order page, with the hint — or 404 when there is no such order.</summary>
    private static IResult Page(Guid publicId, PayPalReturnResult result) =>
        result.Found
            ? TypedResults.Redirect(PayPalLinks.OrderPagePath(publicId, result.Hint))
            : TypedResults.NotFound();

    /// <summary>The body exactly as received: PayPal's signature is over these bytes.</summary>
    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);

        if (buffer.Length > MaxWebhookBytes)
        {
            throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
        }

        // Lenient decoding: a malformed byte becomes U+FFFD and the body then fails verification, which is the
        // right outcome — it is not what PayPal signed.
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Header(HttpContext httpContext, string name) =>
        httpContext.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : string.Empty;

    private static string? Address(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString();

    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }

    private static Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult TooManyRequests(HttpContext httpContext, TimeSpan retryAfter)
    {
        httpContext.Response.Headers.RetryAfter =
            ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        return TypedResults.StatusCode(StatusCodes.Status429TooManyRequests);
    }
}
