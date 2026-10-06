using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog.Admin.Products.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Application.Catalog.Admin.Products;

/// <summary>
/// The checks that need the database, shared by create and edit: the two natural keys on the product and
/// the two on its variants.
/// <para>
/// They are asked before the write and the unique indexes still stand behind them, which is the same pair
/// of guards <c>CatalogWriteSupport</c> keeps for a row version: the read-then-write gives the admin a
/// named field error, and the index catches the race between the check and the save as a 409 rather than a
/// 500 with a constraint name in it.
/// </para>
/// </summary>
internal static class ProductUniquenessRules
{
    public static async Task CheckAsync(
        ICollection<ValidationError> errors,
        IProductWriteStore store,
        ValidatedProductForm form,
        int? productId,
        CancellationToken cancellationToken)
    {
        if (await store.ProductSlugTakenAsync(form.Slug, productId, cancellationToken))
        {
            errors.Add(new ValidationError(
                "slug", $"Another product already uses the address /p/{form.Slug}. Choose a different slug."));
        }

        if (await store.ProductCodeTakenAsync(form.ProductCode, productId, cancellationToken))
        {
            errors.Add(new ValidationError(
                "productCode", $"Another product already uses the code '{form.ProductCode}'."));
        }

        // A request that sent no variants is saying nothing about them, so there is nothing of its own to
        // check for uniqueness — the rows already on the product keep the SKUs they have.
        if (form.Variants is null)
        {
            return;
        }

        var skus = form.Variants.Select(variant => variant.Sku).ToList();
        if (skus.Count > 0)
        {
            foreach (var taken in await store.SkusTakenAsync(skus, productId, cancellationToken))
            {
                var index = form.Variants
                    .Select((variant, position) => (variant, position))
                    .First(entry => string.Equals(entry.variant.Sku, taken, StringComparison.OrdinalIgnoreCase))
                    .position;

                errors.Add(new ValidationError(
                    string.Create(CultureInfo.InvariantCulture, $"variants[{index}].sku"),
                    $"SKU '{taken}' already belongs to another product's variant. A SKU is unique across every "
                    + "variant the shop has ever had, removed products included."));
            }
        }

        var barcodes = form.Variants
            .Select(variant => variant.Barcode)
            .Where(barcode => barcode is not null)
            .Select(barcode => barcode!)
            .ToList();

        if (barcodes.Count > 0)
        {
            foreach (var taken in await store.BarcodesTakenAsync(barcodes, productId, cancellationToken))
            {
                var index = form.Variants
                    .Select((variant, position) => (variant, position))
                    .First(entry => string.Equals(entry.variant.Barcode, taken, StringComparison.OrdinalIgnoreCase))
                    .position;

                errors.Add(new ValidationError(
                    string.Create(CultureInfo.InvariantCulture, $"variants[{index}].barcode"),
                    $"Barcode '{taken}' already belongs to another product's variant."));
            }
        }
    }
}

/// <summary>Creates a product (ADM-10, doc 11 P1 steps 6–13).</summary>
public sealed class CreateProductCommandHandler(
    IProductWriteStore store,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<CreateProductCommand, CatalogWriteResult>
{
    public async Task<CatalogWriteResult> HandleAsync(
        CreateProductCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var references = await store.LoadReferencesAsync(
            ProductWriteRules.ReferenceRequest(command.Form), cancellationToken);

        if (ProductWriteRules.Validate(command.Form, references, productId: null, out var form) is { } invalid)
        {
            return invalid;
        }

        var errors = new List<ValidationError>();
        await ProductUniquenessRules.CheckAsync(errors, store, form!, productId: null, cancellationToken);

        if (errors.Count > 0)
        {
            return new CatalogWriteResult.Invalid(errors);
        }

        var utcNow = clock.UtcNow.UtcDateTime;

        // Doc 11 P1 step 13: created unpublished, always. Step 14 makes publishing its own act, which is
        // also the only way the publish checklist can mean anything — a create that could publish would
        // have to pass a checklist about photographs the admin has not attached yet.
        var product = new Product { IsPublished = false };

        if (ProductAggregateWriter.Apply(product, form!, VariantDeleteGuards.None, utcNow, out _)
            is { } refusal)
        {
            return refusal;
        }

        var outcome = await store.AddProductAsync(
            product,

            // Inside the store's transaction, once every identity value exists, and again on each retry —
            // so the audit row names the id that was really inserted, and the usage and stock rows point at
            // real variants.
            async (inserted, token) =>
            {
                auditLog.Enlist(CatalogAuditTrail.Row(
                    AuditActions.Create,
                    ProductAuditTrail.ProductEntityType,
                    inserted.Id.ToString(CultureInfo.InvariantCulture),
                    before: null,
                    after: ProductWriteRules.Snapshot(inserted),
                    command.Actor,
                    utcNow));

                await store.SyncProductMediaUsagesAsync(
                    inserted.Id, ProductWriteRules.DesiredMediaUsages(inserted), token);

                await store.EnsureStockRowsAsync(inserted.Id, token);
            },
            command.Actor.UserId,
            cancellationToken);

        return CatalogWriteSupport.ToResult(outcome, product.Id);
    }
}

/// <summary>Edits a product (ADM-10).</summary>
public sealed class UpdateProductCommandHandler(
    IProductWriteStore store,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<UpdateProductCommand, CatalogWriteResult>
{
    public async Task<CatalogWriteResult> HandleAsync(
        UpdateProductCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var product = await store.FindProductAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            return new CatalogWriteResult.NotFound(
                string.Create(CultureInfo.InvariantCulture, $"No product has the id {command.ProductId}."));
        }

        if (CatalogWriteSupport.CheckRowVersion(product.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var references = await store.LoadReferencesAsync(
            ProductWriteRules.ReferenceRequest(command.Form), cancellationToken);

        if (ProductWriteRules.Validate(command.Form, references, product.Id, out var form) is { } invalid)
        {
            return invalid;
        }

        // Doc 11 P1's business rules: "Slug change after first publish shows a 'this breaks existing links'
        // confirmation; redirects are not in v1". Asked before anything is written and before the
        // uniqueness round trip, so a declined confirmation costs the server nothing.
        if (!string.Equals(form!.Slug, product.Slug, StringComparison.Ordinal)
            && product.FirstPublishedAtUtc is not null
            && !command.ConfirmSlugChange)
        {
            return new CatalogWriteResult.Conflict(
                CatalogConflictCodes.SlugChangeNeedsConfirmation,
                $"This product has been published at /p/{product.Slug}, so that address may be in a customer's "
                + $"bookmarks, in a search engine's index or in a message somebody sent. Changing it to "
                + $"/p/{form.Slug} will make the old address show 'page not found' — there are no redirects "
                + "in this version. Confirm to go ahead.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["currentSlug"] = product.Slug,
                    ["requestedSlug"] = form.Slug,
                });
        }

        var errors = new List<ValidationError>();
        await ProductUniquenessRules.CheckAsync(errors, store, form, product.Id, cancellationToken);

        if (errors.Count > 0)
        {
            return new CatalogWriteResult.Invalid(errors);
        }

        var before = ProductWriteRules.Snapshot(product);
        var utcNow = clock.UtcNow.UtcDateTime;

        // Whether the two derived side rows exist yet. A product created by this task always has both, but a
        // row restored from a backup or written by a support script may not — and in that case the save has
        // to go through even when no audited field moved, or the listing would keep pricing the product from
        // a ProductStats row that is not there.
        var hadDerivedRows = product.Stats is not null && product.SearchIndex is not null;

        var guards = await store.VariantDeleteGuardsAsync(product.Id, cancellationToken);

        if (ProductAggregateWriter.Apply(product, form, guards, utcNow, out var removedVariants)
            is { } refusal)
        {
            return refusal;
        }

        // Taking a variant out of the collection is not enough. ProductVariants -> Products is NO ACTION,
        // and so is StockLevels -> ProductVariants, so the delete has to be explicit and has to take the
        // stock row with it. See IProductWriteStore.RemoveVariantsAsync.
        // Doc 11 §7 G-01 again, and this is the half that was missing: the checklist ran only on the
        // publish *transition*, so a product already on the shop could be edited into a state it would
        // never have been allowed to reach. `media: []` on a published product answered 204 and left
        // isPublished = 1 with no photographs and a null card image, and the storefront then served that
        // product 200 with zero images. A shopper saw a live product with nothing to look at.
        //
        // Refused rather than auto-unpublished. Taking a product off the shop is its own deliberate act
        // with its own StatusChange audit row, and the whole design of SetProductPublishedCommand — and of
        // SetCategoryActiveCommand before it — rests on that never being a side effect of saving a form.
        // Auto-unpublishing here would also be silent: the admin would get a 204 and discover later that
        // their product had left the shop.
        if (product.IsPublished)
        {
            var liveBlockers = ProductPublishChecklist.Blockers(product);

            if (liveBlockers.Count > 0)
            {
                return new CatalogWriteResult.Conflict(
                    CatalogConflictCodes.PublishedProductWouldBreak,
                    "This product is on the shop, and this change would leave it in a state it is not "
                    + $"allowed to be on the shop in: {string.Join(" ", liveBlockers)} Take it off the shop "
                    + "first if that is what you meant, then make the change.",
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["blockers"] = liveBlockers });
            }
        }

        var cartLinesRemoved = await store.RemoveVariantsAsync(removedVariants, cancellationToken);

        var after = ProductWriteRules.Snapshot(product);

        // A removed size can take a line out of a shopper's bag (VariantReferences), and that is the one
        // effect of this save that nobody on the admin side can see afterwards. It belongs in the trail.
        if (cartLinesRemoved > 0)
        {
            after["cartLinesRemoved"] = cartLinesRemoved;
        }

        var audit = CatalogAuditTrail.ForChange(
            AuditActions.Update,
            ProductAuditTrail.ProductEntityType,
            product.Id.ToString(CultureInfo.InvariantCulture),
            before,
            after,
            command.Actor,
            utcNow);

        if (audit is null && hadDerivedRows)
        {
            // Nothing was different, so nothing is written: the row version does not move and no audit row
            // appears. A save of an untouched form is not an event (CatalogAuditTrail).
            return new CatalogWriteResult.Unchanged(product.Id);
        }

        if (audit is not null)
        {
            auditLog.Enlist(audit);
        }

        await store.SyncProductMediaUsagesAsync(
            product.Id, ProductWriteRules.DesiredMediaUsages(product), cancellationToken);

        return CatalogWriteSupport.ToResult(
            await store.SaveAsync(command.Actor.UserId, cancellationToken),
            product.Id);
    }
}

/// <summary>
/// Publishes or unpublishes a product (ADM-10, doc 11 P1 steps 14–15).
/// </summary>
public sealed class SetProductPublishedCommandHandler(
    IProductWriteStore store,
    IAuditLogWriter auditLog,
    IClock clock,
    CatalogOptions options)
    : ICommandHandler<SetProductPublishedCommand, CatalogWriteResult>
{
    public async Task<CatalogWriteResult> HandleAsync(
        SetProductPublishedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var product = await store.FindProductAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            return new CatalogWriteResult.NotFound(
                string.Create(CultureInfo.InvariantCulture, $"No product has the id {command.ProductId}."));
        }

        if (CatalogWriteSupport.CheckRowVersion(product.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        if (product.IsPublished == command.IsPublished)
        {
            return new CatalogWriteResult.Unchanged(product.Id);
        }

        if (command.IsPublished)
        {
            // Doc 11 §7 G-01. Checked here and shown on the form from the same function, so the button's
            // explanation and the API's refusal cannot disagree.
            var blockers = ProductPublishChecklist.Blockers(product);
            if (blockers.Count > 0)
            {
                return new CatalogWriteResult.Conflict(
                    CatalogConflictCodes.PublishChecklistFailed,
                    "This product is not ready to go on the shop yet: " + string.Join(" ", blockers),
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["blockers"] = blockers });
            }
        }

        var utcNow = clock.UtcNow.UtcDateTime;
        var firstPublishedBefore = product.FirstPublishedAtUtc;

        if (command.IsPublished)
        {
            product.Publish(utcNow, options.NewArrivalDays);
        }
        else
        {
            product.Unpublish();
        }

        var after = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["isPublished"] = command.IsPublished,
            ["name"] = product.Name,
            ["slug"] = product.Slug,
        };

        // Only when the publish actually set them, so an unpublish-republish cycle does not litter the trail
        // with the same two unchanged timestamps.
        if (product.FirstPublishedAtUtc != firstPublishedBefore)
        {
            after["firstPublishedAtUtc"] = product.FirstPublishedAtUtc;
            after["newUntilUtc"] = product.NewUntilUtc;
        }

        auditLog.Enlist(CatalogAuditTrail.Row(
            AuditActions.StatusChange,
            ProductAuditTrail.ProductEntityType,
            product.Id.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["isPublished"] = !command.IsPublished },
            after,
            command.Actor,
            utcNow));

        return CatalogWriteSupport.ToResult(
            await store.SaveAsync(command.Actor.UserId, cancellationToken),
            product.Id);
    }
}

/// <summary>Removes a product: a soft delete (ADM-10, doc 11 P1 step 15).</summary>
public sealed class RemoveProductCommandHandler(
    IProductWriteStore store,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<RemoveProductCommand, CatalogWriteResult>
{
    public async Task<CatalogWriteResult> HandleAsync(
        RemoveProductCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var product = await store.FindProductAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            return new CatalogWriteResult.NotFound(
                string.Create(CultureInfo.InvariantCulture, $"No product has the id {command.ProductId}."));
        }

        if (CatalogWriteSupport.CheckRowVersion(product.RowVersion, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var utcNow = clock.UtcNow.UtcDateTime;
        product.SoftDelete(utcNow, command.Actor.UserId);

        // Doc 11 P1: "Soft delete keeps variants, colours, media links and order lines". The media usage
        // rows are kept with them on purpose — the pictures are still attached to a product that could be
        // restored by a support query, and releasing them would let a staff member delete the photographs of
        // a product that is only hidden. Nothing is served from them, because the product itself is gone
        // from every storefront query.
        auditLog.Enlist(CatalogAuditTrail.Row(
            AuditActions.SoftDelete,
            ProductAuditTrail.ProductEntityType,
            product.Id.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["isDeleted"] = false },
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["isDeleted"] = true,
                ["name"] = product.Name,
                ["slug"] = product.Slug,
                ["productCode"] = product.ProductCode,
                ["wasPublished"] = product.IsPublished,
            },
            command.Actor,
            utcNow));

        return CatalogWriteSupport.ToResult(
            await store.SaveAsync(command.Actor.UserId, cancellationToken),
            product.Id);
    }
}

/// <summary>The <c>ops.AuditLogs.EntityType</c> values ADM-10 writes.</summary>
public static class ProductAuditTrail
{
    /// <summary>
    /// One value for the whole aggregate, not one per table.
    /// <para>
    /// A colour, a variant, a gallery row and a related pick are only ever changed as part of saving a
    /// product, and the entity filter in the audit viewer is how a shop owner asks "who changed this
    /// product?". Separate entity types would split one answer across five filters, and four of them would
    /// name ids no screen shows.
    /// </para>
    /// </summary>
    public const string ProductEntityType = "Product";
}
