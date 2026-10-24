using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Application.Abstractions.Auditing;

namespace SpencerDenim.Api.Http;

/// <summary>
/// The three things every mutating admin JSON endpoint has to do before it reaches a use case: check the
/// antiforgery token, read the body without turning a typo into a 500, and work out who is asking.
/// </summary>
public static class AdminWriteRequest
{
    /// <summary>The header the antiforgery service is configured for (<c>IdentityRegistration</c>).</summary>
    public const string AntiforgeryHeader = "X-XSRF-TOKEN";

    /// <summary>
    /// Reads and parses a JSON request body, answering with a 400 problem for anything malformed.
    /// <para>
    /// <b>Why this is not plain minimal-API model binding.</b> Issue <b>#47</b> is open because
    /// <c>POST /api/admin/auth/login</c> with a body of <c>{bad</c> — or with <c>{"email":123}</c>, a
    /// mistyped field rather than broken syntax — answers <b>500</b>. The cause is not the login endpoints:
    /// minimal APIs are configured to <i>throw</i> on a bad request in Development, and
    /// <c>UseExceptionHandler</c> turns the throw into a 500, while outside Development the same input
    /// short-circuits to a bodyless 400. So the status an admin screen gets depends on the environment,
    /// and in neither case does it say which field was wrong.
    /// </para>
    /// <para>
    /// Reading the body here makes the answer the same everywhere and nameable: one 400 with a
    /// machine-readable <c>code</c>, in Development and in Production alike. It is deliberately scoped to
    /// the endpoints that use it rather than fixed globally — the global mapping is #47's own change, with
    /// #47's own regression tests over the auth endpoints, and quietly altering those endpoints' behaviour
    /// from inside W1-12 would land it with no owner and no test naming it.
    /// </para>
    /// </summary>
    public static async Task<(TBody? Body, IResult? Problem)> ReadJsonAsync<TBody>(
        HttpRequest request,
        CancellationToken cancellationToken)
        where TBody : class
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.HasJsonContentType())
        {
            return (null, Problem(
                StatusCodes.Status415UnsupportedMediaType,
                "Not JSON",
                "Send the request body as application/json.",
                "not_json"));
        }

        // A charset the runtime has no encoding for makes ReadFromJsonAsync throw InvalidOperationException
        // (issue #147), which is neither of the exceptions caught below, so it answered 500 for a header the
        // caller chose. It is decided from the header, with the parser the framework itself uses, rather than by
        // catching InvalidOperationException: that type is also what a bug inside the server throws, and
        // reporting one of those as "your request was wrong" would hide it.
        var contentType = request.GetTypedHeaders().ContentType;
        if (contentType is { Charset.HasValue: true, Encoding: null })
        {
            return (null, Problem(
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported charset",
                "Send the request body as UTF-8.",
                "unsupported_charset"));
        }

        try
        {
            var body = await request.ReadFromJsonAsync<TBody>(cancellationToken);

            return body is null
                ? (null, Problem(
                    StatusCodes.Status400BadRequest,
                    "Empty request body",
                    "The request body is empty, or is the JSON literal null.",
                    "empty_body"))
                : (body, null);
        }
        catch (JsonException exception)
        {
            // The message names the JSON path and position, which is the useful part of it; it contains
            // nothing the caller did not send.
            return (null, Problem(
                StatusCodes.Status400BadRequest,
                "Malformed JSON",
                $"The request body is not valid JSON for this endpoint: {exception.Message}",
                "malformed_json"));
        }
        catch (BadHttpRequestException exception)
        {
            return (null, Problem(
                StatusCodes.Status400BadRequest,
                "Bad request body",
                exception.Message,
                "malformed_json"));
        }
    }

    /// <summary>
    /// Validates the antiforgery token from the request header, exactly as <c>AdminAuthEndpoints.Logout</c>
    /// and <c>AdminMediaEndpoints</c> do, and answers with a readable problem rather than an empty 400.
    /// </summary>
    public static async Task<IResult?> ValidateAntiforgeryAsync(HttpContext httpContext, IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(antiforgery);

        if (!httpContext.Request.Headers.TryGetValue(AntiforgeryHeader, out var token)
            || string.IsNullOrWhiteSpace(token))
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

    /// <summary>
    /// Who is asking and from where, for <c>ops.AuditLogs</c> (doc 08 §3.8). The correlation id is the
    /// request's own trace identifier, so an audit row and the request log line can be put side by side.
    /// </summary>
    public static AdminActor Actor(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var user = httpContext.User;
        var userId = int.TryParse(
            user.FindFirstValue(ClaimTypes.NameIdentifier),
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : (int?)null;

        var userAgent = httpContext.Request.Headers.UserAgent.ToString();

        return new AdminActor(
            userId,
            user.FindFirstValue(ClaimTypes.Email),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            string.IsNullOrEmpty(userAgent) ? null : Truncate(userAgent, UserAgentMaxLength),
            Truncate(httpContext.TraceIdentifier, CorrelationIdMaxLength));
    }

    /// <summary>
    /// The row version a caller is working from: the <c>If-Match</c> header if it sent one, otherwise the
    /// body's own field.
    /// <para>
    /// Both, because the two have different jobs. <c>Category.RowVersion</c>'s own documentation calls it
    /// "exchanged with the admin API as an ETag", and <c>If-Match</c> is the only way a DELETE can carry a
    /// precondition without a body; a field in the body is what a form naturally round-trips, since these
    /// screens edit from a tree payload rather than from a per-row GET with an <c>ETag</c> of its own. The
    /// header wins when both are present, as a precondition should.
    /// </para>
    /// </summary>
    public static string? RowVersion(HttpRequest request, string? fromBody)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Headers.TryGetValue("If-Match", out var header))
        {
            return fromBody;
        }

        var value = header.ToString().Trim();
        if (value.Length == 0)
        {
            return fromBody;
        }

        // Tolerate an ETag's quotes and weak marker, which is how a browser or a proxy may send it back.
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        return value.Trim('"');
    }

    /// <summary>
    /// An RFC 9457 body with a machine-readable <c>code</c> beside the sentence, so an admin screen can
    /// branch on the reason without parsing English — the convention <c>AdminMediaEndpoints</c> set.
    /// </summary>
    public static ProblemHttpResult Problem(int statusCode, string title, string detail, string code) =>
        TypedResults.Problem(
            title: title,
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code });

    /// <summary><c>ops.AuditLogs.UserAgent</c> is <c>nvarchar(400)</c>.</summary>
    private const int UserAgentMaxLength = 400;

    /// <summary><c>ops.AuditLogs.CorrelationId</c> is <c>varchar(64)</c>.</summary>
    private const int CorrelationIdMaxLength = 64;

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
