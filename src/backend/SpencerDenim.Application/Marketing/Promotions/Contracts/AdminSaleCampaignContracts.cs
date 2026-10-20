using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Application.Marketing.Promotions.Contracts;

/// <summary>
/// One <c>marketing.SaleCampaignTargets</c> row as the admin screen draws and edits it (ADM-14, doc 11
/// P3 step 2).
/// </summary>
/// <param name="TargetType">Department, Category or Product.</param>
/// <param name="DepartmentId">Set for a Department target, null otherwise.</param>
/// <param name="CategoryId">Set for a Category target, null otherwise.</param>
/// <param name="ProductId">Set for a Product target, null otherwise.</param>
/// <param name="IncludeSubcategories">
/// Doc 08 §3.6, default 1. Only meaningful for a Category target — a department already covers every
/// category under it and a product has no subtree — and the write path forces it to <see langword="true"/>
/// for the other two types rather than storing a flag that means nothing.
/// </param>
/// <param name="Name">
/// What the target is called, for the list: "Women", "Women › Shirts &amp; Blouses", "Relaxed Linen
/// Blend Shirt". Read-only — the screen shows it and sends back the id.
/// </param>
public sealed record AdminSaleCampaignTargetDto(
    SaleCampaignTargetType TargetType,
    int? DepartmentId,
    int? CategoryId,
    int? ProductId,
    bool IncludeSubcategories,
    string Name);

/// <summary>
/// One <c>marketing.SaleCampaigns</c> row as the admin screen draws and edits it (ADM-14, doc 11 P3 A).
/// </summary>
/// <remarks>
/// <b>One shape for the list and the form</b>, the arrangement <see cref="AdminPromoCodeDto"/> and the
/// couriers screen use: the screen is a list with an add/edit panel, so a second narrower DTO would only
/// be a second place for a field to go missing from.
/// </remarks>
/// <param name="StartsAt">
/// <b>Colombo wall-clock, not UTC.</b> The column is <c>StartsAtUtc</c>; this is what
/// <c>CampaignSchedule.ToColombo</c> made of it, because a staff member who typed "19 October 23:59" meant
/// 23:59 in the shop. Sent back the same way on a save.
/// </param>
/// <param name="EndsAt">Also Colombo wall-clock. <b>Required</b>: doc 02 line 119 is "for a date range".</param>
/// <param name="Banner">
/// Doc 11 P3 step 1's optional banner, or <see langword="null"/> when the campaign has none — or when the
/// asset it pointed at is no longer a ready image in the media library, which the storefront would not
/// show either. Named by its <c>publicId</c>, the way every other admin screen names an asset; the internal
/// id never reaches a client (ADM-18).
/// </param>
/// <param name="Status">
/// Doc 11 P3's derived status — Scheduled, Live, Ended or Inactive. Not stored, so the list cannot
/// disagree with what the shop actually charges.
/// </param>
/// <param name="RowVersion">
/// Base64 <c>rowversion</c>, round-tripped as an ETag through <c>If-Match</c> or the body, so two staff
/// members editing one campaign do not silently overwrite each other (doc 08 §1.5).
/// </param>
public sealed record AdminSaleCampaignDto(
    int Id,
    string Name,
    string? Description,
    SaleDiscountType DiscountType,
    decimal DiscountValue,
    DateTime StartsAt,
    DateTime EndsAt,
    bool IsActive,
    AdminSaleCampaignBannerDto? Banner,
    IReadOnlyList<AdminSaleCampaignTargetDto> Targets,
    PromoCodeStatus Status,
    string RowVersion);

/// <summary>
/// A campaign's banner image as the admin screen previews it (ADM-14, doc 11 P3 step 1).
/// </summary>
/// <param name="PublicId">What the form sends back as <c>bannerPublicId</c> to keep it.</param>
/// <param name="Url">A rendition the preview can draw, or the original when the asset has none.</param>
/// <param name="AltText">
/// The description the media library holds for it, which is what the Sale page reads aloud. Null when it
/// has none, and a save then answers a warning saying so.
/// </param>
public sealed record AdminSaleCampaignBannerDto(Guid PublicId, string? Url, string? AltText);

/// <summary>
/// What a save answers: the row, anything the staff member has to be told although it worked, and what
/// the recompute did.
/// </summary>
/// <param name="Warnings">
/// Sentences about a save that <b>succeeded</b>. A silent save that did something other than what the
/// staff member expected is the shape of defect this project has shipped three times, so these are on the
/// success path on purpose.
/// </param>
/// <param name="ProductsRepriced">
/// How many products doc 11 P3 step 3's recompute covered — "on save, recomputes the stored price summary
/// for affected products in batches". Reported because it is the one part of the save a staff member
/// cannot see on the screen, and a campaign that repriced <b>nothing</b> is almost always a targeting
/// mistake rather than a quiet success.
/// </param>
public sealed record AdminSaleCampaignSavedDto(
    AdminSaleCampaignDto Campaign,
    IReadOnlyList<string> Warnings,
    int ProductsRepriced);
