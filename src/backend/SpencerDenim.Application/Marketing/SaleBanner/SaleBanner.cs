using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;

namespace SpencerDenim.Application.Marketing.SaleBanner;

/// <summary>
/// The live sale campaign's banner as the storefront's Sale page draws it (ADM-14, DEPT-03; doc 11 P3
/// step 1's optional banner).
/// </summary>
/// <param name="ImageUrl">
/// The <c>large</c> rendition, or the original when the asset has none. This is the <c>src</c>.
/// </param>
/// <param name="ImageSrcSet">
/// Every rendition up to <c>zoom</c> with its true width, or null when there is only one file. The client
/// supplies <c>sizes</c>, because only it knows the layout (see <c>MediaUrlBuilder.ToImageSources</c>).
/// </param>
/// <param name="Width">The original's pixel width, for the <c>width</c> attribute, or null when unknown.</param>
/// <param name="Height">The original's pixel height, for the <c>height</c> attribute, or null when unknown.</param>
/// <param name="AltText">
/// The description the media library holds for the picture. <b>Empty, never null, when it has none</b>,
/// so a client binds it straight to <c>alt</c> and the image is announced as decorative rather than by its
/// file name. The admin screen warns the staff member when they choose a banner without one.
/// </param>
public sealed record SaleBannerImageDto(
    string ImageUrl,
    string? ImageSrcSet,
    int? Width,
    int? Height,
    string AltText);

/// <summary>
/// What <c>GET /api/content/sale-banner</c> answers: the banner, or <see langword="null"/> when no live
/// campaign has one.
/// </summary>
/// <remarks>
/// <b>An object with a nullable member rather than a bare value or a 404</b>, for the reason
/// <c>ContentPageLinksDto</c> gives: "there is no banner right now" is the ordinary answer on most days, and
/// the storefront has to be able to tell it apart from "this could not be read". A 404 would also be
/// logged as a failure by every proxy in front of the API for a page that is working.
/// </remarks>
public sealed record SaleBannerDto(SaleBannerImageDto? Banner);

/// <summary>Read port for the Sale page's campaign banner.</summary>
public interface ISaleBannerReader
{
    /// <summary>
    /// The banner of the campaign that is live at <paramref name="utcNow"/>, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <b>Live means <c>CampaignWindow.IsLive</c></b>: active, not removed, and <paramref name="utcNow"/> in
    /// <c>[StartsAtUtc, EndsAtUtc)</c>, the one window convention every scheduled row in this database uses.
    /// The banner must also be a ready public image that has not been deleted.
    /// <para>
    /// <b>When two live campaigns both have a banner, the one that started most recently wins</b>, then the
    /// higher id. Neither doc 08 nor doc 11 says which; the most recent start is the sale a staff member
    /// most likely set up last and expects to see, and the tie-break makes the answer the same on every
    /// read. Recorded as an owner decision in the #191 journal.
    /// </para>
    /// </remarks>
    Task<SaleBannerImageDto?> GetLiveBannerAsync(DateTime utcNow, CancellationToken cancellationToken);
}

/// <summary>Query behind <c>GET /api/content/sale-banner</c>.</summary>
public sealed record GetSaleBannerQuery;

/// <summary>
/// The Sale page's campaign banner, read <b>live</b> on every request.
/// </summary>
/// <remarks>
/// <b>Not cached, and that is the window rule rather than an oversight.</b> The answer is one indexed read
/// of the handful of campaigns whose window contains now, and a cache would round the campaign's own start
/// and end off to its lifetime: a sale set to end at midnight would keep its banner until the entry
/// expired. The prices on the same page come from the stored summary and may lag up to five minutes at a
/// boundary (doc 08 §7.4); the banner does not lag, so for at most those five minutes a banner can sit
/// above prices that have not caught up yet. That is the safer side of the disagreement: the bag charges
/// the campaign price from the first second either way.
/// <para>
/// Nothing in it depends on who is asking, so the server-side render can hand it to the browser without
/// leaking one shopper's state to another.
/// </para>
/// </remarks>
public sealed class GetSaleBannerQueryHandler(ISaleBannerReader reader, IClock clock)
    : IQueryHandler<GetSaleBannerQuery, SaleBannerDto>
{
    public async Task<SaleBannerDto> HandleAsync(GetSaleBannerQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return new SaleBannerDto(await reader.GetLiveBannerAsync(clock.UtcNow.UtcDateTime, cancellationToken));
    }
}
