using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Branding;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayPal;

/// <summary>
/// <see cref="IPayPalGateway"/> over PayPal's REST API (Orders v2, OAuth 2 and the webhook verification call),
/// as a typed <see cref="HttpClient"/>.
/// <para>
/// <b>Everything environmental is read per call from the shop's settings</b> — which PayPal (sandbox or
/// live), the client id and secret, the webhook id — so a Super Admin's change takes effect on the next
/// request with no restart. When PayPal is not configured every call answers <c>Unavailable</c> without
/// making a request.
/// </para>
/// <para>
/// <b>What is deliberately not here.</b> No retry loop: the capture POST in particular is sent once, because
/// whether to ask again is the claim's and the recovery job's decision, and an HTTP-level retry would hide
/// the very timeouts they exist to handle. (A <c>401</c> is the one exception — PayPal did not process that
/// request, so it is sent again once with a fresh token, under the same <c>PayPal-Request-Id</c>.) No secret,
/// access token or whole payload is ever logged; the log lines carry a status code and an operation name.
/// </para>
/// <para>
/// <b>The access token is cached per credential</b> (<see cref="PayPalTokenCache"/>) until a minute before it
/// expires, so a busy minute is not a token request per call.
/// </para>
/// </summary>
internal sealed partial class PayPalHttpGateway(
    HttpClient http,
    IPayPalSettingsReader settingsReader,
    PayPalTokenCache tokens,
    IClock clock,
    ILogger<PayPalHttpGateway> logger) : IPayPalGateway
{
    /// <summary>How long a single call to PayPal may take. A buyer is waiting on the capture.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private const string NotConfigured = "PayPal is not configured.";

    public async Task<PayPalCreateOrderResult> CreateOrderAsync(
        PayPalCreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await settingsReader.ReadAsync(cancellationToken);

        if (!settings.HasCredentials)
        {
            return new PayPalCreateOrderResult.Unavailable(NotConfigured);
        }

        using var response = await SendAsync(
            settings,
            HttpMethod.Post,
            "/v2/checkout/orders",
            CreateOrderBody(request),
            request.RequestId,
            cancellationToken);

        if (response.Failure is { } failure)
        {
            LogUnreachable(logger, "create order", failure);
            return new PayPalCreateOrderResult.Unavailable(failure);
        }

        if (response.IsSuccess && response.Json is { } json)
        {
            var id = Text(json.RootElement, "id");

            if (string.IsNullOrWhiteSpace(id))
            {
                LogUnreadable(logger, "create order", (int)response.Status);
                return new PayPalCreateOrderResult.Unavailable("PayPal's answer could not be read.");
            }

            return new PayPalCreateOrderResult.Created(id, ApprovalUrl(settings, json.RootElement, id));
        }

        if (response.IsServerSide)
        {
            LogServerSide(logger, "create order", (int)response.Status);
            return new PayPalCreateOrderResult.Unavailable($"PayPal answered {(int)response.Status}.");
        }

        LogRejected(logger, "create order", (int)response.Status, response.Issue ?? "unknown");
        return new PayPalCreateOrderResult.Rejected(response.Issue ?? $"PayPal answered {(int)response.Status}.");
    }

    public async Task<PayPalGetOrderResult> GetOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        var settings = await settingsReader.ReadAsync(cancellationToken);

        if (!settings.HasCredentials)
        {
            return new PayPalGetOrderResult.Unavailable(NotConfigured);
        }

        using var response = await SendAsync(
            settings,
            HttpMethod.Get,
            $"/v2/checkout/orders/{Uri.EscapeDataString(orderId)}",
            body: null,
            requestId: null,
            cancellationToken);

        if (response.Failure is { } failure)
        {
            LogUnreachable(logger, "get order", failure);
            return new PayPalGetOrderResult.Unavailable(failure);
        }

        if (response.IsSuccess && response.Json is { } json)
        {
            return new PayPalGetOrderResult.Found(Snapshot(json.RootElement, orderId));
        }

        if (response.Status == HttpStatusCode.NotFound)
        {
            return new PayPalGetOrderResult.NotFound();
        }

        LogServerSide(logger, "get order", (int)response.Status);
        return new PayPalGetOrderResult.Unavailable($"PayPal answered {(int)response.Status}.");
    }

    public async Task<PayPalCaptureCallResult> CaptureOrderAsync(
        string orderId,
        string requestId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var settings = await settingsReader.ReadAsync(cancellationToken);

        if (!settings.HasCredentials)
        {
            return new PayPalCaptureCallResult.Unavailable(NotConfigured);
        }

        using var response = await SendAsync(
            settings,
            HttpMethod.Post,
            $"/v2/checkout/orders/{Uri.EscapeDataString(orderId)}/capture",
            "{}",
            requestId,
            cancellationToken);

        if (response.Failure is { } failure)
        {
            LogUnreachable(logger, "capture", failure);
            return new PayPalCaptureCallResult.Unavailable(failure);
        }

        if (response.IsSuccess && response.Json is { } json)
        {
            var snapshot = Snapshot(json.RootElement, orderId);

            // A capture answered in the minimal form carries no capture details. Read the order for them: a
            // GET is safe to make, and a "captured" with nothing to apply would leave the payment Capturing
            // for the recovery job to find an hour later.
            if (snapshot.Captures.Count == 0)
            {
                var read = await GetOrderAsync(orderId, cancellationToken);

                if (read is PayPalGetOrderResult.Found found)
                {
                    snapshot = found.Order;
                }
            }

            return new PayPalCaptureCallResult.Captured(snapshot);
        }

        if (response.Status == HttpStatusCode.NotFound)
        {
            return new PayPalCaptureCallResult.OrderNotFound();
        }

        if (response.Status == HttpStatusCode.UnprocessableEntity)
        {
            switch (response.Issue)
            {
                case "ORDER_ALREADY_CAPTURED":
                    return new PayPalCaptureCallResult.AlreadyCaptured();

                case "ORDER_NOT_APPROVED" or "PAYER_ACTION_REQUIRED":
                    return new PayPalCaptureCallResult.NotApproved();

                case "INSTRUMENT_DECLINED" or "TRANSACTION_REFUSED" or "PAYER_CANNOT_PAY" or "PAYER_ACCOUNT_RESTRICTED":
                    return new PayPalCaptureCallResult.Declined(response.Issue);
            }
        }

        if (response.IsServerSide)
        {
            LogServerSide(logger, "capture", (int)response.Status);
            return new PayPalCaptureCallResult.Unavailable($"PayPal answered {(int)response.Status}.");
        }

        LogRejected(logger, "capture", (int)response.Status, response.Issue ?? "unknown");
        return new PayPalCaptureCallResult.Rejected(response.Issue ?? $"PayPal answered {(int)response.Status}.");
    }

    public async Task<PayPalWebhookVerification> VerifyWebhookAsync(
        PayPalWebhookHeaders headers,
        string rawBody,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rawBody);

        // Cheap refusals first: a request that does not even look like a PayPal notification never costs a
        // call to PayPal, which is what stops an attacker turning this endpoint into a way to spend our rate
        // limit. The certificate address has to be PayPal's own — PayPal's verification fetches it.
        if (!headers.LooksLikePayPal())
        {
            return PayPalWebhookVerification.Invalid;
        }

        var settings = await settingsReader.ReadAsync(cancellationToken);

        if (!settings.HasCredentials)
        {
            return PayPalWebhookVerification.Error;
        }

        using var response = await SendAsync(
            settings,
            HttpMethod.Post,
            "/v1/notifications/verify-webhook-signature",
            VerifyBody(headers, settings.WebhookId!, rawBody),
            requestId: null,
            cancellationToken);

        if (response.Failure is { } failure)
        {
            LogUnreachable(logger, "verify webhook", failure);
            return PayPalWebhookVerification.Error;
        }

        if (response.IsSuccess && response.Json is { } json)
        {
            return string.Equals(
                Text(json.RootElement, "verification_status"),
                "SUCCESS",
                StringComparison.OrdinalIgnoreCase)
                ? PayPalWebhookVerification.Verified
                : PayPalWebhookVerification.Invalid;
        }

        // 400 / 422: PayPal looked at the request and would not vouch for it.
        if (response.Status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            LogRejected(logger, "verify webhook", (int)response.Status, response.Issue ?? "unknown");
            return PayPalWebhookVerification.Invalid;
        }

        // Anything else — a 401 for our own credentials, a 5xx — is "we could not find out".
        LogServerSide(logger, "verify webhook", (int)response.Status);
        return PayPalWebhookVerification.Error;
    }

    /// <summary>
    /// The create-order body: intent CAPTURE, one purchase unit in USD, and the buyer's way back.
    /// </summary>
    /// <remarks>
    /// <b>The delivery address the shop collected is sent, fixed</b> (owner decision 2026-10-21): with
    /// <c>SET_PROVIDED_ADDRESS</c> PayPal shows the buyer where the parcel goes and does not let them change it,
    /// for Sri Lankan and overseas addresses alike. A store pickup has no address and keeps <c>NO_SHIPPING</c>.
    /// No payer details are sent. <c>custom_id</c> is ours and comes
    /// back in webhooks; <c>invoice_id</c> is left out on purpose, because PayPal refuses a second order with
    /// an invoice id it has seen, which a retry after a decline would be.
    /// </remarks>
    private static string CreateOrderBody(PayPalCreateOrderRequest request)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("intent", "CAPTURE");

            writer.WriteStartArray("purchase_units");
            writer.WriteStartObject();
            writer.WriteString("custom_id", request.CustomId);
            writer.WriteString("description", request.Description);
            writer.WriteStartObject("amount");
            writer.WriteString("currency_code", request.Currency);
            writer.WriteString("value", PayPalUsdAmount.Format(request.Amount));
            writer.WriteEndObject();

            if (request.Shipping is { } shipping)
            {
                WriteShipping(writer, shipping);
            }

            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartObject("payment_source");
            writer.WriteStartObject("paypal");
            writer.WriteStartObject("experience_context");
            writer.WriteString("brand_name", StoreBrand.Name);
            writer.WriteString("landing_page", "NO_PREFERENCE");
            writer.WriteString(
                "shipping_preference",
                request.Shipping is null ? "NO_SHIPPING" : "SET_PROVIDED_ADDRESS");
            writer.WriteString("user_action", "PAY_NOW");
            writer.WriteString("return_url", request.ReturnUrl);
            writer.WriteString("cancel_url", request.CancelUrl);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// <c>purchase_units[0].shipping</c> (owner decision 2026-10-21): the recipient and the address in PayPal's
    /// fields — city in <c>admin_area_2</c>, state or region in <c>admin_area_1</c>, and the ISO country code. An
    /// optional part the order does not have is left out rather than sent empty, which PayPal refuses.
    /// </summary>
    private static void WriteShipping(Utf8JsonWriter writer, PayPalShipping shipping)
    {
        writer.WriteStartObject("shipping");

        writer.WriteStartObject("name");
        writer.WriteString("full_name", shipping.FullName);
        writer.WriteEndObject();

        writer.WriteStartObject("address");
        writer.WriteString("address_line_1", shipping.Line1);
        WriteIfPresent(writer, "address_line_2", shipping.Line2);
        writer.WriteString("admin_area_2", shipping.City);
        WriteIfPresent(writer, "admin_area_1", shipping.Region);
        WriteIfPresent(writer, "postal_code", shipping.PostalCode);
        writer.WriteString("country_code", shipping.CountryCode);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(name, value);
        }
    }

    /// <summary>
    /// <c>verify-webhook-signature</c>'s body. The notification goes in <b>as the bytes PayPal sent</b>
    /// (<c>WriteRawValue</c>) and not as a re-serialised object: the signature is over those bytes, and a
    /// parse-and-write would be free to change a number's spelling or a string's escapes.
    /// </summary>
    private static string VerifyBody(PayPalWebhookHeaders headers, string webhookId, string rawBody)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("auth_algo", headers.AuthAlgo);
            writer.WriteString("cert_url", headers.CertUrl);
            writer.WriteString("transmission_id", headers.TransmissionId);
            writer.WriteString("transmission_sig", headers.TransmissionSig);
            writer.WriteString("transmission_time", headers.TransmissionTime);
            writer.WriteString("webhook_id", webhookId);
            writer.WritePropertyName("webhook_event");
            writer.WriteRawValue(rawBody, skipInputValidation: false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The address the buyer approves at: PayPal's own <c>payer-action</c> (or <c>approve</c>) link when it
    /// gave one on PayPal's domain, and otherwise the documented <c>checkoutnow</c> address for the order id.
    /// </summary>
    private static string ApprovalUrl(PayPalSettings settings, JsonElement order, string orderId)
    {
        if (order.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in links.EnumerateArray())
            {
                if (Text(link, "rel") is "payer-action" or "approve"
                    && Text(link, "href") is { } href
                    && Uri.TryCreate(href, UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps
                    && uri.Host.EndsWith(".paypal.com", StringComparison.OrdinalIgnoreCase))
                {
                    return uri.AbsoluteUri;
                }
            }
        }

        return CheckoutUrl(settings, orderId);
    }

    /// <summary>The documented approval address for an order id: <c>…/checkoutnow?token={id}</c>.</summary>
    internal static string CheckoutUrl(PayPalSettings settings, string orderId) =>
        $"{settings.CheckoutBaseUrl}/checkoutnow?token={Uri.EscapeDataString(orderId)}";

    /// <summary>
    /// An order and its captures, read defensively: a field PayPal omits is null, never an exception.
    /// </summary>
    internal static PayPalOrderSnapshot Snapshot(JsonElement order, string fallbackId)
    {
        var captures = new List<PayPalCaptureSnapshot>();

        if (order.TryGetProperty("purchase_units", out var units) && units.ValueKind == JsonValueKind.Array)
        {
            foreach (var unit in units.EnumerateArray())
            {
                if (unit.ValueKind == JsonValueKind.Object
                    && unit.TryGetProperty("payments", out var payments)
                    && payments.ValueKind == JsonValueKind.Object
                    && payments.TryGetProperty("captures", out var list)
                    && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var capture in list.EnumerateArray())
                    {
                        if (capture.ValueKind == JsonValueKind.Object && Text(capture, "id") is { } captureId)
                        {
                            captures.Add(ToCapture(capture, captureId));
                        }
                    }
                }
            }
        }

        string? payerId = null;

        if (order.TryGetProperty("payer", out var payer) && payer.ValueKind == JsonValueKind.Object)
        {
            payerId = Text(payer, "payer_id");
        }

        return new PayPalOrderSnapshot(
            Text(order, "id") ?? fallbackId,
            OrderStatus(Text(order, "status")),
            payerId,
            captures);
    }

    private static PayPalCaptureSnapshot ToCapture(JsonElement capture, string captureId)
    {
        decimal? amount = null;
        string? currency = null;

        if (capture.TryGetProperty("amount", out var money) && money.ValueKind == JsonValueKind.Object)
        {
            currency = Text(money, "currency_code");

            if (Text(money, "value") is { } value
                && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                amount = parsed;
            }
        }

        string? reason = null;

        if (capture.TryGetProperty("status_details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            reason = Text(details, "reason");
        }

        return new PayPalCaptureSnapshot(captureId, CaptureStatus(Text(capture, "status")), amount, currency, reason);
    }

    private static PayPalOrderStatus OrderStatus(string? status) =>
        status?.ToUpperInvariant() switch
        {
            "CREATED" => PayPalOrderStatus.Created,
            "SAVED" => PayPalOrderStatus.Saved,
            "APPROVED" => PayPalOrderStatus.Approved,
            "VOIDED" => PayPalOrderStatus.Voided,
            "COMPLETED" => PayPalOrderStatus.Completed,
            "PAYER_ACTION_REQUIRED" => PayPalOrderStatus.PayerActionRequired,
            _ => PayPalOrderStatus.Unknown,
        };

    private static PayPalCaptureStatus CaptureStatus(string? status) =>
        status?.ToUpperInvariant() switch
        {
            "COMPLETED" => PayPalCaptureStatus.Completed,
            "PENDING" => PayPalCaptureStatus.Pending,
            "DECLINED" or "DENIED" => PayPalCaptureStatus.Declined,
            "FAILED" => PayPalCaptureStatus.Failed,
            "REFUNDED" => PayPalCaptureStatus.Refunded,
            "PARTIALLY_REFUNDED" => PayPalCaptureStatus.PartiallyRefunded,
            _ => PayPalCaptureStatus.Unknown,
        };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// One authenticated request. Returns a <see cref="PayPalResponse"/> and never throws for a network
    /// problem: a timeout, a refused connection or a certificate failure is a <c>Failure</c> the caller maps
    /// to <c>Unavailable</c>.
    /// </summary>
    private async Task<PayPalResponse> SendAsync(
        PayPalSettings settings,
        HttpMethod method,
        string path,
        string? body,
        string? requestId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string? token;

            try
            {
                token = await tokens.GetAsync(
                    settings,
                    clock.UtcNow.UtcDateTime,
                    (credentials, ct) => RequestTokenAsync(settings, credentials, ct),
                    forceRefresh: attempt > 0,
                    cancellationToken);
            }
            catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
            {
                return PayPalResponse.Failed(Describe(exception));
            }

            if (token is null)
            {
                return PayPalResponse.Failed("PayPal rejected the shop's credentials.");
            }

            using var request = new HttpRequestMessage(method, settings.ApiBaseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (requestId is not null)
            {
                request.Headers.Add("PayPal-Request-Id", requestId);
            }

            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;

            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
            {
                return PayPalResponse.Failed(Describe(exception));
            }

            // 401: the token was refused (it may have been revoked or the clock disagrees). PayPal did not
            // process the request, so sending it again with a fresh token — under the same request id — is
            // safe. Once.
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                continue;
            }

            try
            {
                return await PayPalResponse.ReadAsync(response, cancellationToken);
            }
            catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
            {
                response.Dispose();
                return PayPalResponse.Failed(Describe(exception));
            }
        }

        return PayPalResponse.Failed("PayPal rejected the shop's credentials.");
    }

    /// <summary>
    /// <c>POST /v1/oauth2/token</c> with the client id and secret as HTTP Basic credentials. Returns null
    /// when PayPal refuses them.
    /// </summary>
    private async Task<PayPalToken?> RequestTokenAsync(
        PayPalSettings settings,
        PayPalCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.ApiBaseUrl + "/v1/oauth2/token")
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.ClientId}:{credentials.ClientSecret}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            LogTokenRefused(logger, (int)response.StatusCode);

            // A 4xx is PayPal saying no to these credentials. A 5xx is PayPal being ill, which is not the
            // credentials' fault and must not be cached as such.
            if ((int)response.StatusCode >= 500)
            {
                throw new HttpRequestException($"PayPal's token endpoint answered {(int)response.StatusCode}.");
            }

            return null;
        }

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var accessToken = Text(json.RootElement, "access_token");

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var seconds = json.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var value)
            ? value
            : 300;

        return new PayPalToken(accessToken, TimeSpan.FromSeconds(Math.Max(seconds, 1)));
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is PayPal being unreachable — a timeout, a refused or reset
    /// connection, a TLS failure, a body that did not arrive — rather than the host shutting down.
    /// </summary>
    private static bool IsNetworkFailure(Exception exception, CancellationToken cancellationToken) =>
        exception switch
        {
            OperationCanceledException when cancellationToken.IsCancellationRequested => false,
            OperationCanceledException => true,
            HttpRequestException => true,
            IOException => true,
            JsonException => true,
            _ => false,
        };

    /// <summary>A short description for a log line: the exception's type, never its message (which can carry a URL).</summary>
    private static string Describe(Exception exception) =>
        exception is OperationCanceledException
            ? "The request to PayPal timed out."
            : $"PayPal could not be reached ({exception.GetType().Name}).";

    [LoggerMessage(
        EventId = 3501,
        Level = LogLevel.Warning,
        Message = "PayPal could not be reached for '{Operation}': {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string operation, string reason);

    [LoggerMessage(
        EventId = 3502,
        Level = LogLevel.Warning,
        Message = "PayPal answered {Status} to '{Operation}', which is a fault on PayPal's side or ours (a refused "
            + "token, a 5xx) and not a decision about the payment.")]
    private static partial void LogServerSide(ILogger logger, string operation, int status);

    [LoggerMessage(
        EventId = 3503,
        Level = LogLevel.Warning,
        Message = "PayPal refused '{Operation}' with {Status} ({Issue}).")]
    private static partial void LogRejected(ILogger logger, string operation, int status, string issue);

    [LoggerMessage(
        EventId = 3504,
        Level = LogLevel.Warning,
        Message = "PayPal answered {Status} to '{Operation}' but the answer could not be read.")]
    private static partial void LogUnreadable(ILogger logger, string operation, int status);

    [LoggerMessage(
        EventId = 3505,
        Level = LogLevel.Error,
        Message = "PayPal refused the shop's client credentials ({Status}). Check Payments.PayPal.ClientId and "
            + "ClientSecret, and that Payments.PayPal.Mode matches the environment they were issued for.")]
    private static partial void LogTokenRefused(ILogger logger, int status);

    /// <summary>
    /// What came back, read once. <see cref="Failure"/> is set when nothing came back at all.
    /// </summary>
    private sealed class PayPalResponse : IDisposable
    {
        private PayPalResponse()
        {
        }

        public string? Failure { get; private init; }

        public HttpStatusCode Status { get; private init; }

        public JsonDocument? Json { get; private init; }

        /// <summary>PayPal's first error <c>issue</c> code, e.g. <c>ORDER_ALREADY_CAPTURED</c>.</summary>
        public string? Issue { get; private init; }

        public bool IsSuccess => Failure is null && (int)Status is >= 200 and < 300;

        /// <summary>A 5xx, or a 429: PayPal's side, and worth trying again later.</summary>
        public bool IsServerSide => (int)Status >= 500 || Status == HttpStatusCode.TooManyRequests
            || Status == HttpStatusCode.Unauthorized || Status == HttpStatusCode.Forbidden;

        public static PayPalResponse Failed(string reason) => new() { Failure = reason };

        public static async Task<PayPalResponse> ReadAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            using (response)
            {
                JsonDocument? json = null;
                string? issue = null;

                if (response.Content.Headers.ContentLength is not 0)
                {
                    try
                    {
                        json = await JsonDocument.ParseAsync(
                            await response.Content.ReadAsStreamAsync(cancellationToken),
                            cancellationToken: cancellationToken);
                    }
                    catch (JsonException)
                    {
                        // An empty or non-JSON body (a gateway page): the status is all there is.
                        json = null;
                    }
                }

                if (json is not null && !((int)response.StatusCode is >= 200 and < 300))
                {
                    issue = FirstIssue(json.RootElement);
                }

                return new PayPalResponse { Status = response.StatusCode, Json = json, Issue = issue };
            }
        }

        public void Dispose() => Json?.Dispose();

        /// <summary>
        /// <c>details[0].issue</c>, else <c>name</c>. PayPal puts the specific reason in the first.
        /// </summary>
        private static string? FirstIssue(JsonElement error)
        {
            if (error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (error.TryGetProperty("details", out var details)
                && details.ValueKind == JsonValueKind.Array
                && details.GetArrayLength() > 0
                && details[0].ValueKind == JsonValueKind.Object
                && details[0].TryGetProperty("issue", out var issue)
                && issue.ValueKind == JsonValueKind.String)
            {
                return issue.GetString();
            }

            return error.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
    }
}

/// <summary>An access token and how long PayPal says it lives.</summary>
internal sealed record PayPalToken(string AccessToken, TimeSpan LifeTime);

/// <summary>
/// The client id and secret, with a <c>ToString</c> that prints neither — a credential in a log line is a
/// credential leaked.
/// </summary>
internal sealed class PayPalCredentials(string clientId, string clientSecret)
{
    public string ClientId { get; } = clientId;

    public string ClientSecret { get; } = clientSecret;

    public override string ToString() => "PayPalCredentials[redacted]";
}

/// <summary>
/// The OAuth access token, cached per credential until a minute before it expires (PayPal tokens live for
/// about nine hours).
/// <para>
/// <b>A singleton, keyed by a hash of the mode, the client id and the secret</b> — so a Super Admin who
/// switches from sandbox to live, or pastes a new secret, gets a fresh token on the next call and never
/// reuses one issued for the old credentials, and the secret itself is never a dictionary key. A semaphore
/// per process keeps a burst of calls from each requesting a token.
/// </para>
/// </summary>
internal sealed class PayPalTokenCache : IDisposable
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _key;
    private string? _token;
    private DateTime _expiresAtUtc;

    /// <summary>
    /// The access token for these credentials, from the cache or fetched with <paramref name="fetch"/>; null
    /// when PayPal refuses the credentials. An exception from <paramref name="fetch"/> (PayPal unreachable)
    /// propagates and nothing is cached.
    /// </summary>
    public async Task<string?> GetAsync(
        PayPalSettings settings,
        DateTime nowUtc,
        Func<PayPalCredentials, CancellationToken, Task<PayPalToken?>> fetch,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var key = KeyFor(settings);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (!forceRefresh && _token is not null && _key == key && _expiresAtUtc - Margin > nowUtc)
            {
                return _token;
            }

            var token = await fetch(new PayPalCredentials(settings.ClientId!, settings.ClientSecret!), cancellationToken);

            if (token is null)
            {
                _key = null;
                _token = null;
                return null;
            }

            _key = key;
            _token = token.AccessToken;
            _expiresAtUtc = nowUtc + token.LifeTime;

            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static string KeyFor(PayPalSettings settings) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{settings.Mode}\u0000{settings.ClientId}\u0000{settings.ClientSecret}")));
}
