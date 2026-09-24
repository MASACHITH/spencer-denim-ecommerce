using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Content.Announcements;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Application.Content.Home;
using SpencerDenim.Application.Marketing.Newsletter;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Api.Endpoints.Content;

/// <summary>
/// The public storefront content API (HOME-01..10, GLB-03) and the one thing on the homepage that
/// writes: the newsletter signup (HOME-10).
/// <para>
/// No authentication. Everything a read here returns is what an anonymous visitor may see, and the
/// write records a consent that an anonymous visitor is entitled to give. The admin screens that
/// edit this content are ADM-17, behind permission policies.
/// </para>
/// </summary>
internal static class ContentEndpoints
{
    /// <summary>Same rule as the catalogue: a monitor or a link checker leads with HEAD.</summary>
    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head];

    /// <summary>The rate-limiter policy the signup endpoint runs under (see <c>Program</c>).</summary>
    public const string NewsletterRateLimitPolicy = "newsletter-signup";

    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var content = endpoints.MapGroup("/api/content").WithTags("Content");

        content.MapMethods("/home", ReadMethods, GetHomePageAsync)
            .WithName("GetHomePage")
            .WithSummary("The homepage, section by section.")
            .WithDescription(
                "HOME-01..10. Only the sections that are switched on and inside their schedule are returned, " +
                "in the order the client arranged them, each already carrying its items and — for a product " +
                "row — the head of the listing it links to. A section the storefront should not draw is not " +
                "in the list at all.")
            .Produces<HomePageDto>();

        content.MapMethods("/announcements", ReadMethods, GetAnnouncementsAsync)
            .WithName("GetAnnouncements")
            .WithSummary("The announcement strip above the header.")
            .WithDescription("GLB-03. Active messages inside their schedule, in order. Blank messages are dropped.")
            .Produces<IReadOnlyList<AnnouncementDto>>();

        content.MapPost("/newsletter", SubscribeAsync)
            .WithName("SubscribeToNewsletter")
            .WithSummary("Sign up for the newsletter.")
            .WithDescription(
                "HOME-10, GLB-07. Single opt-in, which is the shape doc 08 §3.6 gives the table. " +
                "The answer does not say whether the address was already on the list: a form that did " +
                "would be a way to test whether someone shops here.")
            .RequireRateLimiting(NewsletterRateLimitPolicy)
            .Produces<NewsletterSignupResponse>()
            .ProducesValidationProblem();

        return endpoints;
    }

    private static async Task<Ok<HomePageDto>> GetHomePageAsync(
        IQueryHandler<GetHomePageQuery, HomePageDto> handler,
        CancellationToken cancellationToken)
    {
        var page = await handler.HandleAsync(new GetHomePageQuery(), cancellationToken);
        return TypedResults.Ok(page);
    }

    private static async Task<Ok<IReadOnlyList<AnnouncementDto>>> GetAnnouncementsAsync(
        IQueryHandler<GetAnnouncementsQuery, IReadOnlyList<AnnouncementDto>> handler,
        CancellationToken cancellationToken)
    {
        var messages = await handler.HandleAsync(new GetAnnouncementsQuery(), cancellationToken);
        return TypedResults.Ok(messages);
    }

    /// <summary>
    /// Records a newsletter consent.
    /// <para>
    /// An address this store will not store is a 400 naming <c>email</c>, the same RFC 9457 shape
    /// the catalogue's bad query strings get. Everything else is one 200 with one sentence, whether
    /// the row was new, already there or an unsubscribe being undone.
    /// </para>
    /// </summary>
    private static async Task<Results<Ok<NewsletterSignupResponse>, ValidationProblem>> SubscribeAsync(
        [FromBody] NewsletterSignupRequest request,
        ICommandHandler<SubscribeToNewsletterCommand, NewsletterSubscriptionOutcome> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var command = new SubscribeToNewsletterCommand(
            request?.Email,
            NewsletterSource.Homepage,
            // The real socket peer. X-Forwarded-For processing is deliberately off (W1-01 round 3),
            // so a caller cannot write someone else's address into the consent record.
            httpContext.Connection.RemoteIpAddress?.ToString());

        var outcome = await handler.HandleAsync(command, cancellationToken);

        if (outcome == NewsletterSubscriptionOutcome.InvalidEmail)
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["email"] = ["Enter an email address, for example name@example.com."],
                },
                title: "That email address could not be read.");
        }

        return TypedResults.Ok(new NewsletterSignupResponse(
            "Thank you. Your email address is on the list."));
    }
}

/// <summary>The newsletter signup form's body (HOME-10).</summary>
/// <param name="Email">The address as typed.</param>
public sealed record NewsletterSignupRequest(string? Email);

/// <summary>
/// What the storefront shows after a signup. One sentence, identical for a new address and one that
/// was already on the list.
/// </summary>
/// <param name="Message">The sentence to show.</param>
public sealed record NewsletterSignupResponse(string Message);
