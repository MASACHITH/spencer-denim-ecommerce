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
/// No stack trace, no exception type: the detail is the JSON reader's message (it names the JSON path and
/// position, nothing the caller did not send) or the framework's own bad-request text.
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
            Detail = json is null
                ? badRequest.Message
                : $"The request body is not valid JSON for this endpoint: {json.Message}",
        };
        problem.Extensions["code"] = json is null ? "bad_request" : "malformed_json";

        httpContext.Response.StatusCode = badRequest.StatusCode;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
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
