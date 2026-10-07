using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Admin;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// "Orders → Bank transfers awaiting approval" and the three decisions on it (doc 11 P7 steps 4, 6 and 8;
/// PAY-01, ADM-09).
/// <para>
/// <b>Two permissions, and the split is doc 11 P7's own table.</b> Reading the queue and opening a slip
/// need <c>Orders.View</c>; approving, rejecting and accepting a late payment need <c>Orders.Approve</c>.
/// Both are enforced by the endpoint's policy against the user's <i>effective</i> permissions — never
/// against claims on the cookie (doc 08 §3.1 R4), so a staff member whose role was changed five minutes
/// ago cannot approve a transfer out of a cookie that was issued before the change.
/// </para>
/// <para>
/// <b>Deliberately narrow.</b> This is the queue doc 11 P7 needs and nothing more: no order list, no order
/// detail, no fulfilment, no cancel, no refund. The full admin orders screen is W3-04 and the refund paths
/// (including G-13's "money received on an order that was never paid") are its and ADM-09's, which is why
/// nothing here writes a <c>sales.Refunds</c> row.
/// </para>
/// <para>
/// <b>Antiforgery is validated by hand</b> on every mutating route, as every other admin endpoint file in
/// this project does, so a refusal is a problem document a client can read rather than an empty 400.
/// </para>
/// </summary>
internal static class AdminBankTransferEndpoints
{
    public static IEndpointRouteBuilder MapAdminBankTransferEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/orders/bank-transfers").WithTags("Admin Bank Transfers");

        api.MapGet("/", GetQueueAsync)
            .RequirePermission(Permissions.Orders.View)
            .WithName("AdminGetBankTransferQueue")
            .WithSummary("Bank transfers awaiting approval, oldest first (doc 11 P7 step 4).")
            .WithDescription(
                "Each row carries the order number (which is also the reference the customer was told to "
                + "quote), the customer, the exact amount, their reference, their slip if they uploaded "
                + "one, and secondsUntilExpiry — negative once the deadline has passed. The time left is "
                + "computed on the server, because the server owns 'now'.")
            .Produces<BankTransferQueuePage>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        api.MapPost("/{orderPublicId:guid}/approve", ApproveAsync)
            .RequirePermission(Permissions.Orders.Approve)
            .WithName("AdminApproveBankTransfer")
            .WithSummary("Approve a bank transfer (doc 11 P7 step 6a).")
            .WithDescription(
                "In one transaction: the order becomes Paid, the payment becomes Succeeded and is marked "
                + "as the settling payment with your name and the time, the reserved stock becomes sold, a "
                + "gapless invoice number is issued, and a history row is written. Approve only when the "
                + "full amount is on the statement: there is no part-payment handling (doc 11 §7 G-12).")
            .Produces<BankTransferDecisionDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/{orderPublicId:guid}/reject", RejectAsync)
            .RequirePermission(Permissions.Orders.Approve)
            .WithName("AdminRejectBankTransfer")
            .WithSummary("Reject a bank transfer, with a reason (doc 11 P7 step 6b).")
            .WithDescription(
                "The payment becomes Failed with your name, the time and your reason. The order stays "
                + "awaiting payment until its deadline, so the customer can transfer again or upload a new "
                + "slip — which creates a new payment record. A reason is required. No email is sent: "
                + "doc 11 §7 G-14 records that a rejection notice is not in v1.")
            .Produces<BankTransferDecisionDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/{orderPublicId:guid}/accept-late", AcceptLateAsync)
            .RequirePermission(Permissions.Orders.Approve)
            .WithName("AdminAcceptLateBankTransfer")
            .WithSummary("Accept money that arrived after the order expired (doc 11 P7 step 8).")
            .WithDescription(
                "Re-checks stock for every line. If everything is available the order becomes Paid with "
                + "today's date and a new invoice number; if any line is short the whole acceptance is "
                + "refused and the response names every short line. If the stock has gone, the money is "
                + "refunded instead — which is doc 11 §7 G-13 and belongs to the refund screen (W3-04).")
            .Produces<BankTransferDecisionDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> GetQueueAsync(
        HttpContext httpContext,
        IQueryHandler<GetBankTransferQueueQuery, BankTransferQueuePage> handler,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        // A staff work list of named customers and their payment references. Never cached anywhere, and
        // keyed on the cookie if any intermediary ignores that.
        NoStore(httpContext.Response);

        return TypedResults.Ok(
            await handler.HandleAsync(new GetBankTransferQueueQuery(page, pageSize), cancellationToken));
    }

    private static Task<IResult> ApproveAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<ReviewBankTransferCommand, ReviewBankTransferResult> handler,
        Guid orderPublicId,
        CancellationToken cancellationToken) =>
        DecideAsync(
            httpContext,
            antiforgery,
            handler,
            orderPublicId,
            BankTransferDecisionKind.Approve,
            cancellationToken);

    private static Task<IResult> RejectAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<ReviewBankTransferCommand, ReviewBankTransferResult> handler,
        Guid orderPublicId,
        CancellationToken cancellationToken) =>
        DecideAsync(
            httpContext,
            antiforgery,
            handler,
            orderPublicId,
            BankTransferDecisionKind.Reject,
            cancellationToken);

    private static Task<IResult> AcceptLateAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<ReviewBankTransferCommand, ReviewBankTransferResult> handler,
        Guid orderPublicId,
        CancellationToken cancellationToken) =>
        DecideAsync(
            httpContext,
            antiforgery,
            handler,
            orderPublicId,
            BankTransferDecisionKind.AcceptLate,
            cancellationToken);

    /// <summary>
    /// The one path all three decisions take: antiforgery, read the note, hand it to the use case, map the
    /// answer.
    /// </summary>
    /// <remarks>
    /// <b>Three routes and one body reader, so the three cannot disagree about what a note is.</b> The
    /// decision is in the URL rather than in the body because it decides the permission-bearing action and
    /// a field in a body is the wrong place for that — a client that mistyped <c>"reject"</c> as
    /// <c>"approve"</c> would otherwise take the money.
    /// </remarks>
    private static async Task<IResult> DecideAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<ReviewBankTransferCommand, ReviewBankTransferResult> handler,
        Guid orderPublicId,
        BankTransferDecisionKind kind,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        // A JsonObject read by hand rather than a bound record, the pattern AdminSettingsEndpoints
        // established: `{"note": 123}` then names the field instead of answering "malformed JSON"
        // (issue #47's family), and an absent note is told apart from a null one.
        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(
            httpContext.Request,
            cancellationToken);

        if (problem is not null)
        {
            return problem;
        }

        if (ReadNote(body!, out var note) is { } badNote)
        {
            return badNote;
        }

        var result = await handler.HandleAsync(
            new ReviewBankTransferCommand(orderPublicId, kind, note, AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        NoStore(httpContext.Response);

        return ToHttp(result);
    }

    private static IResult ToHttp(ReviewBankTransferResult result) => result switch
    {
        ReviewBankTransferResult.Applied applied => TypedResults.Ok(
            new BankTransferDecisionDto(applied.OrderNumber, applied.InvoiceNumber)),

        // A bare 404 with no body, the rule GET /api/orders/{token} already follows: "no such order", "that
        // is a PayPal order" and "that token is not yours" must be one answer.
        ReviewBankTransferResult.NotFound => TypedResults.NotFound(),

        ReviewBankTransferResult.Invalid invalid => ValidationProblem(invalid.Errors),

        // 409 and not 400: nothing is wrong with the request, the world moved. `code` is what lets the
        // screen offer Accept late payment when the answer is `order_expired` rather than printing a
        // sentence and leaving the staff member to work out what to do (doc 11 P7's business rule).
        ReviewBankTransferResult.Refused refused => TypedResults.Problem(
            title: "This decision could not be applied",
            detail: refused.Message,
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = refused.Code,
                ["orderStatus"] = refused.OrderStatus,
                ["paymentStatus"] = refused.PaymentStatus,
            }),

        ReviewBankTransferResult.ShortStock shortStock => TypedResults.Problem(
            title: "Some items are no longer in stock",
            detail: "This order cannot be paid now, because some of its items have been sold since it "
                + "expired. Refund the money received instead.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = ShortStockCode,

                // **The same key the `Refused` branch sends, and its absence here was a defect.** A screen
                // refused with `short_stock` and told nothing else cannot know the order is closed, so it
                // went back to offering Approve on an expired order. There is no `paymentStatus`: the
                // decision rolled back before touching the payment, so the one this would report is the
                // one the caller already has.
                ["orderStatus"] = shortStock.OrderStatus,

                // **An array of described lines, and deliberately not checkout's shape.**
                // POST /api/checkout/orders keys its short lines by variant id because its reader is the
                // shopper's own bag, which can resolve an id to the line the shopper is looking at. This
                // reader is a staff member on a queue screen with no order-detail page, so an id is a
                // dead end: the refusal has to say which item, in the words the customer bought it under.
                ["shortLines"] = shortStock.Lines,
            }),

        _ => throw new InvalidOperationException($"Unhandled review result {result.GetType().Name}."),
    };

    /// <summary>
    /// The code a late acceptance refused by the stock re-check carries. Not in
    /// <see cref="BankTransferRefusalCodes"/> because it is not a state the order is in — it is a fact
    /// about the catalogue.
    /// </summary>
    private const string ShortStockCode = "short_stock";

    /// <summary>Reads the optional <c>note</c> field, telling a wrong type from an absent one.</summary>
    private static ValidationProblem? ReadNote(JsonObject body, out string? note)
    {
        note = null;

        if (!body.TryGetPropertyValue("note", out var node) || node is null)
        {
            return null;
        }

        if (node.GetValueKind() != JsonValueKind.String)
        {
            return ValidationProblem(
                [new ValidationError("note", "Send the note as text, or leave it out.")]);
        }

        note = node.GetValue<string>();

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

/// <summary>What a decision did, for the staff screen's message.</summary>
/// <param name="OrderNumber">The order that moved.</param>
/// <param name="InvoiceNumber">
/// The gapless invoice number issued inside the transaction, or <see langword="null"/> for a rejection.
/// Returned because it is the one thing the decision created that is not on the screen already, and a staff
/// member reading it back confirms the approval actually settled.
/// </param>
public sealed record BankTransferDecisionDto(string OrderNumber, string? InvoiceNumber);
