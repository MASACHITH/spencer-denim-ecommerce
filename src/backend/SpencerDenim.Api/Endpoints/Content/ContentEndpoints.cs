using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Announcements;
using SpencerDenim.Application.Content.Contact;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Application.Content.Home;
using SpencerDenim.Application.Content.Pages;
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

        content.MapMethods("/pages", ReadMethods, GetPagesAsync)
            .WithName("GetContentPages")
            .WithSummary("Every published content page, for the footer, the sitemap and the router.")
            .WithDescription(
                "GLB-10. Slug, title and last-modified only — no bodies, because this list is rendered "
                + "into the footer of every page on the site. An unpublished page is absent: a draft "
                + "policy must not be linked, must not be crawled and must not be openable. An empty list "
                + "is a normal answer and the one a fresh shop gives, which is why the payload is an "
                + "object with a `pages` array rather than a bare array — the storefront has to tell "
                + "“nothing is published” from “this could not be read”, and answers 404 for the "
                + "first and 503 for the second.")
            .Produces<ContentPageLinksDto>();

        content.MapMethods("/pages/{slug}", ReadMethods, GetPageAsync)
            .WithName("GetContentPage")
            .WithSummary("One published content page.")
            .WithDescription(
                "GLB-10. The body is already-sanitised HTML (doc 08 §3.7): it was cleaned against the "
                + "allow-list when it was saved, so what is stored is what is served. A draft, a removed "
                + "page and a slug that never existed all answer the same 404 — deliberately "
                + "indistinguishably, because “there is a page here you may not see” is itself "
                + "something a draft policy should not disclose.")
            .Produces<ContentPageDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        content.MapPost("/contact", SubmitEnquiryAsync)
            .WithName("SubmitContactEnquiry")
            .WithSummary("Send the shop an enquiry (GLB-10).")
            .WithDescription(
                "Saved as a New enquiry with the submitter's address (doc 11 P15 step 6) for staff to "
                + "answer by email or telephone. **No email is sent and none is promised**: doc 11 P15's "
                + "notifications are “none in v1” and there is no mail transport in this build. Spam "
                + "protection is doc 11 §7 G-06's rate limit plus a honeypot field; a submission that "
                + "fills the honeypot gets the same answer as one that does not and is not stored.")
            .Produces<ContactEnquiryResponse>()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesValidationProblem();

        content.MapPost("/newsletter", SubscribeAsync)
            .WithName("SubscribeToNewsletter")
            .WithSummary("Sign up for the newsletter.")
            .WithDescription(
                "HOME-10, GLB-07. Single opt-in, which is the shape doc 08 §3.6 gives the table. " +
                "The answer does not say whether the address was already on the list: a form that did " +
                "would be a way to test whether someone shops here.")
            .Produces<NewsletterSignupResponse>()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
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

    private static async Task<Ok<ContentPageLinksDto>> GetPagesAsync(
        IQueryHandler<GetContentPageLinksQuery, ContentPageLinksDto> handler,
        CancellationToken cancellationToken)
    {
        var links = await handler.HandleAsync(new GetContentPageLinksQuery(), cancellationToken);
        return TypedResults.Ok(links);
    }

    /// <summary>
    /// One published page.
    /// <para>
    /// <b>The 404 body never echoes the slug</b>, which it would be natural to do and which would make
    /// this endpoint a reflected-content surface on a public origin: the slug comes straight off the URL.
    /// The storefront does not need it either — it knows what it asked for.
    /// </para>
    /// </summary>
    private static async Task<Results<Ok<ContentPageDto>, ProblemHttpResult>> GetPageAsync(
        string slug,
        IQueryHandler<GetContentPageQuery, ContentPageDto?> handler,
        CancellationToken cancellationToken)
    {
        var page = await handler.HandleAsync(new GetContentPageQuery(slug), cancellationToken);

        return page is null
            ? TypedResults.Problem(
                title: "No such page",
                detail: "This shop has no published page at that address.",
                statusCode: StatusCodes.Status404NotFound)
            : TypedResults.Ok(page);
    }

    /// <summary>
    /// Records an enquiry (GLB-10, doc 11 P15 steps 5–6).
    /// <para>
    /// <b>The confirmation promises nothing that cannot happen.</b> No email is sent — doc 11 P15's
    /// notifications are explicitly none in v1, and <c>ops.EmailOutbox</c> does not exist — so the
    /// sentence says the enquiry has arrived and that the shop will be in touch, which is true of a staff
    /// member reading the inbox. Checkout shipped a promise of an email nothing could send and it was a
    /// Major finding.
    /// </para>
    /// <para>
    /// <b>The honeypot's answer is byte-for-byte the real one.</b> A 400 naming the field, or a different
    /// sentence, or a different status, would each tell a script exactly which field to leave alone.
    /// </para>
    /// </summary>
    private static async Task<Results<Ok<ContactEnquiryResponse>, ProblemHttpResult, ValidationProblem>>
        SubmitEnquiryAsync(
            [FromBody] ContactEnquiryRequest request,
            ICommandHandler<SubmitContactEnquiryCommand, ContactEnquiryResult> handler,
            IContactEnquiryThrottle throttle,
            HttpContext httpContext,
            CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new SubmitContactEnquiryCommand(
                request?.Name,
                request?.Email,
                request?.Phone,
                request?.Subject,
                request?.Message,
                request?.OrderNumber,
                request?.Website,

                // The real socket peer. X-Forwarded-For processing is deliberately off (W1-01 round 3),
                // so a caller cannot write someone else's address into the record.
                httpContext.Connection.RemoteIpAddress?.ToString()),
            cancellationToken);

        if (result.Outcome == ContactEnquiryOutcome.Invalid)
        {
            return ToValidationProblem(result.Errors);
        }

        if (result.Outcome == ContactEnquiryOutcome.RateLimited)
        {
            httpContext.Response.Headers.RetryAfter =
                ((int)throttle.Window.TotalSeconds).ToString(CultureInfo.InvariantCulture);

            return TypedResults.Problem(
                title: "Too many enquiries",
                detail: "Too many enquiries have been sent from here in a short time. Please try again "
                    + "shortly, or message us on WhatsApp.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        // Saved and Discarded answer identically. See the honeypot note above.
        return TypedResults.Ok(new ContactEnquiryResponse(
            "Thank you — your message has reached us. Our team reads every enquiry and will be in touch."));
    }

    /// <summary>
    /// One 400 with a message per field, so the form can mark each control rather than showing a banner.
    /// <para>
    /// <b>No value the caller sent is echoed into it.</b> The messages are this build's own sentences and
    /// the keys are field names; doc 11 P8 records that the message may hold bank account details, and an
    /// error body that quoted the offending value would put them in a response, a browser cache and very
    /// likely a screenshot.
    /// </para>
    /// </summary>
    private static ValidationProblem ToValidationProblem(IReadOnlyList<ValidationError> errors) =>
        TypedResults.ValidationProblem(
            errors
                .GroupBy(error => error.Field, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Message).Distinct(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal),
            title: "One or more fields need attention.");

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
    private static async Task<Results<Ok<NewsletterSignupResponse>, ProblemHttpResult, ValidationProblem>> SubscribeAsync(
        [FromBody] NewsletterSignupRequest request,
        ICommandHandler<SubscribeToNewsletterCommand, NewsletterSubscriptionOutcome> handler,
        INewsletterSignupThrottle throttle,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var command = new SubscribeToNewsletterCommand(
            request?.Email,
            ReadSource(request?.Source),
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

        if (outcome == NewsletterSubscriptionOutcome.RateLimited)
        {
            httpContext.Response.Headers.RetryAfter =
                ((int)throttle.Window.TotalSeconds).ToString(CultureInfo.InvariantCulture);

            return TypedResults.Problem(
                title: "Too many signups",
                detail: "Too many signups have been made from here in a short time. Please try again shortly.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        return TypedResults.Ok(new NewsletterSignupResponse(
            "Thank you. Your email address is on the list."));
    }

    /// <summary>
    /// Which form the address came from, from a fixed list.
    ///
    /// <para>
    /// Never the caller's string: <c>marketing.NewsletterSubscribers.Source</c> is a
    /// <c>varchar(30)</c> the client reads in ADM-21, and a field on a public endpoint that lands
    /// in a report is a field someone will write something else into. Anything unrecognised is the
    /// homepage, which is where the great majority of signups come from.
    /// </para>
    /// </summary>
    private static string ReadSource(string? source) =>
        string.Equals(source?.Trim(), NewsletterSource.Footer, StringComparison.OrdinalIgnoreCase)
            ? NewsletterSource.Footer
            : NewsletterSource.Homepage;
}

/// <summary>
/// The Contact form's body (GLB-10, doc 11 P15 step 5).
/// <para>
/// Every field is nullable, which is the truth about what JSON can send: issue #47 records that a
/// non-nullable reference type binds a missing property to <see langword="null"/> anyway and turns the
/// resulting <c>NullReferenceException</c> into a 500. The use case decides what a missing field means.
/// </para>
/// </summary>
/// <param name="Name">Who is writing.</param>
/// <param name="Email">Where to reply. Nothing is sent to it by the system (doc 11 P15: notifications none).</param>
/// <param name="Phone">Optional.</param>
/// <param name="Subject">Optional.</param>
/// <param name="Message">The enquiry, up to 4,000 characters.</param>
/// <param name="OrderNumber">Optional, so staff can find the order quickly.</param>
/// <param name="Website">
/// <b>The honeypot</b> (doc 11 §7 G-06). Named <c>website</c> rather than <c>honeypot</c> on purpose:
/// the field has to look like one a form might plausibly have, because a script reads the name. A filled
/// value means an automated submission, which is accepted with the same answer and not stored.
/// </param>
public sealed record ContactEnquiryRequest(
    string? Name,
    string? Email,
    string? Phone,
    string? Subject,
    string? Message,
    string? OrderNumber,
    string? Website);

/// <summary>
/// What the storefront shows after an enquiry. One sentence, identical whether the enquiry was stored or
/// silently discarded as spam — see <c>SubmitEnquiryAsync</c>.
/// </summary>
/// <param name="Message">The sentence to show.</param>
public sealed record ContactEnquiryResponse(string Message);

/// <summary>The newsletter signup form's body (HOME-10, GLB-07).</summary>
/// <param name="Email">The address as typed.</param>
/// <param name="Source">
/// Which form it was: <c>homepage</c> or <c>footer</c>. Anything else is read as the homepage.
/// </param>
public sealed record NewsletterSignupRequest(string? Email, string? Source);

/// <summary>
/// What the storefront shows after a signup. One sentence, identical for a new address and one that
/// was already on the list.
/// </summary>
/// <param name="Message">The sentence to show.</param>
public sealed record NewsletterSignupResponse(string Message);
