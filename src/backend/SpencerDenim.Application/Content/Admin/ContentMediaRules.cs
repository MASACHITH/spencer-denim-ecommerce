using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Application.Content.Admin;

/// <summary>
/// The one decision every ADM-17 media slot makes: may this row point at this asset.
/// <para>
/// The screens upload through the media library first (<c>POST /api/admin/media/images</c> and
/// <c>/videos</c>, which do the signature check, the EXIF strip and the renditions) and send the
/// asset's public id here — the arrangement the settings logo uses. So the questions left are the ones
/// the storefront would otherwise meet as a broken picture: does it exist, is it ready, is it public, is it
/// the right kind of file, and — doc 08 §3.3 and GLB-12 — does an image carry alt text.
/// </para>
/// </summary>
public static class ContentMediaRules
{
    /// <summary>
    /// The asset's row id, or <see langword="null"/> when <paramref name="publicId"/> is null (the slot is
    /// being cleared) or when the asset is refused — in which case an error names <paramref name="field"/>.
    /// </summary>
    public static async Task<int?> ResolveAsync(
        ICollection<ValidationError> errors,
        IContentLookups lookups,
        string field,
        Guid? publicId,
        MediaAssetType expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(lookups);

        if (publicId is not { } id)
        {
            return null;
        }

        var asset = await lookups.FindMediaAsync(id, cancellationToken);

        if (asset is null)
        {
            errors.Add(new ValidationError(field, "That file is not in the media library. Upload it again."));
            return null;
        }

        if (asset.MediaType != expected)
        {
            errors.Add(new ValidationError(
                field,
                expected == MediaAssetType.Video ? "Choose a video here, not an image." : "Choose an image here, not a video."));
            return null;
        }

        if (asset.Status != MediaAssetStatus.Ready || asset.Visibility != MediaVisibility.Public)
        {
            errors.Add(new ValidationError(field, "That file is not ready to be shown on the shop yet."));
            return null;
        }

        if (expected == MediaAssetType.Image && string.IsNullOrWhiteSpace(asset.AltText))
        {
            errors.Add(new ValidationError(
                field,
                "That image has no alt text. Images on the shop need a short description for people using a "
                + "screen reader."));
            return null;
        }

        return asset.Id;
    }
}
