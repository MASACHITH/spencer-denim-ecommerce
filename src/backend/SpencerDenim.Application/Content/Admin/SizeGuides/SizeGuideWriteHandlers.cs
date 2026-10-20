using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Application.Content.Admin.Home;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Application.Content.Admin.SizeGuides;

/// <summary>One size guide as the list shows it (PDP-04, doc 11 P13 step 11).</summary>
/// <param name="CategoryCount">Live categories pointing at it.</param>
/// <param name="ProductCount">Live products pointing at it as their own override.</param>
public sealed record AdminSizeGuideListItemDto(
    int Id,
    string Name,
    int CategoryCount,
    int ProductCount,
    bool HasImage,
    DateTime? UpdatedAtUtc,
    string RowVersion);

/// <summary>One size guide as the editor loads it.</summary>
/// <param name="Table">The stored chart, or null when it holds nothing a shopper could read.</param>
/// <param name="Limits">The bounds the editor should hold the chart to, from the server's own constants.</param>
public sealed record AdminSizeGuideDto(
    int Id,
    string Name,
    string? IntroHtml,
    SizeGuideTableDto? Table,
    string? MeasuringTipsHtml,
    AdminMediaRefDto? Image,
    int CategoryCount,
    int ProductCount,
    string RowVersion,
    SizeGuideLimitsDto Limits);

/// <summary>The write-path bounds of issue #70, sent so the editor and the API agree.</summary>
public sealed record SizeGuideLimitsDto(int MaxColumns, int MaxRows, int MaxCells, int MaxCellLength, int MaxHtmlLength)
{
    public static SizeGuideLimitsDto Current { get; } = new(
        SizeGuideTableInput.MaxColumns,
        SizeGuideTableInput.MaxRows,
        SizeGuideTableInput.MaxCells,
        SizeGuideTableInput.MaxCellLength,
        SizeGuideWriteHandlers.MaxHtmlLength);
}

/// <summary>What a size-guide save answers with.</summary>
/// <param name="RemovedFromIntro">What the sanitiser took out of the intro — shown even though the save worked.</param>
/// <param name="RemovedFromTips">The same for the measuring tips.</param>
public sealed record SizeGuideSavedDto(
    int Id,
    string? RowVersion,
    IReadOnlyList<string> RemovedFromIntro,
    IReadOnlyList<string> RemovedFromTips);

/// <summary>The fields of one size guide as the form sends them.</summary>
public sealed record SizeGuideFields(
    string? Name,
    string? IntroHtml,
    string? TableJson,
    string? MeasuringTipsHtml,
    Guid? ImagePublicId);

public sealed record CreateSizeGuideCommand(SizeGuideFields Fields, AdminActor Actor);

public sealed record UpdateSizeGuideCommand(int SizeGuideId, SizeGuideFields Fields, string? RowVersion, AdminActor Actor);

public sealed record DeleteSizeGuideCommand(int SizeGuideId, string? RowVersion, AdminActor Actor);

/// <summary>
/// The size-guide writes of ADM-17 (PDP-04, doc 11 P13 part E) — the first write path to
/// <c>catalog.SizeGuides</c>, and so the one issue #70 was waiting for.
/// <para>
/// <b>Both of #70's debts are paid here.</b> The table goes through <see cref="SizeGuideTableInput"/>,
/// which bounds the cell count before anything is allocated and refuses what the read path only
/// survives; and <c>IntroHtml</c> and <c>MeasuringTipsHtml</c> — the two fields the PDP-04 modal renders
/// as markup — go through the same allow-list sanitiser as a content page's body, on save, with what it
/// removed reported back to the editor.
/// </para>
/// <para>
/// <b>Content.* and not Categories.*</b>, because doc 11 P13's table puts size guides on the content
/// screens ("Homepage blocks, announcement bar, mega menu cards, size guides, content pages —
/// Content.View/Create/Edit/Delete"). Assigning a guide to a category stays on the category, under
/// Categories.Edit, as W1-12 built it.
/// </para>
/// </summary>
public sealed class SizeGuideWriteHandlers(
    ISizeGuideAdminStore store,
    IHtmlSanitiser sanitiser,
    IAuditLogWriter auditLog,
    IHomePageCache homePageCache,
    IClock clock)
    : ICommandHandler<CreateSizeGuideCommand, ContentWriteResult>,
      ICommandHandler<UpdateSizeGuideCommand, ContentWriteResult>,
      ICommandHandler<DeleteSizeGuideCommand, ContentWriteResult>
{
    /// <summary><c>ops.AuditLogs.EntityType</c> for a size guide.</summary>
    public const string EntityType = "SizeGuide";

    /// <summary><c>catalog.SizeGuides.Name</c> is <c>nvarchar(100)</c>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>
    /// The longest intro or measuring-tips HTML, after cleaning. A paragraph and a short list each; the
    /// guide rides on every inheriting product's payload, so this is a chart's notes, not a page.
    /// </summary>
    public const int MaxHtmlLength = 20_000;

    /// <summary>The two rich-text fields, which the audit row records by length only.</summary>
    private static readonly IReadOnlySet<string> RedactedFields =
        new HashSet<string>(StringComparer.Ordinal) { "introHtml", "measuringTipsHtml", "tableJson" };

    public async Task<ContentWriteResult> HandleAsync(CreateSizeGuideCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        var values = await ReadAsync(errors, command.Fields, cancellationToken);

        if (errors.Count > 0)
        {
            return new ContentWriteResult.Invalid(errors);
        }

        var guide = new SizeGuide();
        values.ApplyTo(guide);
        store.Add(guide);

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.Create,
            EntityType,
            "new",
            before: null,
            Describe(Snapshot(guide)),
            command.Actor,
            clock.UtcNow.UtcDateTime));

        var outcome = await store.SaveAsync(command.Actor.UserId, Usage(guide), cancellationToken);

        return Saved(outcome, guide, values);
    }

    public async Task<ContentWriteResult> HandleAsync(UpdateSizeGuideCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var guide = await store.FindAsync(command.SizeGuideId, cancellationToken);
        if (guide is null)
        {
            return NotFound(command.SizeGuideId);
        }

        if (ContentWriteSupport.CheckRowVersion(guide.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var errors = new List<ValidationError>();
        var values = await ReadAsync(errors, command.Fields, cancellationToken);

        if (errors.Count > 0)
        {
            return new ContentWriteResult.Invalid(errors);
        }

        var before = Snapshot(guide);
        values.ApplyTo(guide);

        var audit = ContentAuditTrail.ForChange(
            AuditActions.Update,
            EntityType,
            Id(guide.Id),
            before,
            Snapshot(guide),
            command.Actor,
            clock.UtcNow.UtcDateTime,
            RedactedFields,
            DescribeField);

        if (audit is null)
        {
            return values.Intro.Removed.Count > 0 || values.Tips.Removed.Count > 0
                ? new ContentWriteResult.Succeeded(guide.Id, values.Intro.Removed, RowVersionOf(guide), values.Tips.Removed)
                : new ContentWriteResult.Unchanged(guide.Id);
        }

        auditLog.Enlist(audit);
        var outcome = await store.SaveAsync(command.Actor.UserId, Usage(guide), cancellationToken);

        return Saved(outcome, guide, values);
    }

    public async Task<ContentWriteResult> HandleAsync(DeleteSizeGuideCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var guide = await store.FindAsync(command.SizeGuideId, cancellationToken);
        if (guide is null)
        {
            return NotFound(command.SizeGuideId);
        }

        if (ContentWriteSupport.CheckRowVersion(guide.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var (categories, products) = await store.UsageAsync(guide.Id, cancellationToken);
        if (categories > 0 || products > 0)
        {
            return new ContentWriteResult.Conflict(
                ContentConflictCodes.SizeGuideInUse,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"This size guide is used by {categories} categor{(categories == 1 ? "y" : "ies")} and {products} "
                    + $"product{(products == 1 ? string.Empty : "s")}. Choose another guide for them first."),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["categoryCount"] = categories,
                    ["productCount"] = products,
                });
        }

        var now = clock.UtcNow.UtcDateTime;
        guide.SoftDelete(now, command.Actor.UserId);

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.SoftDelete,
            EntityType,
            Id(guide.Id),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = guide.Name },
            after: null,
            command.Actor,
            now));

        // The usage rows go with it (doc 08 §3.3): a deleted guide must not hold a photograph's delete
        // guard shut for ever.
        var outcome = await store.SaveAsync(
            command.Actor.UserId,
            new ContentMediaUsageChange(MediaUsageOwnerTypes.SizeGuide, () => guide.Id, []),
            cancellationToken);

        if (outcome == ContentSaveOutcome.ConcurrencyConflict)
        {
            return StaleConflict();
        }

        homePageCache.Evict();
        return new ContentWriteResult.Succeeded(guide.Id);
    }

    private async Task<Values> ReadAsync(List<ValidationError> errors, SizeGuideFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var name = ContentFieldRules.RequiredText(errors, "name", fields.Name, NameMaxLength);

        var table = string.Empty;
        if (!SizeGuideTableInput.TryRead(fields.TableJson, out table, out var tableError))
        {
            errors.Add(new ValidationError("tableJson", tableError!));
        }

        var intro = Clean(errors, "introHtml", fields.IntroHtml);
        var tips = Clean(errors, "measuringTipsHtml", fields.MeasuringTipsHtml);

        var imageAssetId = await ContentMediaRules.ResolveAsync(
            errors, store, "imagePublicId", fields.ImagePublicId, MediaAssetType.Image, cancellationToken);

        return new Values(name ?? string.Empty, intro, table, tips, imageAssetId);
    }

    /// <summary>
    /// Cleans one rich-text field with the allow-list. A refusal (too long or too deeply nested to parse
    /// safely) is a message on the field, and the length is bounded <b>after</b> cleaning, because the
    /// cleaned value is what is stored and served.
    /// </summary>
    private SanitisedHtml Clean(List<ValidationError> errors, string field, string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return SanitisedHtml.Empty;
        }

        var cleaned = sanitiser.Sanitise(html);

        if (cleaned.WasRejected)
        {
            errors.Add(new ValidationError(field, cleaned.Rejection!));
            return SanitisedHtml.Empty;
        }

        if (cleaned.Html.Length > MaxHtmlLength)
        {
            errors.Add(new ValidationError(
                field,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Keep this to {MaxHtmlLength:N0} characters of formatted text. It is {cleaned.Html.Length:N0}.")));
            return SanitisedHtml.Empty;
        }

        if (cleaned.ImagesMissingAltText > 0)
        {
            errors.Add(new ValidationError(field, "Every image here needs alt text (GLB-12)."));
        }

        return cleaned;
    }

    private ContentWriteResult Saved(ContentSaveOutcome outcome, SizeGuide guide, Values values)
    {
        if (outcome == ContentSaveOutcome.ConcurrencyConflict)
        {
            return StaleConflict();
        }

        homePageCache.Evict();

        return new ContentWriteResult.Succeeded(guide.Id, values.Intro.Removed, RowVersionOf(guide), values.Tips.Removed);
    }

    private static ContentMediaUsageChange Usage(SizeGuide guide) =>
        new(
            MediaUsageOwnerTypes.SizeGuide,
            () => guide.Id,
            guide.ImageAssetId is { } image ? [new ContentMediaUsage(image, MediaUsageRoles.Image)] : []);

    private static Dictionary<string, object?> Snapshot(SizeGuide guide) => new(StringComparer.Ordinal)
    {
        ["name"] = guide.Name,
        ["introHtml"] = guide.IntroHtml,
        ["tableJson"] = guide.TableJson,
        ["measuringTipsHtml"] = guide.MeasuringTipsHtml,
        ["imageAssetId"] = guide.ImageAssetId,
    };

    /// <summary>A create's snapshot with the bulky fields described rather than copied.</summary>
    private static Dictionary<string, object?> Describe(Dictionary<string, object?> snapshot)
    {
        foreach (var field in RedactedFields)
        {
            snapshot[field] = DescribeField(field, snapshot[field]);
        }

        return snapshot;
    }

    /// <summary>How a redacted field reads in the trail: its size, never its content.</summary>
    private static object? DescribeField(string field, object? value) =>
        string.Equals(field, "tableJson", StringComparison.Ordinal)
            ? DescribeTable(value as string)
            : ContentAuditTrail.DescribeBody(value as string);

    private static string DescribeTable(string? tableJson) =>
        SizeGuideTable.Parse(tableJson) is { } table
            ? string.Create(CultureInfo.InvariantCulture, $"{table.Columns.Count} columns × {table.Rows.Count} rows")
            : "no readable table";

    private static string? RowVersionOf(SizeGuide guide) =>
        guide.RowVersion is { } version ? Convert.ToBase64String(version) : null;

    private static ContentWriteResult.NotFound NotFound(int id) =>
        new(string.Create(CultureInfo.InvariantCulture, $"No size guide has the id {id}."));

    private static ContentWriteResult.Conflict StaleConflict() =>
        new(
            ContentConflictCodes.StaleRowVersion,
            "Somebody else changed this size guide while you were editing it. Reload to see their change.");

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    private sealed record Values(
        string Name,
        SanitisedHtml Intro,
        string TableJson,
        SanitisedHtml Tips,
        int? ImageAssetId)
    {
        public void ApplyTo(SizeGuide guide)
        {
            guide.Name = Name;
            guide.IntroHtml = Intro.Html.Length == 0 ? null : Intro.Html;
            guide.TableJson = TableJson;
            guide.MeasuringTipsHtml = Tips.Html.Length == 0 ? null : Tips.Html;
            guide.ImageAssetId = ImageAssetId;
        }
    }
}
