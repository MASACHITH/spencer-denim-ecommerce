namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// Set or remove the shop's logo (GLB-02, ADM-22).
/// </summary>
/// <param name="LogoAssetPublicId">
/// The media asset's public token, or <see langword="null"/> to remove the logo.
/// <para>
/// <c>Guid?</c> rather than <c>Guid</c>, because <see langword="null"/> is a <i>meaningful</i> value here —
/// it is the Remove action the owner asked for — and because a non-nullable <c>Guid</c> would bind a missing
/// field to <c>Guid.Empty</c>, which is indistinguishable from a client that sent the wrong thing. A value
/// that is not a GUID is a <c>JsonException</c> and comes back as one 400 naming the body, which is
/// <c>AdminWriteRequest.ReadJsonAsync</c>'s job rather than a 500 (issue #47).
/// </para>
/// </param>
/// <param name="RowVersion">
/// The <c>Store</c> group's token from the read model. May travel in <c>If-Match</c> instead; see
/// <c>AdminWriteRequest.RowVersion</c>.
/// </param>
public sealed record SetStoreLogoRequest(Guid? LogoAssetPublicId, string? RowVersion);

/// <summary>
/// The contact number and the social profile addresses (ADM-22, GLB-07, GLB-08).
/// <para>
/// <b>Every field is nullable and every field is sent on every save.</b> PUT replaces, so a field the body
/// omits is a field the API is told to clear — which is exactly how W1-12's blocker worked, where a request
/// model that had never listed three image columns wiped them on every save while 1,571 tests round-tripped
/// null to null. <c>AdminSettingsWriteShapeTests</c> pins this record against the handler's own list of keys
/// so a sixth field cannot be added to one and forgotten on the other.
/// </para>
/// <para>
/// <b>Issue #47 is why these are <c>string?</c> and not <c>string</c>.</b> A non-nullable reference type
/// binds a missing property to <see langword="null"/> regardless, and the resulting
/// <c>NullReferenceException</c> deeper in is the 500 on the admin login that #47 records. Nullable is the
/// truth about what JSON can send, and the use case decides what a missing field means.
/// </para>
/// </summary>
/// <param name="WhatsApp">Digits only, international form. Blank or absent clears it.</param>
/// <param name="FacebookUrl">A full http(s) address. Blank or absent hides the icon.</param>
/// <param name="InstagramUrl"><inheritdoc cref="FacebookUrl" path="/node()"/></param>
/// <param name="TikTokUrl"><inheritdoc cref="FacebookUrl" path="/node()"/></param>
/// <param name="RowVersion">The group's token from the read model.</param>
public sealed record UpdateContactAndSocialRequest(
    string? WhatsApp,
    string? FacebookUrl,
    string? InstagramUrl,
    string? TikTokUrl,
    string? RowVersion);
