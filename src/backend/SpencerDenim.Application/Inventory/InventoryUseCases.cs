using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Reporting.Contracts;
using SpencerDenim.Application.Inventory.Abstractions;
using SpencerDenim.Application.Inventory.Contracts;
using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.Application.Inventory;

// ---- queries --------------------------------------------------------------------------------------------

/// <summary>The inventory list (<c>Inventory.View</c>). Page and page size are clamped, never refused.</summary>
public sealed record GetInventoryListQuery(
    string? Search,
    InventoryStockFilter Status,
    int? DepartmentId,
    int? CategoryId,
    InventorySort Sort,
    InventorySortDirection Direction,
    int? Page,
    int? PageSize);

/// <summary>The department and category choices for the list's filters (<c>Inventory.View</c>).</summary>
public sealed record GetInventoryFilterOptionsQuery;

/// <summary>One variant's movement history (<c>Inventory.View</c>).</summary>
public sealed record GetInventoryHistoryQuery(int VariantId, int? Page, int? PageSize);

public sealed class InventoryQueryHandlers(IInventoryReader reader)
    : IQueryHandler<GetInventoryListQuery, InventoryListPage>,
        IQueryHandler<GetInventoryFilterOptionsQuery, InventoryFilterOptions>,
        IQueryHandler<GetInventoryHistoryQuery, InventoryHistoryPage?>
{
    public Task<InventoryListPage> HandleAsync(GetInventoryListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = Clamp(query.Page, query.PageSize);

        return reader.ListAsync(
            new InventoryListCriteria(
                query.Search,
                query.Status,
                query.DepartmentId,
                query.CategoryId,
                query.Sort,
                query.Direction,
                page,
                pageSize),
            cancellationToken);
    }

    public Task<InventoryFilterOptions> HandleAsync(
        GetInventoryFilterOptionsQuery query,
        CancellationToken cancellationToken) =>
        reader.FilterOptionsAsync(cancellationToken);

    public Task<InventoryHistoryPage?> HandleAsync(GetInventoryHistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = Clamp(query.Page, query.PageSize);

        return reader.HistoryAsync(query.VariantId, page, pageSize, cancellationToken);
    }

    internal static (int Page, int PageSize) Clamp(int? page, int? pageSize) =>
        (Math.Clamp(page ?? 1, 1, InventoryListCriteria.MaxPage),
            Math.Clamp(pageSize ?? InventoryListCriteria.DefaultPageSize, 1, InventoryListCriteria.MaxPageSize));
}

// ---- commands -------------------------------------------------------------------------------------------

/// <summary>One requested change as it arrived: every field may be missing, and the use case says which.</summary>
public sealed record StockChangeInput(int? VariantId, StockChangeKind? Kind, int? Quantity, int? ExpectedOnHand);

/// <summary>
/// One or more stock changes with one reason (<c>Inventory.Edit</c>). A single inline edit is a request of
/// one row; a bulk update is up to <see cref="StockChangeRules.MaxBulkRows"/>.
/// </summary>
public sealed record ApplyStockChangesCommand(IReadOnlyList<StockChangeInput>? Rows, string? Reason, AdminActor Actor);

/// <summary>Set or clear one variant's low-stock threshold (<c>Inventory.Edit</c>). Null means the shop default.</summary>
public sealed record SetLowStockThresholdCommand(int VariantId, int? Threshold, AdminActor Actor);

public abstract record InventoryWriteResult
{
    private InventoryWriteResult()
    {
    }

    public sealed record Applied(StockChangeResultDto Result) : InventoryWriteResult;

    public sealed record ThresholdSaved(InventoryRow Variant) : InventoryWriteResult;

    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : InventoryWriteResult;

    public sealed record NotFound(string Message) : InventoryWriteResult;
}

/// <summary>
/// The inventory screen's two writes (ADM-12, doc 11 P2 part A).
/// <para>
/// <b>Validation is all or nothing; application is per row.</b> A request that is malformed anywhere — a
/// missing field, a quantity out of range, a variant named twice, no reason — is refused whole with a 400
/// naming every bad field, and nothing is applied: it is a mistake in what was typed, and applying the
/// other rows would leave the staff member working out which half went in. A request that is well formed
/// is then applied row by row (doc 08 §5.8, doc 11 P2 step 7), because the one thing that can still stop a
/// row — an order placed since the screen loaded, holding the units being taken away — is nobody's typing
/// mistake and is no reason to refuse the delivery that arrived on the next line.
/// </para>
/// </summary>
public sealed class InventoryWriteHandlers(IInventoryStore store, IInventoryReader reader, IClock clock)
    : ICommandHandler<ApplyStockChangesCommand, InventoryWriteResult>,
        ICommandHandler<SetLowStockThresholdCommand, InventoryWriteResult>
{
    public async Task<InventoryWriteResult> HandleAsync(
        ApplyStockChangesCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        var requests = Validate(command.Rows, errors);

        var reasonProblem = StockChangeRules.ValidateReason(command.Reason, out var reason);
        if (reasonProblem is not null)
        {
            errors.Add(new ValidationError(reasonProblem.Field, reasonProblem.Message));
        }

        if (errors.Count > 0)
        {
            return new InventoryWriteResult.Invalid(errors);
        }

        var batch = new StockChangeBatch(
            requests,
            reason,
            requests.Count > 1 ? Guid.NewGuid() : null,
            command.Actor,
            clock.UtcNow.UtcDateTime);

        return new InventoryWriteResult.Applied(await store.ApplyAsync(batch, cancellationToken));
    }

    public async Task<InventoryWriteResult> HandleAsync(
        SetLowStockThresholdCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (StockChangeRules.ValidateThreshold(command.Threshold) is { } problem)
        {
            return new InventoryWriteResult.Invalid([new ValidationError(problem.Field, problem.Message)]);
        }

        var outcome = await store.SetThresholdAsync(
            command.VariantId,
            command.Threshold,
            command.Actor,
            clock.UtcNow.UtcDateTime,
            cancellationToken);

        if (outcome == ThresholdChangeOutcome.NotFound
            || await reader.FindAsync(command.VariantId, cancellationToken) is not { } row)
        {
            return NotFound(command.VariantId);
        }

        return new InventoryWriteResult.ThresholdSaved(row);
    }

    /// <summary>Every row's shape, collecting every refusal rather than stopping at the first.</summary>
    internal static List<StockChangeRequest> Validate(IReadOnlyList<StockChangeInput>? rows, List<ValidationError> errors)
    {
        var requests = new List<StockChangeRequest>();

        if (rows is null || rows.Count == 0)
        {
            errors.Add(new ValidationError("rows", "Send at least one change."));
            return requests;
        }

        if (rows.Count > StockChangeRules.MaxBulkRows)
        {
            errors.Add(new ValidationError(
                "rows",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Send at most {StockChangeRules.MaxBulkRows} changes at a time.")));
            return requests;
        }

        var seen = new HashSet<int>();

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"rows[{index}].");
            var before = errors.Count;

            if (row.VariantId is not { } variantId || variantId <= 0)
            {
                errors.Add(new ValidationError(prefix + "variantId", "Say which variant to change."));
            }
            else if (!seen.Add(variantId))
            {
                // Two changes to one variant in one request: which one wins would depend on the order they were
                // applied, and a SetCount after an Adjust would silently discard it. Refused, not merged.
                errors.Add(new ValidationError(prefix + "variantId", "This variant is in the list twice; change it once."));
            }

            if (row.Kind is not { } kind)
            {
                errors.Add(new ValidationError(prefix + "kind", "Use kind Receive, Adjust or SetCount."));
            }
            else if (row.Quantity is not { } quantity)
            {
                errors.Add(new ValidationError(prefix + "quantity", "Enter a quantity."));
            }
            else
            {
                errors.AddRange(StockChangeRules.Validate(kind, quantity, row.ExpectedOnHand, prefix)
                    .Select(problem => new ValidationError(problem.Field, problem.Message)));
            }

            if (errors.Count == before)
            {
                requests.Add(new StockChangeRequest(row.VariantId!.Value, row.Kind!.Value, row.Quantity!.Value, row.ExpectedOnHand));
            }
        }

        return requests;
    }

    private static InventoryWriteResult.NotFound NotFound(int variantId) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"Variant {variantId} does not exist, or has no stock record. Reload the page."));
}
