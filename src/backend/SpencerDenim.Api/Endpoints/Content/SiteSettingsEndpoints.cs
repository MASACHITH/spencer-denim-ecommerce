using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Application.Settings.Site;

namespace SpencerDenim.Api.Endpoints.Content;

/// <summary>
/// The storefront shell's own settings: the logo, the WhatsApp number and the social profile addresses
/// (GLB-02, GLB-07, GLB-08).
/// <para>
/// <b>Anonymous, and a separate route group from <c>/api/content</c> on purpose.</b> This payload comes from
/// <c>ops.Settings</c> rather than from the <c>content</c> schema, and every one of doc 08 §3.8's other
/// sixteen groups is something an anonymous caller must never see. Putting it on its own route, served by its
/// own port, with its own hand-written contract, means the question "could a secret reach this endpoint" has
/// a structural answer — <see cref="SiteSettingsDto"/> has no field one could occupy — rather than a
/// filtering answer.
/// </para>
/// <para>
/// <b>It cannot fail in a way that takes a page down (W2-10's lesson).</b> The storefront renders this on the
/// server on every page; the announcement bar and the navigation tree both fall back rather than fail, and so
/// does this. A read that throws answers <b>200 with the empty payload</b>, which is also the honest answer
/// for a shop that has configured none of it — and that equivalence is what makes the fallback safe: there is
/// no state the storefront has to render differently for "no logo" and "could not find out".
/// </para>
/// </summary>
internal static class SiteSettingsEndpoints
{
    /// <summary>Same rule as the catalogue and the content feed: a monitor or a link checker leads with HEAD.</summary>
    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head];

    public static IEndpointRouteBuilder MapSiteSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var site = endpoints.MapGroup("/api/site").WithTags("Site");

        site.MapMethods("/settings", ReadMethods, GetAsync)
            .WithName("GetSiteSettings")
            .WithSummary("The shop's logo, WhatsApp number and social links.")
            .WithDescription(
                "GLB-02, GLB-07, GLB-08. A fixed allow-list of three things an anonymous visitor may see — "
                + "never a dump of ops.Settings, so no secret and no permission-guarded key can appear here. "
                + "A null logo means 'use the text wordmark', and a social address the client has not supplied "
                + "is absent from the list rather than present with a placeholder. Answers 200 with everything "
                + "empty if the settings cannot be read, so a failure here cannot take a page down.")
            .Produces<SiteSettingsDto>();

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        IQueryHandler<GetSiteSettingsQuery, SiteSettingsDto> handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(await handler.HandleAsync(new GetSiteSettingsQuery(), cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away. Not a fault, and swallowing it into a 200 would both lie and log noise.
            throw;
        }
        catch (Exception exception)
        {
            // Logged at warning rather than error, and deliberately not rethrown: the shell degrading to its
            // wordmark and no social icons is a cosmetic outcome, not an outage, and a 500 here would make it
            // one. The log line is how it stops being invisible — W2-10 shipped two shell fallbacks that were
            // silent, and "the logo stopped showing three weeks ago" is not a thing anybody notices in time.
            SiteSettingsLog.ReadFailed(
                loggerFactory.CreateLogger(SiteSettingsLog.Category),
                exception);

            return TypedResults.Ok(SiteSettingsDto.Empty);
        }
    }
}

/// <summary>
/// Source-generated log messages for the site settings endpoint, the way the rest of the solution logs
/// (<c>MediaLog</c>, <c>CatalogSeedRunner</c>): an interpolated log line allocates and formats even when the
/// level is switched off, and the analyser enforces it.
/// <para>
/// The category is named rather than taken from <c>ILogger&lt;T&gt;</c> because the endpoint class is static
/// and a static type cannot be a generic argument — so the factory is asked for the category by name, which
/// also keeps the string stable if this class is ever moved.
/// </para>
/// </summary>
internal static partial class SiteSettingsLog
{
    /// <summary>The log category for this endpoint.</summary>
    public const string Category = "SpencerDenim.Api.Endpoints.Content.SiteSettings";

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Warning,
        Message = "The site settings could not be read; the storefront shell will use its fallbacks.")]
    public static partial void ReadFailed(ILogger logger, Exception exception);
}
