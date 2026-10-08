using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Sales.Orders.Admin;
using SpencerDenim.Application.Sales.Orders.Contracts;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The admin orders screen (ADM-09, doc 11 P9): the list, one order, the fulfilment transitions, the
/// invoice and the couriers.
/// <para>
/// <b>Three permissions, and the split is doc 11 P9's own table.</b> Reading the list, an order and an
/// invoice needs <c>Orders.View</c>; the transitions and the internal note need <c>Orders.Edit</c>; adding
/// and editing couriers needs <c>Settings.Edit</c>. Every one is enforced by the endpoint's policy against
/// the user's <i>effective</i> permissions — never against claims on the cookie (doc 08 §3.1 R4) — so a
/// staff member whose role changed five minutes ago cannot ship an order out of a cookie issued before the
/// change.
/// </para>
/// <para>
/// <b>Approving a bank transfer is not here.</b> It lives in <c>AdminBankTransferEndpoints</c> behind
/// <c>Orders.Approve</c>, and this screen links to it rather than duplicating it — including
/// <c>accept-late</c>, which issue #124 is about: the order detail is the route to an expired order that a
/// reload cannot destroy, and the route it links to is the one already built and tested.
/// </para>
/// <para>
/// <b>What is deliberately absent.</b> No refund, no cancel: doc 11 P10's refund paths need
/// <c>Orders.Refund</c> and a <c>sales.Refunds</c> writer, and G-13's "money received on an order that was
/// never paid" is still owed (W3-03's journal, item 1). The detail screen <i>reads</i> refunds so an
/// existing one is visible, and offers no button that would write one.
/// </para>
/// <para>
/// <b>Antiforgery is validated by hand</b> on every mutating route, as every other admin endpoint file in
/// this project does, so a refusal is a problem document a client can read rather than an empty 400.
/// </para>
/// </summary>
internal static class AdminOrderEndpoints
{
    public static IEndpointRouteBuilder MapAdminOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/orders").WithTags("Admin Orders");

        api.MapGet("/", GetListAsync)
            .RequirePermission(Permissions.Orders.View)
            .WithName("AdminGetOrders")
            .WithSummary("The order list, searchable and filterable (doc 11 P9 step 1).")
            .WithDescription(
                "Search matches the order number first, because that is what a customer quotes on the "
                + "telephone; an invoice number, an email address, a telephone number and a customer's "
                + "name are also matched. Filter by status with ?status=Paid. The Paid filter defaults to "
                + "oldest first, which is doc 11 P9 step 1's own instruction; ?sort=newest overrides it, "
                + "and the answer says which order was used.")
            .Produces<AdminOrderListPage>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        api.MapGet("/{orderPublicId:guid}", GetOneAsync)
            .RequirePermission(Permissions.Orders.View)
            .WithName("AdminGetOrder")
            .WithSummary("One order in full (doc 11 P9).")
            .WithDescription(
                "Everything on it is a placement-time snapshot: the product names, colours, sizes and "
                + "prices are the ones the customer bought under, not today's catalogue. The only live "
                + "values are the courier list and the tracking URL template, which doc 08 §3.5 renders "
                + "from the courier's current template on purpose. `actions` says which transitions this "
                + "order can take, and `actions.blockedReason` says what to do when it cannot.")
            .Produces<AdminOrderDetailDto>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        api.MapGet("/{orderPublicId:guid}/invoice", GetInvoiceAsync)
            .RequirePermission(Permissions.Orders.View)
            .WithName("AdminGetOrderInvoice")
            .WithSummary("One order's invoice and packing copy (doc 11 P9 step 3).")
            .WithDescription(
                "Available once the order is paid, because that is when the gapless invoice number is "
                + "issued; an unpaid order answers 404, the same answer as an order that does not exist. "
                + "The seller's legal details come from the Store settings and **print nothing when they "
                + "are unset** — an invoice is a legal document and an invented address on one is worse "
                + "than a blank. There is no VAT line: prices already include VAT (doc 08 D2).")
            .Produces<OrderInvoiceDto>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        MapTransition(
            api,
            "start-processing",
            OrderTransitionKind.StartProcessing,
            "Start processing a paid order (doc 11 P9 step 2).",
            "Paid becomes Processing. Print the invoice or packing copy, then pick and pack. There is no "
            + "separate Packed status: packing happens inside Processing (use the internal note if you "
            + "need to record where it got to).");

        MapTransition(
            api,
            "mark-shipped",
            OrderTransitionKind.MarkShipped,
            "Mark a delivery order as shipped (doc 11 P9 steps 4 and 5).",
            "Needs a courier and a tracking number: doc 08 §4.1 makes both mandatory, and without them "
            + "the customer has no way to follow the parcel. One dispatch per order — there is no split "
            + "shipment in v1 (doc 11 §7 G-38), so this ships all of it. No email is sent: ops.EmailOutbox "
            + "is W3-06 and nothing claims otherwise; the customer's own order page shows the status "
            + "timeline.");

        MapTransition(
            api,
            "ready-for-collection",
            OrderTransitionKind.ReadyForCollection,
            "Mark a store pickup order ready for collection (doc 11 P9's pickup variant, step 2).",
            "The same Processing to Shipped transition, with no courier and no tracking number. The order "
            + "then reads as Ready for collection to staff and to the customer, and the auto-deliver job "
            + "will never touch it (doc 08 §4.1 G-18): an order nobody has collected has not been "
            + "delivered.");

        MapTransition(
            api,
            "mark-delivered",
            OrderTransitionKind.MarkDelivered,
            "Mark an order delivered, or collected (doc 11 P9 step 8).",
            "Shipped becomes Delivered, which is what lets the customer write a Verified buyer review. For "
            + "a store pickup order this is Mark as collected, and it is the only way such an order ever "
            + "reaches Delivered.");

        MapTransition(
            api,
            "dispatch",
            OrderTransitionKind.UpdateDispatch,
            "Correct the courier or the tracking number on an order that has shipped (doc 11 P9).",
            "The status does not move: orders never move backwards, so a mistake is corrected rather than "
            + "reversed. **No new notice is sent to the customer**, so they are still holding a tracking "
            + "link that does not work — doc 11 P9 says to telephone or WhatsApp them the same day and "
            + "record it in the internal note.");

        MapTransition(
            api,
            "internal-note",
            OrderTransitionKind.SetInternalNote,
            "Replace the staff-only internal note.",
            "Doc 08 §4.1's correction mechanism, since orders never move backwards. Send internalNote as "
            + "a string to set it, or null to clear it; **leaving the field out changes nothing**, which is "
            + "the distinction four defects in this project came from getting wrong. Writable in every "
            + "status, including Expired and Refunded, which are the ones that most need explaining.");

        var couriers = endpoints.MapGroup("/api/admin/couriers").WithTags("Admin Orders");

        couriers.MapGet("/", GetCouriersAsync)
            .RequirePermission(Permissions.Orders.View)
            .WithName("AdminGetCouriers")
            .WithSummary("Every courier, active first.")
            .WithDescription(
                "**Nothing is seeded, deliberately.** Which carriers the shop uses, and what their "
                + "tracking addresses are, is the client's information, so a new installation has an empty "
                + "list and cannot mark a delivery order as shipped until one is added. Reading needs "
                + "Orders.View, because the dispatch form needs the list; adding and editing needs "
                + "Settings.Edit (doc 11 P9's permission table).")
            .Produces<IReadOnlyList<AdminCourierDto>>()
            .Produces(StatusCodes.Status403Forbidden);

        couriers.MapPost("/", CreateCourierAsync)
            .RequirePermission(Permissions.Settings.Edit)
            .WithName("AdminCreateCourier")
            .WithSummary("Add a courier.")
            .WithDescription(
                "The tracking address must contain {trackingNumber} where the number goes, and must be "
                + "https. Leave it empty for a courier with no online tracking and the number is shown as "
                + "text. Names are unique.")
            .Produces<AdminCourierDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        couriers.MapPut("/{courierId:int}", UpdateCourierAsync)
            .RequirePermission(Permissions.Settings.Edit)
            .WithName("AdminUpdateCourier")
            .WithSummary("Edit a courier, or deactivate it.")
            .WithDescription(
                "Every field is replaced, so send them all. Set isActive to false to take a courier out of "
                + "the dispatch list: deactivating keeps every tracking link on orders already shipped "
                + "with it working, which deleting the row would not — so there is no delete.")
            .Produces<AdminCourierDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    /// <summary>
    /// Maps one transition route.
    /// </summary>
    /// <remarks>
    /// <b>The act is a URL segment and never a field in the body</b>, the same rule
    /// <c>AdminBankTransferEndpoints</c> follows and for the same reason: it decides which
    /// permission-bearing action runs, and a client that mistyped one value for another in a body would
    /// ship an order instead of noting it.
    /// </remarks>
    private static void MapTransition(
        RouteGroupBuilder api,
        string segment,
        OrderTransitionKind kind,
        string summary,
        string description) =>
        api.MapPost(
                $"/{{orderPublicId:guid}}/{segment}",
                (HttpContext httpContext,
                    IAntiforgery antiforgery,
                    ICommandHandler<TransitionOrderCommand, TransitionOrderResult> handler,
                    Guid orderPublicId,
                    CancellationToken cancellationToken) =>
                    TransitionAsync(
                        httpContext,
                        antiforgery,
                        handler,
                        orderPublicId,
                        kind,
                        cancellationToken))
            .RequirePermission(Permissions.Orders.Edit)
            .WithName($"AdminOrder{kind}")
            .WithSummary(summary)
            .WithDescription(description)
            .Produces<OrderTransitionDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> GetListAsync(
        HttpContext httpContext,
        IQueryHandler<GetAdminOrderListQuery, AdminOrderListPage> handler,
        string? search,
        string? status,
        string? sort,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        // A staff work list of named customers, their addresses and their money. Never cached anywhere, and
        // keyed on the cookie if any intermediary ignores that.
        NoStore(httpContext.Response);

        if (ReadStatus(status, out var parsedStatus) is { } badStatus)
        {
            return badStatus;
        }

        if (ReadSort(sort, out var parsedSort) is { } badSort)
        {
            return badSort;
        }

        return TypedResults.Ok(
            await handler.HandleAsync(
                new GetAdminOrderListQuery(search, parsedStatus, parsedSort, page, pageSize),
                cancellationToken));
    }

    private static async Task<IResult> GetOneAsync(
        HttpContext httpContext,
        IQueryHandler<GetAdminOrderQuery, AdminOrderDetailDto?> handler,
        Guid orderPublicId,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        return await handler.HandleAsync(new GetAdminOrderQuery(orderPublicId), cancellationToken)
            is { } order
            ? TypedResults.Ok(order)
            : TypedResults.NotFound();
    }

    private static async Task<IResult> GetInvoiceAsync(
        HttpContext httpContext,
        IQueryHandler<GetOrderInvoiceQuery, OrderInvoiceDto?> handler,
        Guid orderPublicId,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        return await handler.HandleAsync(new GetOrderInvoiceQuery(orderPublicId), cancellationToken)
            is { } invoice
            ? TypedResults.Ok(invoice)
            : TypedResults.NotFound();
    }

    private static async Task<IResult> GetCouriersAsync(
        HttpContext httpContext,
        IQueryHandler<ListCouriersQuery, IReadOnlyList<AdminCourierDto>> handler,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        return TypedResults.Ok(await handler.HandleAsync(new ListCouriersQuery(), cancellationToken));
    }

    /// <summary>
    /// The one path every transition takes: antiforgery, read the body, hand it to the use case, map the
    /// answer.
    /// </summary>
    private static async Task<IResult> TransitionAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<TransitionOrderCommand, TransitionOrderResult> handler,
        Guid orderPublicId,
        OrderTransitionKind kind,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        // A JsonObject read by hand rather than a bound record, the pattern AdminSettingsEndpoints
        // established: `{"trackingNumber": 123}` then names the field instead of answering "malformed
        // JSON" (issue #47's family), and — the part that matters here — **an absent field is told apart
        // from a null one**. A save that did not mention a field has silently cleared it four times in this
        // project, so the internal-note route refuses to touch the column unless the body carries the key.
        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(
            httpContext.Request,
            cancellationToken);

        if (problem is not null)
        {
            return problem;
        }

        if (ReadBody(body!, kind, out var fields) is { } badBody)
        {
            return badBody;
        }

        var result = await handler.HandleAsync(
            new TransitionOrderCommand(
                orderPublicId,
                kind,
                fields.CourierId,
                fields.TrackingNumber,
                fields.Note,
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        NoStore(httpContext.Response);

        return ToHttp(result);
    }

    private static async Task<IResult> CreateCourierAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SaveCourierCommand, SaveCourierResult> saver,
        IQueryHandler<ListCouriersQuery, IReadOnlyList<AdminCourierDto>> lister,
        CancellationToken cancellationToken) =>
        await SaveCourierAsync(httpContext, antiforgery, saver, lister, null, cancellationToken);

    private static async Task<IResult> UpdateCourierAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SaveCourierCommand, SaveCourierResult> saver,
        IQueryHandler<ListCouriersQuery, IReadOnlyList<AdminCourierDto>> lister,
        int courierId,
        CancellationToken cancellationToken) =>
        await SaveCourierAsync(httpContext, antiforgery, saver, lister, courierId, cancellationToken);

    /// <summary>
    /// Creates or updates one courier, answering with the saved row.
    /// </summary>
    /// <remarks>
    /// <b>Every field is required in the body, including the ones a staff member leaves blank.</b> A
    /// courier has five editable fields on one small form, so a partial update buys nothing — and reading
    /// an absent field as null is the exact defect that cleared every category image (W1-12) and four of
    /// five collections in a mapper (W1-03). An absent field is therefore a 400 naming it, not a silent
    /// clear. Blank is still how a staff member says "no tracking address": that is a present field whose
    /// value is null or empty.
    /// </remarks>
    private static async Task<IResult> SaveCourierAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SaveCourierCommand, SaveCourierResult> saver,
        IQueryHandler<ListCouriersQuery, IReadOnlyList<AdminCourierDto>> lister,
        int? courierId,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(
            httpContext.Request,
            cancellationToken);

        if (problem is not null)
        {
            return problem;
        }

        var errors = new List<ValidationError>();

        var name = ReadOptionalString(body!, "name", errors, required: true);
        var template = ReadOptionalString(body!, "trackingUrlTemplate", errors, required: true);
        var phone = ReadOptionalString(body!, "phone", errors, required: true);
        var isActive = ReadBool(body!, "isActive", errors);
        var sortOrder = ReadSortOrder(body!, errors);

        if (errors.Count > 0)
        {
            return ValidationProblem(errors);
        }

        var result = await saver.HandleAsync(
            new SaveCourierCommand(
                courierId,
                name,
                template,
                phone,
                isActive!.Value,
                sortOrder!.Value,
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        NoStore(httpContext.Response);

        switch (result)
        {
            case SaveCourierResult.Invalid invalid:
                return ValidationProblem(invalid.Errors);

            case SaveCourierResult.NotFound:
                return TypedResults.NotFound();

            case SaveCourierResult.Applied applied:
                // The saved row read back, so the screen redraws from the server rather than from what it
                // sent — which is the only way `ordersShipped` and the active ordering can be right.
                var saved = (await lister.HandleAsync(new ListCouriersQuery(), cancellationToken))
                    .FirstOrDefault(courier => courier.Id == applied.CourierId);

                return courierId is null
                    ? TypedResults.Created($"/api/admin/couriers/{applied.CourierId}", saved)
                    : TypedResults.Ok(saved);

            default:
                throw new InvalidOperationException($"Unhandled courier result {result.GetType().Name}.");
        }
    }

    private static IResult ToHttp(TransitionOrderResult result) => result switch
    {
        TransitionOrderResult.Applied applied => TypedResults.Ok(
            new OrderTransitionDto(applied.OrderNumber, applied.Status, applied.StatusLabel)),

        // A bare 404 with no body, the rule GET /api/orders/{token} already follows.
        TransitionOrderResult.NotFound => TypedResults.NotFound(),

        TransitionOrderResult.Invalid invalid => ValidationProblem(invalid.Errors),

        // 409 and not 400: nothing is wrong with the request, the world moved. `code` and `status` are what
        // let the screen redraw the action that will work rather than printing a sentence and leaving the
        // staff member to work out what to do.
        TransitionOrderResult.Refused refused => TypedResults.Problem(
            title: "This order could not be moved",
            detail: refused.Message,
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = refused.Code,
                ["orderStatus"] = refused.Status,
                ["orderStatusLabel"] = refused.StatusLabel,
            }),

        _ => throw new InvalidOperationException($"Unhandled transition result {result.GetType().Name}."),
    };

    /// <summary>
    /// Reads the fields one transition needs, telling an absent field from a null one.
    /// </summary>
    /// <remarks>
    /// <b>The internal note is the field this method exists for.</b> Doc 08 §3.5 makes
    /// <c>InternalNote</c> nullable and clearing it is a legitimate act, so <c>null</c> has to mean
    /// "clear"; which leaves no way to say "do not touch it" except the key's absence. So a request to
    /// <c>/internal-note</c> that carries no <c>internalNote</c> key is a 400 rather than a silent clear —
    /// the mistake W1-12, W1-03 and GLB-10 each made in a different shape.
    /// </remarks>
    private static ValidationProblem? ReadBody(
        JsonObject body,
        OrderTransitionKind kind,
        out (int? CourierId, string? TrackingNumber, string? Note) fields)
    {
        fields = default;

        var errors = new List<ValidationError>();

        int? courierId = null;
        string? trackingNumber = null;
        string? note = null;

        if (kind is OrderTransitionKind.MarkShipped or OrderTransitionKind.UpdateDispatch)
        {
            courierId = ReadInt(body, "courierId", errors);
            trackingNumber = ReadOptionalString(body, "trackingNumber", errors, required: true);
        }

        if (kind == OrderTransitionKind.SetInternalNote)
        {
            if (!body.ContainsKey("internalNote"))
            {
                errors.Add(new ValidationError(
                    "internalNote",
                    "Send internalNote, as text to set the note or null to clear it. Leaving it out is "
                    + "refused rather than treated as clearing it."));
            }
            else
            {
                note = ReadOptionalString(body, "internalNote", errors, required: false);
            }
        }
        else
        {
            // Every other transition takes an optional staff note that joins the history row's sentence.
            // Absent and null are the same thing here, because there is nothing to clear.
            note = ReadOptionalString(body, "note", errors, required: false);
        }

        if (errors.Count > 0)
        {
            return ValidationProblem(errors);
        }

        fields = (courierId, trackingNumber, note);
        return null;
    }

    /// <summary>
    /// Reads a string field. <paramref name="required"/> means the <i>key</i> must be present, not that
    /// the value must be non-empty — the use case decides that and says so in words.
    /// </summary>
    private static string? ReadOptionalString(
        JsonObject body,
        string field,
        List<ValidationError> errors,
        bool required)
    {
        if (!body.TryGetPropertyValue(field, out var node))
        {
            if (required)
            {
                errors.Add(new ValidationError(field, $"Send {field}, as text or null."));
            }

            return null;
        }

        if (node is null)
        {
            return null;
        }

        if (node.GetValueKind() != JsonValueKind.String)
        {
            errors.Add(new ValidationError(field, $"Send {field} as text, or null."));
            return null;
        }

        return node.GetValue<string>();
    }

    private static int? ReadInt(JsonObject body, string field, List<ValidationError> errors)
    {
        if (!body.TryGetPropertyValue(field, out var node) || node is null)
        {
            errors.Add(new ValidationError(field, $"Send {field} as a whole number."));
            return null;
        }

        if (node.GetValueKind() != JsonValueKind.Number || !node.AsValue().TryGetValue<int>(out var value))
        {
            errors.Add(new ValidationError(field, $"Send {field} as a whole number."));
            return null;
        }

        return value;
    }

    private static bool? ReadBool(JsonObject body, string field, List<ValidationError> errors)
    {
        if (!body.TryGetPropertyValue(field, out var node)
            || node is null
            || node.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add(new ValidationError(field, $"Send {field} as true or false."));
            return null;
        }

        return node.GetValue<bool>();
    }

    /// <summary>
    /// Reads <c>sortOrder</c>, which is a <c>smallint</c>.
    /// </summary>
    /// <remarks>
    /// Range-checked here rather than left to the column, because a value outside <c>smallint</c> would
    /// otherwise reach SQL Server and come back as an overflow 500 instead of a sentence naming the field.
    /// </remarks>
    private static short? ReadSortOrder(JsonObject body, List<ValidationError> errors)
    {
        if (!body.TryGetPropertyValue("sortOrder", out var node) || node is null)
        {
            errors.Add(new ValidationError("sortOrder", "Send sortOrder as a whole number."));
            return null;
        }

        if (node.GetValueKind() != JsonValueKind.Number
            || !node.AsValue().TryGetValue<short>(out var value))
        {
            errors.Add(new ValidationError(
                "sortOrder",
                $"Send sortOrder as a whole number between {short.MinValue} and {short.MaxValue}."));
            return null;
        }

        return value;
    }

    /// <summary>
    /// Reads <c>?status=</c>, by name.
    /// </summary>
    /// <remarks>
    /// <b>By name and not by number, and an unknown name is a 400 rather than "all orders".</b> The
    /// numbers are the database's contract (doc 08 §1.4) and must not travel on a query string where a
    /// renumbering would silently change what a bookmark means; and a typo that quietly widened the filter
    /// to every order would be a staff member working the wrong queue with nothing on the page saying so.
    /// </remarks>
    private static ValidationProblem? ReadStatus(string? raw, out OrderStatus? status)
    {
        status = null;

        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Enum.TryParse(raw, ignoreCase: true, out OrderStatus parsed)
            || !Enum.IsDefined(parsed))
        {
            return ValidationProblem(
            [
                new ValidationError(
                    "status",
                    "Unknown status. Use one of "
                    + string.Join(", ", Enum.GetNames<OrderStatus>())
                    + ", or leave it out for every order."),
            ]);
        }

        status = parsed;
        return null;
    }

    private static ValidationProblem? ReadSort(string? raw, out AdminOrderSort? sort)
    {
        sort = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!Enum.TryParse(raw, ignoreCase: true, out AdminOrderSort parsed) || !Enum.IsDefined(parsed))
        {
            return ValidationProblem(
            [
                new ValidationError("sort", "Use sort=newest or sort=oldest, or leave it out."),
            ]);
        }

        sort = parsed;
        return null;
    }

    private static ValidationProblem ValidationProblem(IReadOnlyList<ValidationError> errors) =>
        TypedResults.ValidationProblem(
            errors
                .GroupBy(error => error.Field, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Message).Distinct(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal),
            title: "One or more fields are invalid.");

    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, private";
        response.Headers.Vary = "Cookie";
    }
}

/// <summary>What a transition did, for the staff screen's message and its redraw.</summary>
/// <param name="OrderNumber">The order that moved.</param>
/// <param name="Status">Where it is now, by name.</param>
/// <param name="StatusLabel">
/// What to show, which differs for a pickup order: <c>Shipped</c> reads as "Ready for collection" and
/// <c>Delivered</c> as "Collected" (doc 11 P9's pickup variant).
/// </param>
public sealed record OrderTransitionDto(string OrderNumber, string Status, string StatusLabel);
