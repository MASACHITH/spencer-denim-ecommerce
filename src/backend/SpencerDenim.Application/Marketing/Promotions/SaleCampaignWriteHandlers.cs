using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Application.Marketing.Promotions.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Application.Marketing.Promotions;

/// <summary>One target as the form sends it (ADM-14, doc 11 P3 step 2). Every field nullable: issue #47.</summary>
public sealed record SaleCampaignTargetInput(
    SaleCampaignTargetType? TargetType,
    int? DepartmentId,
    int? CategoryId,
    int? ProductId,
    bool? IncludeSubcategories);

/// <summary>
/// The fields a staff member sets on a sale campaign (ADM-14, doc 11 P3 steps 1 and 2).
/// </summary>
/// <remarks>
/// <b>Every field is nullable and every field is sent on every save</b>, for the two reasons
/// <see cref="PromoCodeInput"/> gives: a JSON body that omits a property binds null into a non-nullable
/// one (issue #47), and "left as it is" and "cleared" are different instructions whose collapse wiped
/// every category image in W1-12. Here the stakes are the shop's prices: a save that silently dropped
/// <c>targets</c> would turn a campaign on one category into a campaign on nothing, or — worse, if the
/// previous targets were kept — leave a staff member believing they had narrowed a sale they had not.
/// </remarks>
public sealed record SaleCampaignInput(
    string? Name,
    string? Description,
    SaleDiscountType? DiscountType,
    decimal? DiscountValue,
    DateTime? StartsAt,
    DateTime? EndsAt,
    bool? IsActive,
    Guid? BannerPublicId,
    IReadOnlyList<SaleCampaignTargetInput>? Targets);

/// <summary>Create a sale campaign (ADM-14, <c>Promotions.Create</c>).</summary>
public sealed record CreateSaleCampaignCommand(SaleCampaignInput Fields, AdminActor Actor);

/// <summary>Edit a sale campaign (ADM-14, <c>Promotions.Edit</c>).</summary>
public sealed record UpdateSaleCampaignCommand(
    int CampaignId,
    SaleCampaignInput Fields,
    string? RowVersion,
    AdminActor Actor);

/// <summary>Remove a sale campaign — a soft delete (ADM-14, <c>Promotions.Delete</c>).</summary>
public sealed record DeleteSaleCampaignCommand(int CampaignId, string? RowVersion, AdminActor Actor);

/// <summary>What an ADM-14 write decided. The same four answers <see cref="PromoWriteResult"/> has.</summary>
public abstract record SaleCampaignWriteResult
{
    private SaleCampaignWriteResult()
    {
    }

    /// <summary>The write happened.</summary>
    public sealed record Succeeded(AdminSaleCampaignSavedDto Saved) : SaleCampaignWriteResult;

    /// <summary>Rejected input, one entry per field, so the screen attaches each message to its control.</summary>
    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : SaleCampaignWriteResult
    {
        public Invalid(string field, string message)
            : this(new List<ValidationError> { new(field, message) }.AsReadOnly())
        {
        }
    }

    /// <summary>No such campaign, or it has been removed.</summary>
    public sealed record NotFound(string Message) : SaleCampaignWriteResult;

    /// <summary>Well formed and still impossible: a stale row version.</summary>
    public sealed record Conflict(string Code, string Message) : SaleCampaignWriteResult;
}

/// <summary>
/// Create, edit and remove a sale campaign (ADM-14; doc 11 P3 section A, doc 08 §3.6).
/// <para>
/// <b>Three writes in one class, because they share every rule</b> — the argument
/// <c>PromoCodeWriteHandlers</c> and <c>ContentPageWriteHandlers</c> make for the same arrangement.
/// </para>
/// <para>
/// <b>Validation is here and not left to the check constraints.</b>
/// <c>CK_SaleCampaigns_DiscountValue</c>, <c>CK_SaleCampaigns_Window</c> and
/// <c>CK_SaleCampaignTargets_Target</c> exist for the support script, the data fix and the restore — the
/// paths that do not come through a use case — and every rule below is also one of them. Leaving a rule to
/// the constraint alone turns a typo into a 500 instead of a named field error beside the control.
/// </para>
/// <para>
/// <b>Every write recomputes the stored price summary, and that is doc 11 P3 step 3.</b> The affected set
/// is the <b>union of the products the campaign targeted before and after</b>: a product dropped from a
/// campaign needs its summary put back just as much as one added to it needs it taken down, and after the
/// save there is no way to find it. The recompute runs <i>after</i> the commit, because it is a set-based
/// statement that must read committed rows — and because a failure in it must not undo a save the staff
/// member was told succeeded. If it does fail, the <c>RefreshCampaignPricing</c> job picks the boundary up
/// within five minutes and the bag and checkout were never affected: they price live (doc 08 §7.4).
/// </para>
/// <para>
/// <b>Every write also evicts the homepage cache</b> (doc 08 §3.7: "homepage output cached 5 min and
/// evicted on save"). The homepage's product rows and its Sale section (HOME-07) print prices from the
/// stored summary this class has just recomputed, so without the eviction the busiest page on the site
/// could show the pre-campaign prices for up to five minutes after a sale was switched on, while the bag
/// already charged the campaign price. Issue #192 named the missing call. It runs after the recompute, and
/// in a <c>finally</c>, so a render that happened between the commit and the recompute cannot be what the
/// cache keeps.
/// </para>
/// </summary>
public sealed class SaleCampaignWriteHandlers(
    ISaleCampaignAdminStore store,
    ISaleCampaignAdminReader reader,
    ICampaignPricingRefresher refresher,
    IAuditLogWriter auditLog,
    IClock clock,
    IHomePageCache homePage)
    : ICommandHandler<CreateSaleCampaignCommand, SaleCampaignWriteResult>,
        ICommandHandler<UpdateSaleCampaignCommand, SaleCampaignWriteResult>,
        ICommandHandler<DeleteSaleCampaignCommand, SaleCampaignWriteResult>
{
    /// <summary><c>ops.AuditLogs.EntityType</c> for a sale campaign (doc 08 §3.3's own spelling).</summary>
    public const string EntityType = "SaleCampaign";

    /// <summary>
    /// The most targets one campaign may carry.
    /// </summary>
    /// <remarks>
    /// <b>Not a rule from doc 08 or doc 11 — a decision this task made, and it is in the task report as
    /// one.</b> Neither document bounds the collection. The screen is a picker over departments,
    /// categories and products, and a department target already covers a whole department in one row, so
    /// two hundred is far more than any real campaign needs; what the bound actually stops is a request
    /// that makes the validator do two hundred thousand existence checks and the recompute sweep the whole
    /// catalogue, from an authenticated staff account that has typed something wrong.
    /// </remarks>
    public const int MaxTargets = 200;

    public async Task<SaleCampaignWriteResult> HandleAsync(
        CreateSaleCampaignCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var (fields, errors) = Validate(command.Fields);

        if (errors.Count == 0)
        {
            (fields, errors) = await CheckReferencesAsync(fields, cancellationToken);
        }

        if (errors.Count > 0)
        {
            return new SaleCampaignWriteResult.Invalid(errors);
        }

        var campaign = new SaleCampaign();
        Apply(campaign, fields);
        store.Add(campaign);

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.Create,
            EntityType,

            // The name and not the id: the row has no id until the save.
            fields.Name,
            null,
            Snapshot(campaign),
            command.Actor,
            clock.UtcNow.UtcDateTime));

        // A new campaign targeted nothing before, so the affected set is what it targets now — which is
        // only knowable after the save, when the target rows exist. Resolved below.
        return await CommitAsync(campaign, fields, [], command.Actor.UserId, cancellationToken);
    }

    public async Task<SaleCampaignWriteResult> HandleAsync(
        UpdateSaleCampaignCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var campaign = await store.FindAsync(command.CampaignId, cancellationToken);

        if (campaign is null)
        {
            return NotFound(command.CampaignId);
        }

        if (RowVersionMismatch(campaign, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var (fields, errors) = Validate(command.Fields);

        if (errors.Count == 0)
        {
            (fields, errors) = await CheckReferencesAsync(fields, cancellationToken);
        }

        if (errors.Count > 0)
        {
            return new SaleCampaignWriteResult.Invalid(errors);
        }

        // **Taken before the targets are replaced.** This is the half of doc 11 P3 step 3's "affected
        // products" that is invisible afterwards: a category removed from the campaign has to have its
        // products' summaries put back, and once the target row is gone nothing can say which they were.
        var before = await refresher.ProductsTargetedByAsync([campaign.Id], cancellationToken);
        var snapshot = Snapshot(campaign);

        Apply(campaign, fields);

        var audit = ContentAuditTrail.ForChange(
            AuditActions.Update,
            EntityType,
            campaign.Id.ToString(CultureInfo.InvariantCulture),
            snapshot,
            Snapshot(campaign),
            command.Actor,
            clock.UtcNow.UtcDateTime);

        if (audit is not null)
        {
            auditLog.Enlist(audit);
        }

        return await CommitAsync(campaign, fields, before, command.Actor.UserId, cancellationToken);
    }

    public async Task<SaleCampaignWriteResult> HandleAsync(
        DeleteSaleCampaignCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var campaign = await store.FindAsync(command.CampaignId, cancellationToken);

        if (campaign is null)
        {
            return NotFound(command.CampaignId);
        }

        if (RowVersionMismatch(campaign, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var now = clock.UtcNow.UtcDateTime;
        var snapshot = Snapshot(campaign);

        // Taken before the soft delete, for the same reason the edit does it: afterwards the campaign is
        // behind the global query filter and `ProductsTargetedByAsync` would have to bypass it.
        var affected = await refresher.ProductsTargetedByAsync([campaign.Id], cancellationToken);

        // Doc 11 P3 step 5's third way to end a campaign. A soft delete, so the orders it priced keep a
        // resolvable `OrderLines.SaleCampaignId` — which is why the foreign key is NO ACTION and nothing
        // here is ever hard-deleted. The targets stay, and the cascade only runs on a purge.
        campaign.SoftDelete(now, command.Actor.UserId);

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.Delete,
            EntityType,
            campaign.Id.ToString(CultureInfo.InvariantCulture),
            snapshot,
            null,
            command.Actor,
            now));

        var outcome = await store.SaveAsync(campaign, command.Actor.UserId, cancellationToken);

        if (outcome == SaleCampaignSaveOutcome.ConcurrencyConflict)
        {
            return StaleRowVersion();
        }

        // The sale is over for the listing as well as for the bag, which is the whole of doc 11 P3 step 3
        // on a delete: without this the Sale page would go on advertising a campaign that no longer
        // exists until the next job pass.
        var pass = await RefreshAndEvictAsync(affected, cancellationToken);

        return new SaleCampaignWriteResult.Succeeded(
            new AdminSaleCampaignSavedDto(ToDto(campaign, now), [], pass.Products));
    }

    private async Task<SaleCampaignWriteResult> CommitAsync(
        SaleCampaign campaign,
        ValidFields fields,
        IReadOnlyList<int> previouslyTargeted,
        int? actingUserId,
        CancellationToken cancellationToken)
    {
        var outcome = await store.SaveAsync(campaign, actingUserId, cancellationToken);

        if (outcome == SaleCampaignSaveOutcome.ConcurrencyConflict)
        {
            return StaleRowVersion();
        }

        var now = clock.UtcNow.UtcDateTime;

        // Re-read, so the screen is shown what the database holds rather than what we meant to write. The
        // row version in particular has to be the one the save produced, or the next edit from this form
        // would be refused as stale; and the target ids are only resolvable to names from the catalogue.
        var saved = await reader.FindAsync(campaign.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Sale campaign {campaign.Id} was saved but could not be read back.");

        // Doc 11 P3 step 3, after the commit: the union of before and after, so a product that has just
        // stopped being targeted has its summary put back in the same pass.
        var nowTargeted = await refresher.ProductsTargetedByAsync([campaign.Id], cancellationToken);
        var affected = previouslyTargeted.Concat(nowTargeted).Distinct().ToList();

        var pass = await RefreshAndEvictAsync(affected, cancellationToken);

        return new SaleCampaignWriteResult.Succeeded(
            new AdminSaleCampaignSavedDto(saved, Warnings(fields, affected.Count, now), pass.Products));
    }

    /// <summary>
    /// Doc 11 P3 step 3's recompute, then the homepage eviction doc 08 §3.7 asks of every save.
    /// </summary>
    /// <remarks>
    /// <b>The eviction is in a <c>finally</c></b>: if the recompute fails, the campaign is still saved and
    /// the bag already prices it live, so the homepage should at least stop serving a page assembled before
    /// the save. It comes <i>after</i> the recompute, not after the commit, so a visitor who rendered the
    /// homepage in between cannot leave the pre-recompute prices cached for five minutes.
    /// </remarks>
    private async Task<CampaignPricingRefreshPass> RefreshAndEvictAsync(
        IReadOnlyCollection<int> affected,
        CancellationToken cancellationToken)
    {
        try
        {
            return await refresher.RefreshProductsAsync(affected, cancellationToken);
        }
        finally
        {
            homePage.Evict();
        }
    }

    /// <summary>
    /// Sentences about a save that worked. Doc 08 asks for none of these; they are here because each one
    /// is a mistake this form would otherwise make silently.
    /// </summary>
    private static List<string> Warnings(ValidFields fields, int affectedProducts, DateTime utcNow)
    {
        var warnings = new List<string>();

        var status = CampaignWindow.StatusOf(fields.IsActive, fields.StartsAtUtc, fields.EndsAtUtc, utcNow);

        if (status == PromoCodeStatus.Ended)
        {
            warnings.Add("This campaign's end date has already passed, so it is not discounting anything.");
        }

        if (affectedProducts == 0)
        {
            // The one that matters most. A campaign whose targets reach no product looks identical on the
            // screen to one that is working, and the shop would simply never go on sale.
            warnings.Add(
                "This campaign's targets do not reach any product, so no price has changed. Check the "
                + "departments, categories and products it applies to.");
        }

        if (fields.BannerAssetId is not null && string.IsNullOrWhiteSpace(fields.BannerAltText))
        {
            // A warning and not a refusal. The description lives on the media-library asset, and a file
            // uploaded once before without one is answered from the checksum index with its old, empty
            // description, so a refusal here could leave a staff member with no way to choose that
            // picture at all. The Sale page draws it as decorative (alt="") instead.
            warnings.Add(
                "The banner image has no description, so people using a screen reader will not hear "
                + "what it says. Upload it with a description if it carries words or an offer.");
        }

        if (fields.DiscountType == SaleDiscountType.Percent
            && fields.DiscountValue == SaleCampaign.MaxPercentDiscount)
        {
            warnings.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{SaleCampaign.MaxPercentDiscount:0}% is the largest discount a campaign may take, and it leaves a tenth of each price."));
        }

        return warnings;
    }

    private static SaleCampaignWriteResult.NotFound NotFound(int campaignId) =>
        new(string.Create(CultureInfo.InvariantCulture, $"No sale campaign has the id {campaignId}."));

    private static SaleCampaignWriteResult.Conflict StaleRowVersion() =>
        new(
            PromoConflictCodes.StaleRowVersion,
            "Somebody else changed this campaign while you were editing it. Reload and re-apply your "
            + "changes.");

    /// <summary>
    /// The optimistic-concurrency check (doc 08 §1.5). A caller that sends no row version is refused
    /// rather than allowed to write blind.
    /// </summary>
    private static SaleCampaignWriteResult? RowVersionMismatch(SaleCampaign campaign, string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion))
        {
            return new SaleCampaignWriteResult.Invalid(
                "rowVersion",
                "Send the campaign's row version so a concurrent edit is not overwritten.");
        }

        var current = campaign.RowVersion is { } bytes ? Convert.ToBase64String(bytes) : null;

        // An ETag may arrive quoted, and `If-Match: "AAAA..."` is the correct spelling of one. Compared
        // ordinally: base64 is case-sensitive data.
        var supplied = rowVersion.Trim().Trim('"');

        return string.Equals(current, supplied, StringComparison.Ordinal) ? null : StaleRowVersion();
    }

    /// <summary>
    /// Every rule doc 11 P3 step 1 and 2 and doc 08 §3.6 state, as named field errors.
    /// </summary>
    /// <remarks>
    /// <b>All of them at once, never the first.</b> A staff member filling in a form with three mistakes
    /// in it should be told three times rather than three times over.
    /// </remarks>
    private static (ValidFields Fields, List<ValidationError> Errors) Validate(SaleCampaignInput input)
    {
        var errors = new List<ValidationError>();

        var name = string.IsNullOrWhiteSpace(input.Name) ? null : input.Name.Trim();

        if (name is null)
        {
            errors.Add(new ValidationError("name", "Give the campaign a name."));
        }
        else if (name.Length > SaleCampaign.MaxNameLength)
        {
            errors.Add(new ValidationError(
                "name",
                string.Create(CultureInfo.InvariantCulture, $"Keep the name to {SaleCampaign.MaxNameLength} characters or fewer.")));
        }

        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();

        if (description is { } text && text.Length > SaleCampaign.MaxDescriptionLength)
        {
            errors.Add(new ValidationError(
                "description",
                string.Create(CultureInfo.InvariantCulture, $"Keep the description to {SaleCampaign.MaxDescriptionLength} characters or fewer.")));
        }

        if (input.DiscountType is not { } discountType
            || discountType is not (SaleDiscountType.Percent or SaleDiscountType.FixedAmount))
        {
            errors.Add(new ValidationError(
                "discountType",
                "Choose a percentage off or a fixed amount off."));
            discountType = SaleDiscountType.Percent;
        }

        var discountValue = ValidateDiscountValue(input, discountType, errors);

        if (input.StartsAt is null)
        {
            errors.Add(new ValidationError("startsAt", "Choose when the campaign starts."));
        }

        if (input.EndsAt is null)
        {
            // Required, unlike a promo code's end: doc 02 line 119 is "for a date range" and doc 08 §3.6
            // makes the column NOT NULL. A sale with no end is a price change, not a campaign.
            errors.Add(new ValidationError("endsAt", "Choose when the campaign ends."));
        }
        else if (input.StartsAt is { } startsAt && input.EndsAt.Value <= startsAt)
        {
            // The same strictness as CK_SaleCampaigns_Window, so a row the form accepts is a row the
            // database accepts.
            errors.Add(new ValidationError("endsAt", "The end must be after the start."));
        }

        if (input.BannerPublicId is { } banner && banner == Guid.Empty)
        {
            errors.Add(new ValidationError("bannerPublicId", "Choose a banner image, or leave it empty."));
        }

        var targets = ValidateTargets(input.Targets, errors);

        var fields = new ValidFields(
            name ?? string.Empty,
            description,
            discountType,
            discountValue,
            CampaignWindow.ToUtc(input.StartsAt ?? default),
            CampaignWindow.ToUtc(input.EndsAt ?? default),

            // Null is "off": a form that did not send the flag has not asked for a live campaign, and the
            // safe reading of a half-filled request is the one that does not put a discount on the shop.
            input.IsActive ?? false,
            input.BannerPublicId,

            // Resolved by CheckReferencesAsync, which is the only place that can ask the media library.
            null,
            null,
            targets);

        return (fields, errors);
    }

    private static decimal ValidateDiscountValue(
        SaleCampaignInput input,
        SaleDiscountType discountType,
        List<ValidationError> errors)
    {
        if (discountType == SaleDiscountType.Percent)
        {
            // Doc 08 §3.6 "CK Percent ≤ 90" and doc 11 P3 step 1 "percent up to 90%". The strict lower
            // bound is not in doc 08 and is here for the reason the check constraint gives: a campaign of
            // 0% is a sale the shop advertises that discounts nothing.
            if (input.DiscountValue is not { } percent
                || percent <= 0m
                || percent > SaleCampaign.MaxPercentDiscount)
            {
                errors.Add(new ValidationError(
                    "discountValue",
                    string.Create(CultureInfo.InvariantCulture, $"Enter a percentage above 0 and up to {SaleCampaign.MaxPercentDiscount:0}.")));
                return 0m;
            }

            return percent;
        }

        if (input.DiscountValue is not { } amount || amount <= 0m)
        {
            errors.Add(new ValidationError("discountValue", "Enter an amount above zero."));
            return 0m;
        }

        return amount;
    }

    /// <summary>
    /// Doc 08 §3.6's <c>CK_SaleCampaignTargets_Target</c> as field errors, plus the two rules a constraint
    /// cannot express: at least one target, and no target twice.
    /// </summary>
    /// <remarks>
    /// <b>A duplicate target is refused rather than silently de-duplicated.</b> It changes no price —
    /// "lowest resulting price wins" makes two identical targets idempotent — but a staff member who has
    /// added Women twice has made a mistake, and a form that quietly tidied it would also quietly tidy the
    /// case where they meant two different categories and picked the same one twice.
    /// </remarks>
    private static List<ValidTarget> ValidateTargets(
        IReadOnlyList<SaleCampaignTargetInput>? inputs,
        List<ValidationError> errors)
    {
        var targets = new List<ValidTarget>();

        if (inputs is null || inputs.Count == 0)
        {
            errors.Add(new ValidationError(
                "targets",
                "Choose at least one department, category or product for the campaign to apply to."));
            return targets;
        }

        if (inputs.Count > MaxTargets)
        {
            errors.Add(new ValidationError(
                "targets",
                string.Create(CultureInfo.InvariantCulture, $"A campaign may have at most {MaxTargets} targets. Use a department or a category instead of listing products.")));
            return targets;
        }

        var seen = new HashSet<(SaleCampaignTargetType Type, int Id)>();

        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var field = string.Create(CultureInfo.InvariantCulture, $"targets[{index}]");

            if (input.TargetType is not { } type
                || type is not (SaleCampaignTargetType.Department
                    or SaleCampaignTargetType.Category
                    or SaleCampaignTargetType.Product))
            {
                errors.Add(new ValidationError(field, "Choose a department, a category or a product."));
                continue;
            }

            // Exactly the matching column, which is the constraint's own wording. Checked as a pair
            // rather than as "exactly one is not null", because a row saying Category with a ProductId on
            // it would pass the looser test and mean two different things.
            var id = type switch
            {
                SaleCampaignTargetType.Department => input.DepartmentId,
                SaleCampaignTargetType.Category => input.CategoryId,
                _ => input.ProductId,
            };

            var others = type switch
            {
                SaleCampaignTargetType.Department => (input.CategoryId, input.ProductId),
                SaleCampaignTargetType.Category => (input.DepartmentId, input.ProductId),
                _ => (input.DepartmentId, input.CategoryId),
            };

            if (id is not { } targetId || targetId <= 0)
            {
                errors.Add(new ValidationError(field, "Choose what the campaign applies to."));
                continue;
            }

            if (others.Item1 is not null || others.Item2 is not null)
            {
                errors.Add(new ValidationError(
                    field,
                    "A target names one department, one category or one product, not more than one."));
                continue;
            }

            if (!seen.Add((type, targetId)))
            {
                errors.Add(new ValidationError(field, "This target is already on the campaign."));
                continue;
            }

            targets.Add(new ValidTarget(
                type,
                targetId,

                // Doc 08 §3.6's DF 1, and forced for the two types it cannot mean anything for: a
                // department already covers its categories and a product has no subtree, so storing
                // `false` there would be a column whose value could never be read.
                type != SaleCampaignTargetType.Category || (input.IncludeSubcategories ?? true)));
        }

        return targets;
    }

    /// <summary>
    /// That every id the targets and the banner name actually exists, as a field error rather than a
    /// foreign-key violation.
    /// </summary>
    /// <remarks>
    /// Also resolves the banner's <c>publicId</c> to the internal id the column stores, and fetches its
    /// description for the warning about a banner without one.
    /// <para>
    /// Run only once the shape is valid, so a form with a missing name does not also pay for three
    /// round-trips — and so the errors a staff member sees are the ones they can act on first.
    /// </para>
    /// </remarks>
    private async Task<(ValidFields Fields, List<ValidationError> Errors)> CheckReferencesAsync(
        ValidFields fields,
        CancellationToken cancellationToken)
    {
        var errors = new List<ValidationError>();

        if (fields.BannerPublicId is { } banner)
        {
            if (await store.FindBannerAsync(banner, cancellationToken) is { } candidate)
            {
                fields = fields with
                {
                    BannerAssetId = candidate.MediaAssetId,
                    BannerAltText = candidate.AltText,
                };
            }
            else
            {
                errors.Add(new ValidationError(
                    "bannerPublicId",
                    "That banner is not a ready public image in the media library. Upload a picture, or "
                    + "leave the banner empty."));
            }
        }

        var existence = await store.WhichTargetsExistAsync(
            Ids(fields.Targets, SaleCampaignTargetType.Department),
            Ids(fields.Targets, SaleCampaignTargetType.Category),
            Ids(fields.Targets, SaleCampaignTargetType.Product),
            cancellationToken);

        for (var index = 0; index < fields.Targets.Count; index++)
        {
            var target = fields.Targets[index];

            var exists = target.TargetType switch
            {
                SaleCampaignTargetType.Department => existence.DepartmentIds.Contains(target.TargetId),
                SaleCampaignTargetType.Category => existence.CategoryIds.Contains(target.TargetId),
                _ => existence.ProductIds.Contains(target.TargetId),
            };

            if (!exists)
            {
                errors.Add(new ValidationError(
                    string.Create(CultureInfo.InvariantCulture, $"targets[{index}]"),
                    "That no longer exists in the catalogue. Remove it from the campaign."));
            }
        }

        return (fields, errors);
    }

    private static List<int> Ids(IReadOnlyList<ValidTarget> targets, SaleCampaignTargetType type) =>
        targets.Where(target => target.TargetType == type).Select(target => target.TargetId).ToList();

    private static void Apply(SaleCampaign campaign, ValidFields fields)
    {
        campaign.Name = fields.Name;
        campaign.Description = fields.Description;
        campaign.DiscountType = fields.DiscountType;
        campaign.DiscountValue = fields.DiscountValue;
        campaign.StartsAtUtc = fields.StartsAtUtc;
        campaign.EndsAtUtc = fields.EndsAtUtc;
        campaign.IsActive = fields.IsActive;
        campaign.BannerAssetId = fields.BannerAssetId;

        // **The targets are replaced wholesale**, which is what a form that sends the whole collection
        // means. Clearing and re-adding rather than diffing: the rows carry nothing but their identity —
        // no audit columns, no row version, nothing an id is worth keeping for — and a diff would be a
        // second place for "a target this body does not mention" to be read as "leave it alone". The
        // cleared rows are deleted by EF as orphans of a required relationship, and that is a hard delete
        // of a child row, which doc 08 §1.6 permits: `SaleCampaignTargets` is on the cascade list and is
        // not marked [SD].
        campaign.Targets.Clear();

        foreach (var target in fields.Targets)
        {
            campaign.Targets.Add(new SaleCampaignTarget
            {
                TargetType = target.TargetType,
                DepartmentId = target.TargetType == SaleCampaignTargetType.Department
                    ? target.TargetId
                    : null,
                CategoryId = target.TargetType == SaleCampaignTargetType.Category ? target.TargetId : null,
                ProductId = target.TargetType == SaleCampaignTargetType.Product ? target.TargetId : null,
                IncludeSubcategories = target.IncludeSubcategories,
            });
        }
    }

    /// <summary>The audit snapshot: every field a staff member can change, and nothing else.</summary>
    private static Dictionary<string, object?> Snapshot(SaleCampaign campaign) =>
        new(StringComparer.Ordinal)
        {
            ["name"] = campaign.Name,
            ["description"] = campaign.Description,
            ["discountType"] = campaign.DiscountType.ToString(),
            ["discountValue"] = campaign.DiscountValue,

            // UTC in the trail, like every other timestamp in ops.AuditLogs; the Colombo conversion stays
            // at the screen's edge. A trail with two timezones in it is a trail nobody can order.
            ["startsAtUtc"] = campaign.StartsAtUtc,
            ["endsAtUtc"] = campaign.EndsAtUtc,
            ["isActive"] = campaign.IsActive,
            ["bannerAssetId"] = campaign.BannerAssetId,

            // The targets as text, because the trail's value is a staff member reading "it used to be
            // Women and now it is Women › Jeans" — which a list of ids cannot tell them.
            ["targets"] = string.Join(
                ", ",
                campaign.Targets.Select(Describe).Order(StringComparer.Ordinal)),
        };

    /// <summary>
    /// One target as the audit trail spells it: the type, the id, and — for a category that does
    /// <b>not</b> include its subtree — that it is narrowed, because widening a category target is a
    /// change to what the shop discounts that a bare id would hide.
    /// </summary>
    private static string Describe(SaleCampaignTarget target)
    {
        var id = target.DepartmentId ?? target.CategoryId ?? target.ProductId;
        var narrowed = target.TargetType == SaleCampaignTargetType.Category && !target.IncludeSubcategories;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{target.TargetType}:{id}{(narrowed ? " (this category only)" : string.Empty)}");
    }

    /// <summary>
    /// The row for a <b>delete</b>'s response, built from the entity rather than re-read: the reader
    /// filters soft-deleted rows out, so there would be nothing to read back.
    /// </summary>
    private static AdminSaleCampaignDto ToDto(SaleCampaign campaign, DateTime utcNow) =>
        new(
            campaign.Id,
            campaign.Name,
            campaign.Description,
            campaign.DiscountType,
            campaign.DiscountValue,
            CampaignWindow.ToColombo(campaign.StartsAtUtc),
            CampaignWindow.ToColombo(campaign.EndsAtUtc),
            campaign.IsActive,

            // Null on a delete's response: the row is gone from the screen, and resolving the asset's URL
            // is the reader's job.
            null,

            // Empty, and deliberately not the entity's own targets: resolving them to the names the DTO
            // carries needs the catalogue, which is the reader's job, and a removed campaign's targets are
            // not something the screen shows.
            [],
            CampaignWindow.StatusOf(campaign.IsActive, campaign.StartsAtUtc, campaign.EndsAtUtc, utcNow),
            campaign.RowVersion is { } bytes ? Convert.ToBase64String(bytes) : string.Empty);

    /// <summary>The input after validation: no nullable intentions left, and the dates in UTC.</summary>
    private sealed record ValidFields(
        string Name,
        string? Description,
        SaleDiscountType DiscountType,
        decimal DiscountValue,
        DateTime StartsAtUtc,
        DateTime EndsAtUtc,
        bool IsActive,
        Guid? BannerPublicId,
        int? BannerAssetId,
        string? BannerAltText,
        IReadOnlyList<ValidTarget> Targets);

    private sealed record ValidTarget(
        SaleCampaignTargetType TargetType,
        int TargetId,
        bool IncludeSubcategories);
}
