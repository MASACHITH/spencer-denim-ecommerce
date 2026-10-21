using System.Linq.Expressions;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Infrastructure.Content;

/// <summary>
/// The one definition of "a mega-menu card the storefront may draw" (GLB-05, #215), shared by the departments
/// feed (<c>DepartmentCatalogReader</c>) and the editor's "on the menu now" marker (<c>ContentAdminReaders</c>),
/// so the screen and the shop cannot disagree about which card is showing.
/// <para>
/// Switched on, inside its schedule (<see cref="ContentSchedule"/>'s rule: start inclusive, end exclusive), and
/// with a picture that is ready and public. A soft-deleted asset is already absent through the media query
/// filter, so its navigation is null and the card is not drawn. Of the cards that pass, the storefront draws
/// the first by <c>SortOrder</c> then <c>Id</c>.
/// </para>
/// </summary>
internal static class MegaMenuCardVisibility
{
    public static Expression<Func<MegaMenuPromoCard, bool>> DrawableAt(DateTime utcNow) =>
        card => card.IsActive
            && (card.StartsAtUtc == null || card.StartsAtUtc <= utcNow)
            && (card.EndsAtUtc == null || card.EndsAtUtc > utcNow)
            && card.ImageAsset != null
            && card.ImageAsset.Status == MediaAssetStatus.Ready
            && card.ImageAsset.Visibility == MediaVisibility.Public
            && card.ImageAsset.MediaType == MediaAssetType.Image;
}
