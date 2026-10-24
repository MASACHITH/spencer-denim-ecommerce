using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Application.Settings.Site;

namespace SpencerDenim.Api.Endpoints.Content;

/// <summary>
/// The currencies a shopper may choose to see prices in, with their rates (issue #248, docs/02's decision of
/// 2026-10-24). <b>Display only</b>: nothing the shop charges or stores is computed from these.
/// <para>
/// <b>Anonymous, and its own route like <see cref="SiteSettingsEndpoints"/>, for the same reason.</b> The payload
/// comes from <c>ops.Settings</c>, whose other rows an anonymous caller must never see, so it is served by its own
/// port with a hand-written contract (<see cref="CurrencyRateDto"/>) that has no field a secret, a permission or
/// PayPal's own rate could occupy.
/// </para>
/// <para>
/// <b>It cannot fail in a way that takes a page down.</b> The storefront asks for it while rendering, so a read that
/// throws answers <b>200 with an empty list</b>, which is also the honest answer for a shop that has set no rate: the
/// storefront then offers rupees only. The failure is logged, and, unlike a real answer, is <b>not</b> given a
/// <c>Cache-Control</c>, so a blip is not remembered by a browser or a proxy for a minute.
/// </para>
/// </summary>
internal static class CurrencyRatesEndpoints
{
    /// <summary>Same rule as the catalogue and the content feed: a monitor or a link checker leads with HEAD.</summary>
    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head];

    /// <summary>
    /// <b>Two different sixty-second lifetimes, and only one of them is doc 08's.</b> Doc 08 §3.8 ("Options classes
    /// are cached for 60 s and evicted on save") describes the in-process cache behind this endpoint
    /// (<c>ICurrencyRatesCache</c>), which the save evicts. This header is the endpoint's own choice, made to match
    /// it: a browser or proxy should not hold an answer longer than the server does. It cannot be evicted, so a
    /// saved rate can take up to this long to reach a browser that already has the old list, which is fine for a
    /// figure that moves weekly.
    /// </summary>
    private const string CacheControlValue = "public, max-age=60";

    public static IEndpointRouteBuilder MapCurrencyRatesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/api/currency-rates", ReadMethods, GetAsync)
            .WithTags("Site")
            .WithName("GetCurrencyRates")
            .WithSummary("The currencies prices can be shown in, and what one unit of each costs in rupees.")
            .WithDescription(
                "Display only: USD, GBP, EUR, AUD and AED, in that order, each with its code, symbol and rate "
                + "(rupees per one unit). A currency whose rate staff have not set, or whose stored value is not a "
                + "usable rate, is left out; an empty list is a valid answer and means rupees only. Never carries "
                + "PayPal's rate or buffer. Answers 200 with an empty list if the settings cannot be read, so a "
                + "failure here cannot take a page down.")
            .Produces<IReadOnlyList<CurrencyRateDto>>();

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        HttpContext httpContext,
        IQueryHandler<GetCurrencyRatesQuery, IReadOnlyList<CurrencyRateDto>> handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var rates = await handler.HandleAsync(new GetCurrencyRatesQuery(), cancellationToken);

            httpContext.Response.Headers.CacheControl = CacheControlValue;

            return TypedResults.Ok(rates);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away. Not a fault, and swallowing it into a 200 would both lie and log noise.
            throw;
        }
        catch (Exception exception)
        {
            CurrencyRatesLog.ReadFailed(loggerFactory.CreateLogger(CurrencyRatesLog.Category), exception);

            return TypedResults.Ok(Array.Empty<CurrencyRateDto>());
        }
    }
}

/// <summary>
/// Source-generated log messages for the currency rates endpoint, the way <see cref="SiteSettingsLog"/> does it and
/// for the same reasons (an interpolated line allocates when the level is off, and the analyser enforces it; the
/// category is named because a static class cannot be a generic argument).
/// </summary>
internal static partial class CurrencyRatesLog
{
    /// <summary>The log category for this endpoint.</summary>
    public const string Category = "SpencerDenim.Api.Endpoints.Content.CurrencyRates";

    [LoggerMessage(
        EventId = 1302,
        Level = LogLevel.Warning,
        Message = "The currency rates could not be read; the storefront will offer rupees only.")]
    public static partial void ReadFailed(ILogger logger, Exception exception);
}
