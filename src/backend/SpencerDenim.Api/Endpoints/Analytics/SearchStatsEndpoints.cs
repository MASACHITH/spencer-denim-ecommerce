using Microsoft.AspNetCore.Antiforgery;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Analytics.Search;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Analytics;

/// <summary>
/// SRCH-01's popular searches (issue #78; doc 08 DB25, §7.6): the storefront's beacon for a submitted search, the
/// public list, and the staff hide list.
/// <para>
/// <b>The beacon cannot slow or break a search</b>, for the reason the page-view beacon gives: it normalises and
/// classifies in memory and increments a bounded in-memory count; the database is written once a minute by another
/// thread. It answers <c>204</c> whatever happened to the term — counted, refused as personal, ignored as a bot,
/// rate-limited, dropped — so the answer tells a caller nothing about what was kept. The request log line carries
/// the route, never the term (which travels in the body).
/// </para>
/// <para>
/// <b>No IP address reaches the database, a log line or <c>ops.AuditLogs</c>, but one is read</b> — the tester pass
/// that found the 5-token "has results" bypass and the unbounded buffer also found that the endpoint had no rate
/// limit at all. <see cref="SearchSubmissionThrottle"/> partitions on the caller's address exactly as
/// <c>NewsletterSignupThrottle</c> and <c>ContactEnquiryThrottle</c> already do: the address lives only in the
/// in-memory limiter's bucket, for as long as its one-minute window, and nowhere else.
/// </para>
/// </summary>
internal static class SearchStatsEndpoints
{
    public const string SubmissionsRoute = "/api/search/submissions";

    public const string PopularRoute = "/api/search/popular";

    /// <summary>A term longer than this is not read at all: the column holds 100 characters.</summary>
    private const int MaxTermCharacters = 400;

    public static IEndpointRouteBuilder MapSearchStatsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(SubmissionsRoute, RecordSubmission)
            .AllowAnonymous()
            .WithTags("Search")
            .WithName("RecordSearchSubmission")
            .WithSummary("Count one submitted search for popular searches (fire and forget).")
            .WithDescription(
                "Body: { term, automated? }. Sent by the storefront's results page once per search it shows. The term is "
                + "normalised (lower-case, trimmed, single-spaced, 100 characters) and not counted at all when it carries "
                + "an at-sign, seven or more digits or an order number, or when the caller is a bot. Rate-limited per "
                + "address (SearchSubmissionThrottle); a refused submission counts nothing. Always 204. No IP address, "
                + "user or session is read or stored.")
            .Produces(StatusCodes.Status204NoContent);

        endpoints.MapGet(PopularRoute, GetPopularAsync)
            .AllowAnonymous()
            .WithTags("Search")
            .WithName("GetPopularSearches")
            .WithSummary("What other shoppers search for most (SRCH-01).")
            .WithDescription(
                "Up to eight terms searched, with results, at least SearchStats:PopularMinimumSearches times in the last "
                + "30 Colombo days, 40 characters at most, none that staff have hidden. Terms only, no counts. An empty "
                + "list is normal for a new shop.")
            .Produces<PopularSearchesDto>();

        var admin = endpoints.MapGroup("/api/admin/content/popular-searches").WithTags("Admin Popular Searches");

        admin.MapGet("/", GetAdminAsync)
            .RequirePermission(Permissions.Content.View)
            .WithName("AdminGetPopularSearches")
            .WithSummary("The popular searches shoppers see now, with their counts, and the terms staff have hidden.")
            .Produces<AdminPopularSearchesDto>()
            .Produces(StatusCodes.Status403Forbidden);

        admin.MapPost("/hide", HideAsync)
            .RequirePermission(Permissions.Content.Edit)
            .WithName("AdminHidePopularSearch")
            .WithSummary("Take a term off the public popular-searches list.")
            .WithDescription("Body: { term }. Normalised as searches are counted. 200 { changed } — false when it was already hidden.")
            .Produces<SearchTermChangedDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        admin.MapPost("/show", ShowAsync)
            .RequirePermission(Permissions.Content.Edit)
            .WithName("AdminShowPopularSearch")
            .WithSummary("Let a hidden term appear on the public popular-searches list again.")
            .WithDescription("Body: { term }. 200 { changed } — false when it was not hidden.")
            .Produces<SearchTermChangedDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static Microsoft.AspNetCore.Http.HttpResults.NoContent RecordSubmission(
        HttpContext httpContext,
        SearchSubmissionBeacon beacon,
        ISearchQueryRecorder recorder,
        SearchSubmissionThrottle throttle,
        IClock clock)
    {
        httpContext.Response.Headers.CacheControl = "no-store";

        // The rate limit (issue #78 tester pass) runs first and costs nothing when it refuses: a flood is turned
        // away before the term is even read, exactly as a bot or a refused term already is.
        if (throttle.TryAcquire(httpContext.Connection.RemoteIpAddress?.ToString())
            && beacon.Term is { Length: > 0 and <= MaxTermCharacters } raw
            && SearchSubmissionClassifier.Accept(raw, httpContext.Request.Headers.UserAgent.ToString(), beacon.Automated ?? false) is { } term)
        {
            recorder.TryRecord(term, clock.UtcNow);
        }

        return TypedResults.NoContent();
    }

    private static async Task<IResult> GetPopularAsync(
        HttpContext httpContext,
        IQueryHandler<GetPopularSearchesQuery, PopularSearchesDto> handler,
        CancellationToken cancellationToken)
    {
        // The same five minutes the API caches it for: one list for every shopper, and it moves slowly.
        httpContext.Response.Headers.CacheControl = "public, max-age=300";

        return TypedResults.Ok(await handler.HandleAsync(new GetPopularSearchesQuery(), cancellationToken));
    }

    private static async Task<IResult> GetAdminAsync(
        HttpContext httpContext,
        IQueryHandler<GetAdminPopularSearchesQuery, AdminPopularSearchesDto> handler,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store, no-cache, private";
        httpContext.Response.Headers.Vary = "Cookie";

        return TypedResults.Ok(await handler.HandleAsync(new GetAdminPopularSearchesQuery(), cancellationToken));
    }

    private static Task<IResult> HideAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<HideSearchTermCommand, Result<bool>> handler,
        CancellationToken cancellationToken) =>
        ChangeAsync(httpContext, antiforgery, (term, actor) => handler.HandleAsync(new HideSearchTermCommand(term, actor), cancellationToken), cancellationToken);

    private static Task<IResult> ShowAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<ShowSearchTermCommand, Result<bool>> handler,
        CancellationToken cancellationToken) =>
        ChangeAsync(httpContext, antiforgery, (term, actor) => handler.HandleAsync(new ShowSearchTermCommand(term, actor), cancellationToken), cancellationToken);

    private static async Task<IResult> ChangeAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        Func<string?, Application.Abstractions.Auditing.AdminActor, Task<Result<bool>>> change,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";

        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forgery)
        {
            return forgery;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<SearchTermRequest>(httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var result = await change(body!.Term, AdminWriteRequest.Actor(httpContext));

        return result.IsSuccess
            ? TypedResults.Ok(new SearchTermChangedDto(result.Value))
            : TypedResults.ValidationProblem(
                result.Errors
                    .GroupBy(error => error.Field, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Select(error => error.Message).ToArray(), StringComparer.Ordinal),
                title: "The term cannot be used.");
    }
}

/// <summary>The search beacon's body.</summary>
/// <param name="Term">The term the results page is showing.</param>
/// <param name="Automated">The browser reported <c>navigator.webdriver</c>.</param>
internal sealed record SearchSubmissionBeacon(string? Term, bool? Automated);

/// <summary>The hide and show requests' body.</summary>
internal sealed record SearchTermRequest(string? Term);

/// <summary>What a hide or show answers: whether anything changed.</summary>
public sealed record SearchTermChangedDto(bool Changed);
