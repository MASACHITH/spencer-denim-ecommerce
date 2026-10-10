using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SpencerDenim.Api.Http;

/// <summary>
/// Issue <b>#47</b>: a body minimal-API binding cannot read — <c>{bad</c>, or <c>{"email":123}</c> on the
/// login endpoint — answers <b>400 with a problem document</b>, in every environment.
/// <para>
/// <b>What it was.</b> Minimal APIs throw <see cref="BadHttpRequestException"/> on a bad body in
/// Development (<c>RouteHandlerOptions.ThrowOnBadRequest</c> defaults to "is Development") and
/// <c>UseExceptionHandler</c> turned the throw into a <b>500</b>; outside Development the same request
/// short-circuited to a <b>bodyless 400</b>. So the status depended on the environment, and neither said what
/// was wrong. <see cref="AddApiBadRequestHandling"/> makes the framework throw everywhere and this handler
/// answers the throw, so every endpoint that binds a body behaves the same and the same way as
/// <see cref="AdminWriteRequest.ReadJsonAsync{TBody}"/>: a 400 (or the exception's own 4xx, e.g. 413) with a
/// machine-readable <c>code</c>.
/// </para>
/// <para>
/// No stack trace, no exception type, no framework wording: the detail is fixed text plus, for bad JSON, the JSON
/// path the reader stopped at.
/// </para>
/// </summary>
internal sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        var json = FindJsonException(badRequest);
        var problem = new ProblemDetails
        {
            Status = badRequest.StatusCode,
            Title = json is null ? "Bad request" : "Malformed JSON",

            // Fixed wording, never the exception's own message: the framework's text and System.Text.Json's name
            // internal CLR types ("could not be converted to SpencerDenim.Api.Endpoints.Admin.AdminLoginRequest",
            // "Implicit body inferred for parameter ... Did you mean to use a Service instead?"), which tells a
            // stranger how the server is built. The JSON path is the part that helps the caller, and it is
            // something they sent.
            Detail = json is null
                ? "The request could not be read. Check the body and the parameters."
                : json.Path is { Length: > 0 } path
                    ? $"The request body is not valid JSON for this endpoint (at {path})."
                    : "The request body is not valid JSON for this endpoint.",
        };
        problem.Extensions["code"] = json is null ? "bad_request" : "malformed_json";

        httpContext.Response.StatusCode = badRequest.StatusCode;

        if (await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        }))
        {
            return true;
        }

        // No registered writer accepts the caller's Accept header (text/html, application/xml): answer with the
        // problem document anyway rather than a bodyless 400 and an "unhandled exception" error in the log.
        await httpContext.Response.WriteAsJsonAsync(
            problem, options: null, contentType: "application/problem+json", cancellationToken);

        return true;
    }

    private static JsonException? FindJsonException(Exception exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is JsonException json)
            {
                return json;
            }
        }

        return null;
    }
}

internal static class BadRequestHandlingSetup
{
    public static IServiceCollection AddApiBadRequestHandling(this IServiceCollection services)
    {
        // Throw in every environment, not only Development, so a bad body always reaches the handler above
        // rather than turning into a bodyless 400 in Production.
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddExceptionHandler<BadRequestExceptionHandler>();

        return services;
    }
}
