using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Application.Content.Admin.Home;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Application.Content.Admin.MegaMenu;

/// <summary>
/// Every department with its mega-menu cards, as the ADM-17 editor loads them (GLB-05, doc 11 P13 part D; #215).
/// </summary>
/// <param name="Departments">Every department, active or not, in menu order.</param>
/// <param name="MaxCardsPerDepartment">The server's own limit, so the editor and the API agree.</param>
public sealed record AdminMegaMenuContentDto(
    IReadOnlyList<AdminMegaMenuDepartmentDto> Departments,
    int MaxCardsPerDepartment);

/// <summary>One department and its cards.</summary>
/// <param name="Description">What the menu shows under the heading when no card is on (the fallback).</param>
/// <param name="HeroImage">The picture the menu shows when no card is on (the fallback), or null.</param>
/// <param name="Cards">Every card, on or off, in position order.</param>
public sealed record AdminMegaMenuDepartmentDto(
    int Id,
    string Name,
    string Slug,
    bool IsActive,
    string? Description,
    AdminMediaRefDto? HeroImage,
    IReadOnlyList<AdminMegaMenuCardDto> Cards);

/// <summary>One card as the editor lists it.</summary>
/// <param name="StartsAt">Colombo wall-clock (<c>yyyy-MM-ddTHH:mm</c>), or null.</param>
/// <param name="EndsAt">Colombo wall-clock, or null.</param>
/// <param name="IsLiveNow">Switched on and inside its schedule right now.</param>
/// <param name="IsShownNow">
/// The one card the storefront draws for this department right now: the first live card, by position, whose
/// picture is ready. At most one per department.
/// </param>
/// <param name="RowVersion">The concurrency token to send back, base64.</param>
public sealed record AdminMegaMenuCardDto(
    int Id,
    int DepartmentId,
    string Title,
    string? Subtitle,
    string ButtonLabel,
    string LinkUrl,
    AdminMediaRefDto? Image,
    short SortOrder,
    bool IsActive,
    string? StartsAt,
    string? EndsAt,
    bool IsLiveNow,
    bool IsShownNow,
    string RowVersion);

/// <summary>What a card write answers with.</summary>
public sealed record MegaMenuCardSavedDto(int Id, string? RowVersion);

/// <summary>The fields of one card as the form sends them. Every one is required in the body; null clears.</summary>
public sealed record MegaMenuCardFields(
    int? DepartmentId,
    string? Title,
    string? Subtitle,
    string? ButtonLabel,
    string? LinkUrl,
    Guid? ImagePublicId,
    int? SortOrder,
    bool IsActive,
    DateTime? StartsAt,
    DateTime? EndsAt);

public sealed record CreateMegaMenuCardCommand(MegaMenuCardFields Fields, AdminActor Actor);

public sealed record UpdateMegaMenuCardCommand(int CardId, MegaMenuCardFields Fields, string? RowVersion, AdminActor Actor);

public sealed record DeleteMegaMenuCardCommand(int CardId, string? RowVersion, AdminActor Actor);

/// <summary>
/// The mega-menu card writes of ADM-17 (GLB-05, doc 11 P13 part D, #215): add, edit, remove.
/// <para>
/// <b>#192's rules, unchanged.</b> Plain text stored as typed and rendered by interpolation (control characters
/// refused); a link that is a path on this site or a full http(s) address with no credentials
/// (<see cref="ContentFieldRules.OptionalLink"/>); a picture chosen from the media library by public id, which
/// must be a ready, public image with alt text (<see cref="ContentMediaRules"/>); a schedule in the shop's own
/// time; a row version on every edit and delete; an audit row of field diffs, none for an untouched resave; and
/// the <c>MediaUsages</c> rows rewritten in the same transaction as the card, so the media library refuses to
/// delete a picture a menu is showing.
/// </para>
/// <para>
/// <b>The cache.</b> The departments feed the mega menu reads (<c>GET /api/catalog/departments</c>) is not
/// cached on the server — it is read live on every request — so a save is on the next page that asks. The
/// homepage cache is evicted all the same, as every ADM-17 write does, so no content write is the one that
/// forgot.
/// </para>
/// </summary>
public sealed class MegaMenuCardWriteHandlers(
    IMegaMenuCardAdminStore store,
    IAuditLogWriter auditLog,
    IHomePageCache homePageCache,
    IClock clock)
    : ICommandHandler<CreateMegaMenuCardCommand, ContentWriteResult>,
      ICommandHandler<UpdateMegaMenuCardCommand, ContentWriteResult>,
      ICommandHandler<DeleteMegaMenuCardCommand, ContentWriteResult>
{
    /// <summary><c>ops.AuditLogs.EntityType</c> for a card. Doc 08 §3.3's owner-type name.</summary>
    public const string EntityType = "MegaMenuPromoCard";

    public async Task<ContentWriteResult> HandleAsync(CreateMegaMenuCardCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        var values = await ReadAsync(errors, command.Fields, cancellationToken);

        if (errors.Count > 0)
        {
            return new ContentWriteResult.Invalid(errors);
        }

        if (await store.CountForDepartmentAsync(values.DepartmentId, cancellationToken)
            >= MegaMenuPromoCardLimits.MaxCardsPerDepartment)
        {
            return TooMany();
        }

        var card = new MegaMenuPromoCard();
        values.ApplyTo(card);
        store.Add(card);

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.Create,
            EntityType,
            "new",
            before: null,
            Snapshot(card),
            command.Actor,
            clock.UtcNow.UtcDateTime));

        return await SavedAsync(card, command.Actor, cancellationToken);
    }

    public async Task<ContentWriteResult> HandleAsync(UpdateMegaMenuCardCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var card = await store.FindAsync(command.CardId, cancellationToken);
        if (card is null)
        {
            return NotFound(command.CardId);
        }

        if (ContentWriteSupport.CheckRowVersion(card.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var errors = new List<ValidationError>();
        var values = await ReadAsync(errors, command.Fields, cancellationToken);

        if (errors.Count > 0)
        {
            return new ContentWriteResult.Invalid(errors);
        }

        // Moving a card to another department counts against that department's limit; staying put does not.
        if (values.DepartmentId != card.DepartmentId
            && await store.CountForDepartmentAsync(values.DepartmentId, cancellationToken)
                >= MegaMenuPromoCardLimits.MaxCardsPerDepartment)
        {
            return TooMany();
        }

        var before = Snapshot(card);
        values.ApplyTo(card);

        var audit = ContentAuditTrail.ForChange(
            AuditActions.Update,
            EntityType,
            Id(card.Id),
            before,
            Snapshot(card),
            command.Actor,
            clock.UtcNow.UtcDateTime);

        if (audit is null)
        {
            return new ContentWriteResult.Unchanged(card.Id);
        }

        auditLog.Enlist(audit);

        return await SavedAsync(card, command.Actor, cancellationToken);
    }

    public async Task<ContentWriteResult> HandleAsync(DeleteMegaMenuCardCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var card = await store.FindAsync(command.CardId, cancellationToken);
        if (card is null)
        {
            return NotFound(command.CardId);
        }

        if (ContentWriteSupport.CheckRowVersion(card.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.Delete,
            EntityType,
            Id(card.Id),
            Snapshot(card),
            after: null,
            command.Actor,
            clock.UtcNow.UtcDateTime));

        // A hard delete — the table has no soft-delete flag (doc 08 §3.7) — and its usage rows go with it in the
        // same transaction, so a removed card does not hold a photograph's delete guard shut for ever.
        var cardId = card.Id;
        store.Remove(card);

        var outcome = await store.SaveAsync(
            command.Actor.UserId,
            new ContentMediaUsageChange(MediaUsageOwnerTypes.MegaMenuPromoCard, () => cardId, []),
            cancellationToken);

        if (outcome == ContentSaveOutcome.ConcurrencyConflict)
        {
            return StaleConflict();
        }

        homePageCache.Evict();
        return new ContentWriteResult.Succeeded(cardId);
    }

    private async Task<Values> ReadAsync(List<ValidationError> errors, MegaMenuCardFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var departmentId = 0;
        if (fields.DepartmentId is not { } requested)
        {
            errors.Add(new ValidationError("departmentId", "Choose the department whose menu shows this card."));
        }
        else if (!await store.DepartmentExistsAsync(requested, cancellationToken))
        {
            errors.Add(new ValidationError("departmentId", "That department does not exist. Reload the page."));
        }
        else
        {
            departmentId = requested;
        }

        var title = ContentFieldRules.RequiredText(errors, "title", fields.Title, MegaMenuPromoCardLimits.TitleMaxLength);
        var subtitle = ContentFieldRules.OptionalText(errors, "subtitle", fields.Subtitle, MegaMenuPromoCardLimits.SubtitleMaxLength);
        var buttonLabel = ContentFieldRules.RequiredText(errors, "buttonLabel", fields.ButtonLabel, ContentFieldLimits.ButtonLabelMaxLength);

        string? linkUrl = null;
        if (string.IsNullOrWhiteSpace(fields.LinkUrl))
        {
            errors.Add(new ValidationError("linkUrl", "Say where the card goes: a page on this shop such as /new-arrivals."));
        }
        else
        {
            linkUrl = ContentFieldRules.OptionalLink(errors, "linkUrl", fields.LinkUrl, mayLeaveSite: true);
        }

        var sortOrder = ContentFieldRules.SortOrder(errors, "sortOrder", fields.SortOrder);
        var (startsAtUtc, endsAtUtc) = ContentFieldRules.Schedule(errors, "endsAt", fields.StartsAt, fields.EndsAt);

        var imageAssetId = await ContentMediaRules.ResolveAsync(
            errors, store, "imagePublicId", fields.ImagePublicId, MediaAssetType.Image, cancellationToken);

        // The storefront draws a card only with its picture; a switched-on card without one would be a card that
        // silently never appears. Saved switched off, it may wait for one.
        if (fields.IsActive && fields.ImagePublicId is null)
        {
            errors.Add(new ValidationError(
                "imagePublicId",
                "A card needs an image before it can be shown. Save it switched off until you have one."));
        }

        return new Values(
            departmentId,
            title ?? string.Empty,
            subtitle,
            buttonLabel ?? string.Empty,
            linkUrl ?? string.Empty,
            imageAssetId,
            sortOrder,
            fields.IsActive,
            startsAtUtc,
            endsAtUtc);
    }

    private async Task<ContentWriteResult> SavedAsync(MegaMenuPromoCard card, AdminActor actor, CancellationToken cancellationToken)
    {
        var outcome = await store.SaveAsync(actor.UserId, Usage(card), cancellationToken);

        if (outcome == ContentSaveOutcome.ConcurrencyConflict)
        {
            return StaleConflict();
        }

        homePageCache.Evict();

        return new ContentWriteResult.Succeeded(
            card.Id,
            [],
            card.RowVersion is { } version ? Convert.ToBase64String(version) : null);
    }

    private static ContentMediaUsageChange Usage(MegaMenuPromoCard card) =>
        new(
            MediaUsageOwnerTypes.MegaMenuPromoCard,
            () => card.Id,
            card.ImageAssetId is { } image ? [new ContentMediaUsage(image, MediaUsageRoles.Image)] : []);

    private static Dictionary<string, object?> Snapshot(MegaMenuPromoCard card) => new(StringComparer.Ordinal)
    {
        ["departmentId"] = card.DepartmentId,
        ["title"] = card.Title,
        ["subtitle"] = card.Subtitle,
        ["buttonLabel"] = card.ButtonLabel,
        ["linkUrl"] = card.LinkUrl,
        ["imageAssetId"] = card.ImageAssetId,
        ["sortOrder"] = card.SortOrder,
        ["isActive"] = card.IsActive,
        ["startsAtUtc"] = card.StartsAtUtc,
        ["endsAtUtc"] = card.EndsAtUtc,
    };

    private static ContentWriteResult.Conflict TooMany() =>
        new(
            ContentConflictCodes.TooManyMegaMenuCards,
            string.Create(
                CultureInfo.InvariantCulture,
                $"This department already holds {MegaMenuPromoCardLimits.MaxCardsPerDepartment} cards. Remove one you no longer need first."));

    private static ContentWriteResult.NotFound NotFound(int id) =>
        new(string.Create(CultureInfo.InvariantCulture, $"No mega-menu card has the id {id}."));

    private static ContentWriteResult.Conflict StaleConflict() =>
        new(
            ContentConflictCodes.StaleRowVersion,
            "Somebody else changed this card while you were editing it. Reload to see their change.");

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    private sealed record Values(
        int DepartmentId,
        string Title,
        string? Subtitle,
        string ButtonLabel,
        string LinkUrl,
        int? ImageAssetId,
        short SortOrder,
        bool IsActive,
        DateTime? StartsAtUtc,
        DateTime? EndsAtUtc)
    {
        public void ApplyTo(MegaMenuPromoCard card)
        {
            card.DepartmentId = DepartmentId;
            card.Title = Title;
            card.Subtitle = Subtitle;
            card.ButtonLabel = ButtonLabel;
            card.LinkUrl = LinkUrl;
            card.ImageAssetId = ImageAssetId;
            card.SortOrder = SortOrder;
            card.IsActive = IsActive;
            card.StartsAtUtc = StartsAtUtc;
            card.EndsAtUtc = EndsAtUtc;
        }
    }
}
