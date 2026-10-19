using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Admin.Reporting.Contracts;
using SpencerDenim.Application.Inventory;
using SpencerDenim.Application.Inventory.Contracts;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The inventory screen (ADM-12, W1-05, issue #190; doc 11 P2 part A).
/// <para>
/// <b>Permissions are doc 11 P2's own table:</b> the list and a variant's movement history need
/// <c>Inventory.View</c>, which Order Staff hold (doc 02's role table: "Inventory (view)"); receiving,
/// adjusting, setting a count and changing a threshold need <c>Inventory.Edit</c>, which Store Manager and
/// Super Admin hold. Until this file, <c>Inventory.View</c> was granted and used by nothing.
/// </para>
/// <para>
/// <b>The inventory <i>report</i> keeps <c>Reports.View</c></b> (<c>AdminReportingEndpoints</c>), and is not
/// widened to accept <c>Inventory.View</c> as well: doc 11 P16 says Order Staff see the dashboard and not
/// Reports, and the stock figures they need are on this screen. Recorded in the #190 journal as an owner
/// decision.
/// </para>
/// </summary>
internal static class AdminInventoryEndpoints
{
    public static IEndpointRouteBuilder MapAdminInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var inventory = endpoints.MapGroup("/api/admin/inventory").WithTags("Admin Inventory");

        inventory.MapGet("/", GetListAsync)
            .RequirePermission(Permissions.Inventory.View)
            .WithName("AdminListInventory")
            .WithSummary("Stock per active variant, live, with in-stock / low / out counts (ADM-12).")
            .WithDescription(
                "?search= matches part of a product name or the start of a SKU or product code; "
                + "?status=All, InStock, LowStock or OutOfStock; ?departmentId= and ?categoryId= (the category "
                + "and those below it, by the product's main category); ?sort=Product, Sku, OnHand, Reserved or "
                + "Available and ?direction=Asc or Desc. Paged, 25 by default, at most 100. Low is 1 to the "
                + "variant's own threshold or the shop default; out of stock is never low. The counts ignore "
                + "the status filter.")
            .Produces<InventoryListPage>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        inventory.MapGet("/filters", GetFiltersAsync)
            .RequirePermission(Permissions.Inventory.View)
            .WithName("AdminInventoryFilters")
            .WithSummary("The departments and categories the list can be filtered by, and the shop default threshold.")
            .Produces<InventoryFilterOptions>()
            .Produces(StatusCodes.Status403Forbidden);

        inventory.MapGet("/{variantId:int}/movements", GetHistoryAsync)
            .RequirePermission(Permissions.Inventory.View)
            .WithName("AdminInventoryHistory")
            .WithSummary("One variant's stock movements, newest first, with its stock now (doc 11 P2 step 4).")
            .Produces<InventoryHistoryPage>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden);

        inventory.MapPost("/adjustments", ApplyAsync)
            .RequirePermission(Permissions.Inventory.Edit)
            .WithName("AdminAdjustStock")
            .WithSummary("Receive, adjust or set the count of one or more variants, with one reason (doc 11 P2).")
            .WithDescription(
                "Body: { reason, rows: [ { variantId, kind, quantity, expectedOnHand } ] }, every key sent. "
                + "kind Receive adds quantity (1 to 100,000); Adjust adds a signed quantity (not 0, at most "
                + "100,000 either way); SetCount sets on hand to quantity (0 to 1,000,000) only while it still "
                + "equals expectedOnHand. expectedOnHand is null except for SetCount. reason is required, up "
                + "to 250 characters. At most 500 rows, each variant once. A malformed request is refused "
                + "whole (400) and changes nothing. A well-formed one is applied row by row in one "
                + "transaction: each row is Applied, Refused (it would leave on hand below what unpaid orders "
                + "hold, or above 1,000,000), Changed (a SetCount whose figure moved; re-confirm) or NotFound. "
                + "Every applied row writes an inventory movement and an audit row; a request of more than "
                + "one row shares a batchId.")
            .Produces<StockChangeResultDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        inventory.MapPut("/{variantId:int}/threshold", SetThresholdAsync)
            .RequirePermission(Permissions.Inventory.Edit)
            .WithName("AdminSetLowStockThreshold")
            .WithSummary("Set a variant's low-stock threshold, or clear it to use the shop default.")
            .WithDescription(
                "Body: { lowStockThreshold }: a whole number from 0 to 10,000, or null for the shop default. "
                + "Audited when it changes. Answers the variant as it now is.")
            .Produces<InventoryRow>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> GetListAsync(
        HttpContext httpContext,
        IQueryHandler<GetInventoryListQuery, InventoryListPage> handler,
        string? search,
        string? status,
        int? departmentId,
        int? categoryId,
        string? sort,
        string? direction,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        var errors = new List<ValidationError>();
        var chosenStatus = ReadName(status, "status", InventoryStockFilter.All, errors);
        var chosenSort = ReadName(sort, "sort", InventorySort.Product, errors);
        var chosenDirection = ReadName(direction, "direction", InventorySortDirection.Asc, errors);

        if (errors.Count > 0)
        {
            return AdminJsonBody.Problem(errors);
        }

        return TypedResults.Ok(await handler.HandleAsync(
            new GetInventoryListQuery(search, chosenStatus, departmentId, categoryId, chosenSort, chosenDirection, page, pageSize),
            cancellationToken));
    }

    private static async Task<IResult> GetFiltersAsync(
        HttpContext httpContext,
        IQueryHandler<GetInventoryFilterOptionsQuery, InventoryFilterOptions> handler,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        return TypedResults.Ok(await handler.HandleAsync(new GetInventoryFilterOptionsQuery(), cancellationToken));
    }

    private static async Task<IResult> GetHistoryAsync(
        int variantId,
        HttpContext httpContext,
        IQueryHandler<GetInventoryHistoryQuery, InventoryHistoryPage?> handler,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        var history = await handler.HandleAsync(new GetInventoryHistoryQuery(variantId, page, pageSize), cancellationToken);

        return history is null
            ? NotFound(string.Create(CultureInfo.InvariantCulture, $"There is no variant {variantId}."))
            : TypedResults.Ok(history);
    }

    private static async Task<IResult> ApplyAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<ApplyStockChangesCommand, InventoryWriteResult> handler,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(httpContext.Request, cancellationToken);

        if (problem is not null)
        {
            return problem;
        }

        if (AdminJsonBody.ReadString(body!, "reason", out var reason) is { } badReason)
        {
            return badReason;
        }

        if (AdminJsonBody.ReadObjectArray(body!, "rows", out var rawRows) is { } badRows)
        {
            return badRows;
        }

        var errors = new List<ValidationError>();
        var rows = new List<StockChangeInput>(rawRows.Count);

        for (var index = 0; index < rawRows.Count; index++)
        {
            rows.Add(ReadRow(rawRows[index], string.Create(CultureInfo.InvariantCulture, $"rows[{index}]."), errors));
        }

        if (errors.Count > 0)
        {
            return AdminJsonBody.Problem(errors);
        }

        var result = await handler.HandleAsync(
            new ApplyStockChangesCommand(rows, reason, AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return result switch
        {
            InventoryWriteResult.Applied applied => TypedResults.Ok(applied.Result),
            InventoryWriteResult.Invalid invalid => AdminJsonBody.Problem(invalid.Errors),
            _ => throw new InvalidOperationException($"Unexpected stock change result '{result}'."),
        };
    }

    private static async Task<IResult> SetThresholdAsync(
        int variantId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SetLowStockThresholdCommand, InventoryWriteResult> handler,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(httpContext.Request, cancellationToken);

        if (problem is not null)
        {
            return problem;
        }

        // Present and null means "use the shop default"; absent is refused, so a client that forgot the
        // field cannot clear a threshold by accident (AdminJsonBody's presence rule).
        if (AdminJsonBody.ReadInt(body!, "lowStockThreshold", out var threshold) is { } badThreshold)
        {
            return badThreshold;
        }

        var result = await handler.HandleAsync(
            new SetLowStockThresholdCommand(variantId, threshold, AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return result switch
        {
            InventoryWriteResult.ThresholdSaved saved => TypedResults.Ok(saved.Variant),
            InventoryWriteResult.Invalid invalid => AdminJsonBody.Problem(invalid.Errors),
            InventoryWriteResult.NotFound missing => NotFound(missing.Message),
            _ => throw new InvalidOperationException($"Unexpected threshold result '{result}'."),
        };
    }

    /// <summary>
    /// One row's four keys, each required (present and null is allowed for <c>expectedOnHand</c>). Refusals are
    /// keyed <c>rows[i].field</c> so the screen can put each next to its row.
    /// </summary>
    private static StockChangeInput ReadRow(JsonObject row, string prefix, List<ValidationError> errors)
    {
        var variantId = ReadRowInt(row, "variantId", prefix, errors);
        var quantity = ReadRowInt(row, "quantity", prefix, errors);
        var expectedOnHand = ReadRowInt(row, "expectedOnHand", prefix, errors);
        StockChangeKind? kind = null;

        if (!row.TryGetPropertyValue("kind", out var node))
        {
            errors.Add(new ValidationError(prefix + "kind", "Send kind."));
        }
        else if (node is not null)
        {
            if (node is not JsonValue json
                || !json.TryGetValue<string>(out var text)
                || !TryExactName(text, out StockChangeKind parsed))
            {
                errors.Add(new ValidationError(prefix + "kind", "Use kind Receive, Adjust or SetCount."));
            }
            else
            {
                kind = parsed;
            }
        }

        return new StockChangeInput(variantId, kind, quantity, expectedOnHand);
    }

    private static int? ReadRowInt(JsonObject row, string field, string prefix, List<ValidationError> errors)
    {
        if (!row.TryGetPropertyValue(field, out var node))
        {
            errors.Add(new ValidationError(prefix + field, $"Send {field}."));
            return null;
        }

        if (node is null)
        {
            return null;
        }

        if (node is not JsonValue json || !json.TryGetValue<int>(out var number))
        {
            errors.Add(new ValidationError(prefix + field, $"Send {field} as a whole number between -2,147,483,648 and 2,147,483,647."));
            return null;
        }

        return number;
    }

    /// <summary>Reads an enum by name, refusing a number.</summary>
    private static TEnum ReadName<TEnum>(string? raw, string field, TEnum fallback, List<ValidationError> errors)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (TryExactName(raw, out TEnum parsed))
        {
            return parsed;
        }

        errors.Add(new ValidationError(field, $"Use {field}={string.Join(", ", Enum.GetNames<TEnum>())}."));
        return fallback;
    }

    /// <summary>
    /// One of the enum's own names, compared without case after trimming — and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>Enum.TryParse</c></b> (tester #190, D2): it also accepts a number ("2", "+2", " 2"), and a
    /// comma list that it ORs together as flags — "Receive, Adjust" is 1|2, which is SetCount. A name match
    /// against <c>Enum.GetNames</c> refuses all of those by construction.
    /// </remarks>
    internal static bool TryExactName<TEnum>(string? raw, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        var name = Enum.GetNames<TEnum>().FirstOrDefault(candidate => string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            return false;
        }

        value = Enum.Parse<TEnum>(name);
        return true;
    }

    private static ProblemHttpResult NotFound(string message) =>
        AdminWriteRequest.Problem(StatusCodes.Status404NotFound, "Not found", message, "not_found");

    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, private";
        response.Headers.Vary = "Cookie";
    }
}
