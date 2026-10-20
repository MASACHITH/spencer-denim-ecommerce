using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Application.Content.Admin.Home;

/// <summary>
/// Edit one homepage block's own fields (doc 11 P13 step 6): heading, switch, position, schedule and the
/// per-type settings. A full replacement — every field is sent, and a field the block does not use must be
/// <see langword="null"/>.
/// </summary>
/// <param name="StartsAt">Colombo wall-clock, or null for "no start".</param>
/// <param name="EndsAt">Colombo wall-clock, or null for "no end".</param>
public sealed record UpdateHomeSectionCommand(
    int SectionId,
    string? Title,
    string? Subtitle,
    bool IsEnabled,
    int? SortOrder,
    DateTime? StartsAt,
    DateTime? EndsAt,
    HomeSectionMode? Mode,
    int? Count,
    string? ListingUrl,
    string? LinkLabel,
    string? Overlay,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// The fields of one homepage item as a form sends them (doc 11 P13 step 7). Media slots are public ids
/// from the media library; schedules are Colombo wall-clock.
/// </summary>
public sealed record HomeSectionItemFields(
    string? Title,
    string? Subtitle,
    string? Body,
    string? ButtonLabel,
    string? LinkUrl,
    Guid? ImagePublicId,
    Guid? MobileImagePublicId,
    Guid? VideoPublicId,
    int? DepartmentId,
    string? ProductSlug,
    string? IconKey,
    int? SortOrder,
    bool IsEnabled,
    DateTime? StartsAt,
    DateTime? EndsAt);

/// <summary>Add an item to a block. <paramref name="RowVersion"/> is the <b>section's</b>.</summary>
public sealed record CreateHomeSectionItemCommand(
    int SectionId,
    HomeSectionItemFields Fields,
    string? RowVersion,
    AdminActor Actor);

/// <summary>Edit an item. <paramref name="RowVersion"/> is the <b>section's</b>.</summary>
public sealed record UpdateHomeSectionItemCommand(
    int ItemId,
    HomeSectionItemFields Fields,
    string? RowVersion,
    AdminActor Actor);

/// <summary>Remove an item. <paramref name="RowVersion"/> is the <b>section's</b>.</summary>
public sealed record DeleteHomeSectionItemCommand(int ItemId, string? RowVersion, AdminActor Actor);

/// <summary>
/// The homepage writes of ADM-17 (doc 11 P13 part B). One class, because they share the block rules, the
/// audit shape and the one thing doc 08 §3.7 asks of every one of them: "homepage output cached 5 min and
/// <b>evicted on save</b>".
/// <para>
/// <b>The section is the aggregate.</b> <c>content.HomeSectionItems</c> has no row version of its own —
/// it is a pure child row (doc 08 §1.6) — so an item write is checked against the section's, and moves it.
/// Two editors arranging the same block is the conflict worth catching; two editing different blocks never
/// collide.
/// </para>
/// </summary>
public sealed class HomeContentWriteHandlers(
    IHomeContentAdminStore store,
    IAuditLogWriter auditLog,
    IHomePageCache homePageCache,
    CatalogOptions catalogOptions,
    IClock clock)
    : ICommandHandler<UpdateHomeSectionCommand, ContentWriteResult>,
      ICommandHandler<CreateHomeSectionItemCommand, ContentWriteResult>,
      ICommandHandler<UpdateHomeSectionItemCommand, ContentWriteResult>,
      ICommandHandler<DeleteHomeSectionItemCommand, ContentWriteResult>
{
    /// <summary><c>ops.AuditLogs.EntityType</c> for a block.</summary>
    public const string SectionEntityType = "HomeSection";

    /// <summary><c>ops.AuditLogs.EntityType</c> for an item.</summary>
    public const string ItemEntityType = "HomeSectionItem";

    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<ContentWriteResult> HandleAsync(
        UpdateHomeSectionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var section = await store.FindSectionAsync(command.SectionId, cancellationToken);
        if (section is null)
        {
            return SectionNotFound(command.SectionId);
        }

        if (ContentWriteSupport.CheckRowVersion(section.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var rule = HomeBlockRules.For(section.SectionType);
        var errors = new List<ValidationError>();

        var title = SectionText(errors, rule, HomeBlockRules.SectionFields.Title, command.Title, ContentFieldLimits.TitleMaxLength);
        var subtitle = SectionText(errors, rule, HomeBlockRules.SectionFields.Subtitle, command.Subtitle, ContentFieldLimits.SubtitleMaxLength);
        var sortOrder = ContentFieldRules.SortOrder(errors, "sortOrder", command.SortOrder);
        var (startsAtUtc, endsAtUtc) = ContentFieldRules.Schedule(errors, "endsAt", command.StartsAt, command.EndsAt);
        var settings = ReadSettings(errors, rule, section.SectionType, command);

        if (errors.Count > 0)
        {
            return new ContentWriteResult.Invalid(errors);
        }

        var before = Snapshot(section);

        section.Title = title;
        section.Subtitle = subtitle;
        section.IsEnabled = command.IsEnabled;
        section.SortOrder = sortOrder;
        section.StartsAtUtc = startsAtUtc;
        section.EndsAtUtc = endsAtUtc;

        // The column is rewritten only when what it *means* changed. Its stored spelling may differ from
        // what this class writes — a seeded `{"mode":"manual"}` means count 8 just as much as
        // `{"mode":"manual","count":8}` does — and a save of an untouched form must not be an event.
        if (!SameSettings(HomeSectionSettings.Parse(section.SettingsJson), settings, rule))
        {
            section.SettingsJson = settings.Json;
        }

        var after = Snapshot(section);
        var audit = ContentAuditTrail.ForChange(
            AuditActions.Update,
            SectionEntityType,
            Id(section.Id),
            before,
            after,
            command.Actor,
            clock.UtcNow.UtcDateTime);

        if (audit is null)
        {
            return new ContentWriteResult.Unchanged(section.Id);
        }

        auditLog.Enlist(audit);

        return await SaveAsync(section, section.Id, command.Actor, usage: null, cancellationToken);
    }

    public async Task<ContentWriteResult> HandleAsync(
        CreateHomeSectionItemCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var section = await store.FindSectionAsync(command.SectionId, cancellationToken);
        if (section is null)
        {
            return SectionNotFound(command.SectionId);
        }

        if (ContentWriteSupport.CheckRowVersion(section.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var rule = HomeBlockRules.For(section.SectionType);
        if (ItemsRefusal(rule, section, creating: true) is { } refused)
        {
            return refused;
        }

        var errors = new List<ValidationError>();
        var values = await ReadItemAsync(errors, rule, command.Fields, cancellationToken);

        if (errors.Count > 0)
        {
            return new ContentWriteResult.Invalid(errors);
        }

        var item = new HomeSectionItem { HomeSectionId = section.Id };
        values.ApplyTo(item);

        store.AddItem(item);
        store.MarkChanged(section);

        var after = Snapshot(item);
        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.Create,
            ItemEntityType,
            "new",
            before: null,
            after,
            command.Actor,
            clock.UtcNow.UtcDateTime));

        return await SaveAsync(
            section,
            entityId: null,
            command.Actor,
            new ContentMediaUsageChange(
                MediaUsageOwnerTypes.HomeSectionItem,
                () => item.Id,
                HomeItemMediaUsages.For(section.SectionType, item)),
            cancellationToken,
            createdItem: item);
    }

    public async Task<ContentWriteResult> HandleAsync(
        UpdateHomeSectionItemCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var section = await store.FindSectionOfItemAsync(command.ItemId, cancellationToken);
        var item = section?.Items.FirstOrDefault(candidate => candidate.Id == command.ItemId);
        if (section is null || item is null)
        {
            return ItemNotFound(command.ItemId);
        }

        if (ContentWriteSupport.CheckRowVersion(section.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var rule = HomeBlockRules.For(section.SectionType);
        if (ItemsRefusal(rule, section, creating: false) is { } refused)
        {
            return refused;
        }

        var errors = new List<ValidationError>();
        var values = await ReadItemAsync(errors, rule, command.Fields, cancellationToken);

        if (errors.Count > 0)
        {
            return new ContentWriteResult.Invalid(errors);
        }

        var before = Snapshot(item);
        values.ApplyTo(item);
        var after = Snapshot(item);

        var audit = ContentAuditTrail.ForChange(
            AuditActions.Update,
            ItemEntityType,
            Id(item.Id),
            before,
            after,
            command.Actor,
            clock.UtcNow.UtcDateTime);

        if (audit is null)
        {
            return new ContentWriteResult.Unchanged(item.Id);
        }

        auditLog.Enlist(audit);
        store.MarkChanged(section);

        return await SaveAsync(
            section,
            item.Id,
            command.Actor,
            new ContentMediaUsageChange(
                MediaUsageOwnerTypes.HomeSectionItem,
                () => item.Id,
                HomeItemMediaUsages.For(section.SectionType, item)),
            cancellationToken);
    }

    public async Task<ContentWriteResult> HandleAsync(
        DeleteHomeSectionItemCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var section = await store.FindSectionOfItemAsync(command.ItemId, cancellationToken);
        var item = section?.Items.FirstOrDefault(candidate => candidate.Id == command.ItemId);
        if (section is null || item is null)
        {
            return ItemNotFound(command.ItemId);
        }

        if (ContentWriteSupport.CheckRowVersion(section.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var itemId = item.Id;

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.Delete,
            ItemEntityType,
            Id(itemId),
            Snapshot(item),
            after: null,
            command.Actor,
            clock.UtcNow.UtcDateTime));

        store.RemoveItem(item);
        store.MarkChanged(section);

        return await SaveAsync(
            section,
            itemId,
            command.Actor,
            new ContentMediaUsageChange(MediaUsageOwnerTypes.HomeSectionItem, () => itemId, []),
            cancellationToken);
    }

    /// <summary>
    /// Saves, and on success evicts the homepage cache — doc 08 §3.7's "evicted on save", which is what
    /// makes a staff member's change visible to the next visitor rather than five minutes later.
    /// </summary>
    private async Task<ContentWriteResult> SaveAsync(
        HomeSection section,
        int? entityId,
        AdminActor actor,
        ContentMediaUsageChange? usage,
        CancellationToken cancellationToken,
        HomeSectionItem? createdItem = null)
    {
        var outcome = await store.SaveAsync(actor.UserId, usage, cancellationToken);

        if (outcome == ContentSaveOutcome.ConcurrencyConflict)
        {
            return StaleSection();
        }

        homePageCache.Evict();

        return new ContentWriteResult.Succeeded(
            entityId ?? createdItem?.Id ?? section.Id,
            [],
            section.RowVersion is { } version ? Convert.ToBase64String(version) : null);
    }

    // ------------------------------------------------------------------ section fields

    private static string? SectionText(
        List<ValidationError> errors,
        HomeBlockRule rule,
        string field,
        string? value,
        int maxLength)
    {
        if (!rule.UsesSectionField(field))
        {
            RefuseUnused(errors, field, value);
            return null;
        }

        return ContentFieldRules.OptionalText(errors, field, value, maxLength);
    }

    private SectionSettings ReadSettings(
        List<ValidationError> errors,
        HomeBlockRule rule,
        HomeSectionType type,
        UpdateHomeSectionCommand command)
    {
        // Mode: chosen only for trending. New Arrivals and Sale are always drawn from their listing — that
        // is what HOME-03 and HOME-07 are — so they carry "auto" without asking.
        HomeSectionMode? mode = null;
        if (rule.UsesSectionField(HomeBlockRules.SectionFields.Mode))
        {
            if (command.Mode is null)
            {
                errors.Add(new ValidationError(HomeBlockRules.SectionFields.Mode, "Choose automatic or hand-picked."));
            }

            mode = command.Mode ?? HomeSectionMode.Auto;
        }
        else
        {
            if (command.Mode is not null)
            {
                errors.Add(Unused(HomeBlockRules.SectionFields.Mode));
            }

            mode = HomeBlockRules.IsProductRow(type) ? HomeSectionMode.Auto : null;
        }

        int? count = null;
        if (rule.UsesSectionField(HomeBlockRules.SectionFields.Count))
        {
            if (command.Count is null || command.Count < 1 || command.Count > HomeSectionSettings.MaxCount)
            {
                errors.Add(new ValidationError(
                    HomeBlockRules.SectionFields.Count,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Show between 1 and {HomeSectionSettings.MaxCount} products.")));
            }
            else
            {
                count = command.Count;
            }
        }
        else if (command.Count is not null)
        {
            errors.Add(Unused(HomeBlockRules.SectionFields.Count));
        }

        string? listingUrl = null;
        if (rule.UsesSectionField(HomeBlockRules.SectionFields.ListingUrl))
        {
            listingUrl = ContentFieldRules.OptionalLink(
                errors, HomeBlockRules.SectionFields.ListingUrl, command.ListingUrl, mayLeaveSite: false);

            if (listingUrl is null && mode == HomeSectionMode.Auto && !HasError(errors, HomeBlockRules.SectionFields.ListingUrl))
            {
                errors.Add(new ValidationError(
                    HomeBlockRules.SectionFields.ListingUrl,
                    "Say which listing the row shows, such as /new-arrivals or /women/dresses."));
            }
            else if (listingUrl is not null && !IsAnswerableListing(listingUrl))
            {
                // The row and its "view all" are the same query (ListingUrl), so a filter the catalogue
                // cannot answer would be an empty row here and a 400 on the listing page.
                errors.Add(new ValidationError(
                    HomeBlockRules.SectionFields.ListingUrl,
                    "The shop cannot show that listing: check the filters after the '?'."));
                listingUrl = null;
            }
        }
        else
        {
            RefuseUnused(errors, HomeBlockRules.SectionFields.ListingUrl, command.ListingUrl);
        }

        string? linkLabel = null;
        if (rule.UsesSectionField(HomeBlockRules.SectionFields.LinkLabel))
        {
            linkLabel = ContentFieldRules.OptionalText(
                errors, HomeBlockRules.SectionFields.LinkLabel, command.LinkLabel, ContentFieldLimits.ButtonLabelMaxLength);
        }
        else
        {
            RefuseUnused(errors, HomeBlockRules.SectionFields.LinkLabel, command.LinkLabel);
        }

        string? overlay = null;
        if (rule.UsesSectionField(HomeBlockRules.SectionFields.Overlay))
        {
            var wanted = string.IsNullOrWhiteSpace(command.Overlay) ? null : command.Overlay.Trim().ToLowerInvariant();
            if (wanted is not null && !HomeBlockRules.Overlays.Contains(wanted, StringComparer.Ordinal))
            {
                errors.Add(new ValidationError(HomeBlockRules.SectionFields.Overlay, "Use dark, light or none."));
            }
            else
            {
                overlay = wanted;
            }
        }
        else
        {
            RefuseUnused(errors, HomeBlockRules.SectionFields.Overlay, command.Overlay);
        }

        return new SectionSettings(mode, count, listingUrl, linkLabel, overlay, ToJson(mode, count, listingUrl, linkLabel, overlay));
    }

    private bool IsAnswerableListing(string listingUrl)
    {
        var criteria = ListingUrl.ToCriteria(listingUrl, HomeSectionSettings.DefaultCount);
        return criteria is not null && ProductListQuery.Create(criteria, catalogOptions).IsSuccess;
    }

    /// <summary>The settings column as this class writes it: only the keys the block uses.</summary>
    private static string? ToJson(HomeSectionMode? mode, int? count, string? listingUrl, string? linkLabel, string? overlay)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);

        if (mode is { } chosen)
        {
            values["mode"] = chosen == HomeSectionMode.Manual ? "manual" : "auto";
        }

        if (count is { } number)
        {
            values["count"] = number;
        }

        if (listingUrl is not null)
        {
            values["listingUrl"] = listingUrl;
        }

        if (linkLabel is not null)
        {
            values["linkLabel"] = linkLabel;
        }

        if (overlay is not null)
        {
            values["overlay"] = overlay;
        }

        return values.Count == 0 ? null : JsonSerializer.Serialize(values, SettingsJsonOptions);
    }

    private static bool SameSettings(HomeSectionSettings stored, SectionSettings wanted, HomeBlockRule rule)
    {
        var sameMode = !rule.UsesSectionField(HomeBlockRules.SectionFields.Mode) || stored.Mode == wanted.Mode;
        var sameCount = !rule.UsesSectionField(HomeBlockRules.SectionFields.Count) || stored.Count == wanted.Count;

        return sameMode
            && sameCount
            && string.Equals(stored.ListingUrl, wanted.ListingUrl, StringComparison.Ordinal)
            && string.Equals(stored.LinkLabel, wanted.LinkLabel, StringComparison.Ordinal)
            && string.Equals(stored.Overlay, wanted.Overlay, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ item fields

    private static ContentWriteResult.Conflict? ItemsRefusal(HomeBlockRule rule, HomeSection section, bool creating)
    {
        if (rule.MaxItems == 0)
        {
            return new ContentWriteResult.Conflict(
                ContentConflictCodes.ItemsNotSupported,
                "This block has no items of its own: it shows products from a listing, or a form.");
        }

        if (creating && section.Items.Count >= rule.MaxItems)
        {
            return new ContentWriteResult.Conflict(
                ContentConflictCodes.SectionFull,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"This block holds at most {rule.MaxItems} item{(rule.MaxItems == 1 ? string.Empty : "s")}. Edit or remove one instead."),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["maxItems"] = rule.MaxItems });
        }

        return null;
    }

    private async Task<ItemValues> ReadItemAsync(
        List<ValidationError> errors,
        HomeBlockRule rule,
        HomeSectionItemFields fields,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var title = ItemText(errors, rule, HomeBlockRules.ItemFields.Title, fields.Title, ContentFieldLimits.TitleMaxLength);
        var subtitle = ItemText(errors, rule, HomeBlockRules.ItemFields.Subtitle, fields.Subtitle, ContentFieldLimits.SubtitleMaxLength);
        var body = ItemText(errors, rule, HomeBlockRules.ItemFields.Body, fields.Body, ContentFieldLimits.BodyMaxLength);
        var buttonLabel = ItemText(errors, rule, HomeBlockRules.ItemFields.ButtonLabel, fields.ButtonLabel, ContentFieldLimits.ButtonLabelMaxLength);

        string? linkUrl = null;
        if (rule.UsesItemField(HomeBlockRules.ItemFields.LinkUrl))
        {
            linkUrl = ContentFieldRules.OptionalLink(errors, HomeBlockRules.ItemFields.LinkUrl, fields.LinkUrl, rule.LinkMayLeaveSite);
        }
        else
        {
            RefuseUnused(errors, HomeBlockRules.ItemFields.LinkUrl, fields.LinkUrl);
        }

        var imageAssetId = await MediaAsync(errors, rule, HomeBlockRules.ItemFields.Image, "imagePublicId", fields.ImagePublicId, MediaAssetType.Image, cancellationToken);
        var mobileImageAssetId = await MediaAsync(errors, rule, HomeBlockRules.ItemFields.MobileImage, "mobileImagePublicId", fields.MobileImagePublicId, MediaAssetType.Image, cancellationToken);
        var videoAssetId = await MediaAsync(errors, rule, HomeBlockRules.ItemFields.Video, "videoPublicId", fields.VideoPublicId, MediaAssetType.Video, cancellationToken);

        // Doc 08 §3.7's CK, said as a sentence before the database says it as an error: a phone, a data
        // saver and prefers-reduced-motion all need a still.
        if (fields.VideoPublicId is not null && fields.ImagePublicId is null && fields.MobileImagePublicId is null)
        {
            errors.Add(new ValidationError(
                "imagePublicId",
                "A hero video needs a still image as well, for phones and for people who turn motion off."));
        }

        // #192 tester (UX): a hero or banner item is its picture. The storefront draws a hero or banner block
        // only when an item carries artwork (hasContent in home-sections.ts) and silently draws nothing
        // otherwise, so an item switched on with no picture was a save that "worked" and changed nothing a
        // shopper could see. It may be saved switched off while the picture is on its way.
        if (HomeBlockRules.NeedsArtworkToShow(rule) && fields.IsEnabled
            && fields.ImagePublicId is null && fields.MobileImagePublicId is null
            && !HasError(errors, "imagePublicId"))
        {
            errors.Add(new ValidationError(
                "imagePublicId",
                "Add a picture (or a video with its still) before switching this on. It can be saved switched off until then."));
        }

        int? departmentId = null;
        if (rule.UsesItemField(HomeBlockRules.ItemFields.Department))
        {
            if (fields.DepartmentId is { } wanted)
            {
                if (await store.DepartmentExistsAsync(wanted, cancellationToken))
                {
                    departmentId = wanted;
                }
                else
                {
                    errors.Add(new ValidationError("departmentId", "Choose one of the shop's departments."));
                }
            }
        }
        else if (fields.DepartmentId is not null)
        {
            errors.Add(Unused("departmentId"));
        }

        int? productId = null;
        if (rule.UsesItemField(HomeBlockRules.ItemFields.Product))
        {
            var slug = string.IsNullOrWhiteSpace(fields.ProductSlug) ? null : fields.ProductSlug.Trim();
            if (slug is not null)
            {
                productId = await store.FindProductIdAsync(slug, cancellationToken);
                if (productId is null)
                {
                    errors.Add(new ValidationError("productSlug", "No product has that address."));
                }
            }
        }
        else
        {
            RefuseUnused(errors, "productSlug", fields.ProductSlug);
        }

        string? iconKey = null;
        if (rule.UsesItemField(HomeBlockRules.ItemFields.IconKey))
        {
            var wanted = string.IsNullOrWhiteSpace(fields.IconKey) ? null : fields.IconKey.Trim().ToLowerInvariant();
            if (wanted is not null && !HomeBlockRules.BenefitIcons.Contains(wanted, StringComparer.Ordinal))
            {
                errors.Add(new ValidationError(
                    HomeBlockRules.ItemFields.IconKey,
                    "Choose one of the icons the shop can draw: " + string.Join(", ", HomeBlockRules.BenefitIcons) + "."));
            }
            else
            {
                iconKey = wanted;
            }
        }
        else
        {
            RefuseUnused(errors, HomeBlockRules.ItemFields.IconKey, fields.IconKey);
        }

        RequireFilled(errors, rule, HomeBlockRules.ItemFields.Title, HomeBlockRules.ItemFields.Title, title, fields.Title);
        RequireFilled(errors, rule, HomeBlockRules.ItemFields.Image, "imagePublicId", imageAssetId, fields.ImagePublicId);
        RequireFilled(errors, rule, HomeBlockRules.ItemFields.Department, "departmentId", departmentId, fields.DepartmentId);
        RequireFilled(errors, rule, HomeBlockRules.ItemFields.IconKey, HomeBlockRules.ItemFields.IconKey, iconKey, fields.IconKey);

        var sortOrder = ContentFieldRules.SortOrder(errors, "sortOrder", fields.SortOrder);
        var (startsAtUtc, endsAtUtc) = ContentFieldRules.Schedule(errors, "endsAt", fields.StartsAt, fields.EndsAt);

        return new ItemValues(
            title,
            subtitle,
            body,
            buttonLabel,
            linkUrl,
            imageAssetId,
            mobileImageAssetId,
            videoAssetId,
            departmentId,
            productId,
            iconKey,
            sortOrder,
            fields.IsEnabled,
            startsAtUtc,
            endsAtUtc);
    }

    private static string? ItemText(List<ValidationError> errors, HomeBlockRule rule, string field, string? value, int maxLength)
    {
        if (!rule.UsesItemField(field))
        {
            RefuseUnused(errors, field, value);
            return null;
        }

        return ContentFieldRules.OptionalText(errors, field, value, maxLength);
    }

    private async Task<int?> MediaAsync(
        List<ValidationError> errors,
        HomeBlockRule rule,
        string ruleField,
        string requestField,
        Guid? publicId,
        MediaAssetType expected,
        CancellationToken cancellationToken)
    {
        if (!rule.UsesItemField(ruleField))
        {
            if (publicId is not null)
            {
                errors.Add(Unused(requestField));
            }

            return null;
        }

        return await ContentMediaRules.ResolveAsync(errors, store, requestField, publicId, expected, cancellationToken);
    }

    /// <summary>
    /// A required field that is empty. Only said when the value was not already refused for another reason,
    /// so a too-long title does not also read "required".
    /// </summary>
    private static void RequireFilled<T>(
        List<ValidationError> errors,
        HomeBlockRule rule,
        string ruleField,
        string requestField,
        T? resolved,
        object? supplied)
    {
        if (!rule.RequiresItemField(ruleField) || resolved is not null || HasError(errors, requestField))
        {
            return;
        }

        errors.Add(new ValidationError(
            requestField,
            supplied is null || supplied is string { Length: 0 } ? "This is required for this block." : "This is required."));
    }

    private static void RefuseUnused(List<ValidationError> errors, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            errors.Add(Unused(field));
        }
    }

    private static ValidationError Unused(string field) =>
        new(field, "This block does not use " + field + ". Send it as null.");

    private static bool HasError(List<ValidationError> errors, string field) =>
        errors.Exists(error => string.Equals(error.Field, field, StringComparison.Ordinal));

    // ------------------------------------------------------------------ audit

    private static Dictionary<string, object?> Snapshot(HomeSection section) => new(StringComparer.Ordinal)
    {
        ["title"] = section.Title,
        ["subtitle"] = section.Subtitle,
        ["isEnabled"] = section.IsEnabled,
        ["sortOrder"] = section.SortOrder,
        ["startsAtUtc"] = section.StartsAtUtc,
        ["endsAtUtc"] = section.EndsAtUtc,
        ["settingsJson"] = section.SettingsJson,
    };

    private static Dictionary<string, object?> Snapshot(HomeSectionItem item) => new(StringComparer.Ordinal)
    {
        ["homeSectionId"] = item.HomeSectionId,
        ["title"] = item.Title,
        ["subtitle"] = item.Subtitle,
        ["body"] = item.Body,
        ["buttonLabel"] = item.ButtonLabel,
        ["linkUrl"] = item.LinkUrl,
        ["imageAssetId"] = item.ImageAssetId,
        ["mobileImageAssetId"] = item.MobileImageAssetId,
        ["videoAssetId"] = item.VideoAssetId,
        ["departmentId"] = item.DepartmentId,
        ["productId"] = item.ProductId,
        ["iconKey"] = item.IconKey,
        ["sortOrder"] = item.SortOrder,
        ["isEnabled"] = item.IsEnabled,
        ["startsAtUtc"] = item.StartsAtUtc,
        ["endsAtUtc"] = item.EndsAtUtc,
    };

    private static ContentWriteResult.NotFound SectionNotFound(int sectionId) =>
        new(string.Create(CultureInfo.InvariantCulture, $"No homepage block has the id {sectionId}."));

    private static ContentWriteResult.NotFound ItemNotFound(int itemId) =>
        new(string.Create(CultureInfo.InvariantCulture, $"No homepage item has the id {itemId}."));

    private static ContentWriteResult.Conflict StaleSection() =>
        new(
            ContentConflictCodes.StaleRowVersion,
            "Somebody else changed this block while you were editing it. Reload to see their change, then re-apply yours.");

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The settings a section form resolved to, with the JSON this class would store for them.</summary>
    private sealed record SectionSettings(
        HomeSectionMode? Mode,
        int? Count,
        string? ListingUrl,
        string? LinkLabel,
        string? Overlay,
        string? Json);

    /// <summary>An item form's values once every one has been checked.</summary>
    private sealed record ItemValues(
        string? Title,
        string? Subtitle,
        string? Body,
        string? ButtonLabel,
        string? LinkUrl,
        int? ImageAssetId,
        int? MobileImageAssetId,
        int? VideoAssetId,
        int? DepartmentId,
        int? ProductId,
        string? IconKey,
        short SortOrder,
        bool IsEnabled,
        DateTime? StartsAtUtc,
        DateTime? EndsAtUtc)
    {
        public void ApplyTo(HomeSectionItem item)
        {
            item.Title = Title;
            item.Subtitle = Subtitle;
            item.Body = Body;
            item.ButtonLabel = ButtonLabel;
            item.LinkUrl = LinkUrl;
            item.ImageAssetId = ImageAssetId;
            item.MobileImageAssetId = MobileImageAssetId;
            item.VideoAssetId = VideoAssetId;
            item.DepartmentId = DepartmentId;
            item.ProductId = ProductId;
            item.IconKey = IconKey;
            item.SortOrder = SortOrder;
            item.IsEnabled = IsEnabled;
            item.StartsAtUtc = StartsAtUtc;
            item.EndsAtUtc = EndsAtUtc;
        }
    }
}

/// <summary>
/// The <c>media.MediaUsages</c> rows one homepage item owns (doc 08 §3.3: "an owner's save rewrites its
/// usage rows in the same transaction"). The roles follow the client-asset importer's, so a row the
/// importer filled and a row a staff member filled read the same in the "used in…" panel.
/// </summary>
public static class HomeItemMediaUsages
{
    public static IReadOnlyCollection<ContentMediaUsage> For(HomeSectionType type, HomeSectionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var usages = new HashSet<ContentMediaUsage>();

        if (item.ImageAssetId is { } image)
        {
            usages.Add(new ContentMediaUsage(
                image,
                type is HomeSectionType.CampaignBanner or HomeSectionType.ActivewearFeature
                    ? MediaUsageRoles.Banner
                    : MediaUsageRoles.Image));
        }

        if (item.MobileImageAssetId is { } mobile)
        {
            usages.Add(new ContentMediaUsage(mobile, MediaUsageRoles.MobileImage));
        }

        if (item.VideoAssetId is { } video)
        {
            usages.Add(new ContentMediaUsage(video, MediaUsageRoles.Video));
        }

        return usages;
    }
}
