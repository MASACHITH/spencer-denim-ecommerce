namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The JSON bodies the ADM-11 structure endpoints accept.
/// <para>
/// <b>Every field is nullable, including the booleans and the numbers,</b> and that is not laziness. A
/// non-nullable <c>int</c> or <c>bool</c> makes a missing field bind to 0 or false, which reads as "the
/// admin chose zero" and is indistinguishable from a client that forgot to send it; and a value outside
/// <c>byte</c> range on a <c>byte</c> property is a <c>JsonException</c>, which would come back as
/// "malformed JSON" when what is actually wrong is one field an admin typed. Nullable everywhere means the
/// use case decides, and every refusal names its field.
/// </para>
/// </summary>
/// <param name="RowVersion">
/// Base64 of the row's <c>RowVersion</c>, from the admin read model. May be sent in the <c>If-Match</c>
/// header instead; see <c>AdminWriteRequest.RowVersion</c>.
/// </param>
public sealed record UpdateDepartmentRequest(
    string? Name,
    string? Slug,
    string? Description,
    int? HeroImageAssetId,
    string? SeoTitle,
    string? SeoDescription,
    string? RowVersion);

/// <summary>Switch a department on or off (doc 08 §7.1).</summary>
public sealed record SetActiveRequest(bool? IsActive, string? RowVersion);

/// <summary>The departments in the order they should appear, all of them.</summary>
public sealed record ReorderDepartmentsRequest(IReadOnlyList<int>? OrderedDepartmentIds);

/// <summary>Create a category (doc 11 P1 step 2).</summary>
public sealed record CreateCategoryRequest(
    int? DepartmentId,
    int? ParentCategoryId,
    string? Name,
    string? Slug,
    string? Description,
    int? ImageAssetId,
    int? BannerAssetId,
    string? BannerLinkUrl,
    int? AgeGroupId,
    int? SizeGuideId,
    bool? ShowInMegaMenu,
    int? MegaMenuColumn,
    string? SeoTitle,
    string? SeoDescription);

/// <summary>
/// Edit a category. <c>ParentCategoryId</c> is part of the edit: changing it moves the category and
/// re-paths its whole subtree (doc 11 P1 step 4). <c>IsActive</c> is deliberately not here — it has its
/// own route, so taking a section of the shop down is never a side effect of saving a form.
/// </summary>
public sealed record UpdateCategoryRequest(
    int? ParentCategoryId,
    string? Name,
    string? Slug,
    string? Description,
    int? ImageAssetId,
    int? BannerAssetId,
    string? BannerLinkUrl,
    int? AgeGroupId,
    int? SizeGuideId,
    bool? ShowInMegaMenu,
    int? MegaMenuColumn,
    string? SeoTitle,
    string? SeoDescription,
    string? RowVersion);

/// <summary>
/// One parent's children in the order they should appear — <b>all</b> of them (see <c>ReorderList</c> for
/// why a subset is refused rather than applied).
/// </summary>
public sealed record ReorderCategoriesRequest(
    int? DepartmentId,
    int? ParentCategoryId,
    IReadOnlyList<int>? OrderedCategoryIds);
