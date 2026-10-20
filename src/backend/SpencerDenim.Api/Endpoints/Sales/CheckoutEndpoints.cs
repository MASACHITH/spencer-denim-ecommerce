using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Application.Sales.Checkout;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Orders.Customer;
using SpencerDenim.Application.Sales.Payments.Customer;

namespace SpencerDenim.Api.Endpoints.Sales;

/// <summary>
/// Checkout and the order it produces (CHK-01..04).
/// <para>
/// <b>No authentication is required, and a guest checkout is still a checkout (CHK-03).</b> The visitor is
/// identified exactly as the bag identifies them: by the signed-in customer when there is one, otherwise
/// by the token in <c>sd_cart</c> (<see cref="SignedInCustomer"/>), and <c>PlaceOrderCommand.CustomerUserId</c>
/// is filled from the same session — which is what puts the order in My account (ACC-02).
/// </para>
/// <para>
/// <b>Where this ends.</b> A placed order and its number. Nothing here touches money: PAY-01/PAY-03 — the
/// bank details, the reference to quote, the slip upload, the approval queue and the expiry job — are
/// W3-03's steps 2 and 3, and PAY-02 (PayPal) is W4-01.
/// </para>
/// </summary>
internal static class CheckoutEndpoints
{
    public static IEndpointRouteBuilder MapCheckoutEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var checkout = endpoints.MapGroup("/api/checkout").WithTags("Checkout");

        checkout.MapGet(string.Empty, GetCheckoutAsync)
            .WithName("GetCheckout")
            .WithSummary("The visitor's checkout: bag, districts, delivery options, payment methods.")
            .WithDescription(
                "CHK-01..03. Every line is repriced from the catalogue on each read (doc 08 §6). "
                + "Pass districtId and deliveryMethodId to be quoted a delivery fee: without a district "
                + "there is no zone, and doc 08 §6 step 10 has no fee to give — so none is shown rather "
                + "than one being invented. Pass promoCode to be quoted with a promo code applied "
                + "(ADM-13): it is revalidated on every read, and a code that does not qualify comes back "
                + "as `promoRefusal` with `appliedPromoCode` null, so the box clears rather than leaving a "
                + "discount on screen that is not in the total.")
            .Produces<CheckoutSessionDto>();

        checkout.MapPost("/orders", PlaceOrderAsync)
            .WithName("PlaceOrder")
            .WithSummary("Place the order.")
            .WithDescription(
                "CHK-01..04, doc 11 P6. Reserves stock for every line in one transaction and writes the "
                + "order as PendingPayment. Re-sending the same checkoutKey returns the first order and "
                + "creates nothing (doc 08 §5.4). 409 when a line has sold out or the total has changed "
                + "since it was displayed, or the promo code could not take a use (`promoRefusal`); nothing "
                + "is written in any of the three cases. 429 with `Retry-After` when this visitor address "
                + "has already placed its quota of orders in the current window (issue #107): nothing is "
                + "written, the bag and the checkout key are untouched, and a resubmission of a checkout key "
                + "that has already placed an order is never refused for it — it is answered with that order.")
            .Produces<OrderConfirmationDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // Outside the /api/checkout group: an order outlives the checkout that made it, and the guest
        // order link in doc 11 P6 step 11 points at the order, not at a checkout.
        endpoints.MapGet("/api/orders/{publicId:guid}", GetOrderAsync)
            .WithTags("Checkout")
            .WithName("GetOrder")
            .WithSummary("One placed order, by the unguessable token in its link.")
            .WithDescription(
                "CHK-04. The success page and a guest's order link read the same endpoint, so the page "
                + "survives a reload. A token that matches no order answers 404 and says nothing else.")
            .Produces<OrderConfirmationDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Doc 11 P7 step 3: the customer's own bank reference, on the order they reached by its token.
        // Beside GetOrder rather than inside /api/checkout, for the same reason GetOrder is: a shopper
        // whose bag has moved on — or who opens their link next week — is still looking at this order, and
        // nothing here reads the cart cookie.
        endpoints.MapPut("/api/orders/{publicId:guid}/payment-reference", SetPaymentReferenceAsync)
            .WithTags("Checkout")
            .WithName("SetOrderPaymentReference")
            .WithSummary("The bank reference the customer is quoting for their transfer (PAY-01).")
            .WithDescription(
                "Doc 11 P7 step 3. Updates the order's open bank payment, never creating a second one "
                + "(doc 08 \u00a73.5 N7). Sending null or an empty string clears it, which a customer who "
                + "mistyped it needs. A token that matches no order, or an order that can no longer be "
                + "paid, answers 404 and says nothing else \u2014 the token never expires and is a guest's only "
                + "credential, so a stranger must not learn that it names a real order.")
            .Accepts<SetPaymentReferenceRequest>("application/json")
            .Produces<OrderPaymentProofDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Doc 11 P10 flow A, step 1's customer half: "Opens the order (account or order link) and clicks
        // Cancel order". Beside GetOrder for the same reason the reference route is: a shopper who opens
        // their link next week is still looking at this order, and nothing here reads the cart cookie.
        endpoints.MapPost("/api/orders/{publicId:guid}/cancel", CancelOrderAsync)
            .WithTags("Checkout")
            .WithName("CancelOwnOrder")
            .WithSummary("The shopper cancels their own unpaid order (doc 11 P10 flow A).")
            .WithDescription(
                "Doc 11 P10 step 3, in one transaction: the order becomes Cancelled with the closed time, "
                + "its open payment becomes Cancelled, and the items go back on sale. It takes no body "
                + "and no reason — P10 asks the staff member for a reason and the customer only to "
                + "press the button; the order's history records that the customer decided. A token that "
                + "matches no order, an order that is already paid, one that has expired and one that is "
                + "already cancelled all answer **404 and say nothing else**, so there is one refusal "
                + "contract across the guest routes and no sentence that could read as having changed "
                + "something. It is **not** a confidentiality measure, and this description used to claim "
                + "it was: `GET /api/orders/{token}` answers 200 with the whole order for a real token in "
                + "any status, so a token holder already learns both that it is real and what state it is "
                + "in. A **paid** order is never cancelled — doc 11 P10 refunds one instead. No email is "
                + "sent and none is owed (§7 G-14).")
            .Produces<CancelOrderDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<Ok<CheckoutSessionDto>> GetCheckoutAsync(
        IQueryHandler<GetCheckoutQuery, CheckoutView> handler,
        HttpContext httpContext,
        TimeProvider clock,
        CancellationToken cancellationToken,
        [FromQuery] int? districtId = null,
        [FromQuery] int? deliveryMethodId = null,
        [FromQuery] string? promoCode = null)
    {
        var view = await handler.HandleAsync(
            new GetCheckoutQuery(await SignedInCustomer.ReadCartLookupAsync(httpContext), districtId, deliveryMethodId, promoCode),
            cancellationToken);

        CartCookie.Apply(httpContext.Response, view.Cookie, view.GuestToken, clock);
        NoStore(httpContext.Response);

        return TypedResults.Ok(view.Session);
    }

    private static async Task<Results<Created<OrderConfirmationDto>, Ok<OrderConfirmationDto>, ProblemHttpResult, ValidationProblem>>
        PlaceOrderAsync(
            [FromBody] PlaceOrderRequest? request,
            ICommandHandler<PlaceOrderCommand, PlaceOrderResult> handler,
            IOrderPlacementThrottle throttle,
            HttpContext httpContext,
            CancellationToken cancellationToken)
    {
        // `request` is nullable and every string on it is nullable, which is issue #47: a body of `{}` —
        // or no body at all — binds nulls into whatever the shape declares, and a non-nullable string
        // property is an annotation rather than a runtime guard. The command carries the nulls and the
        // validator turns them into one 400 naming every missing field. Nothing here dereferences before
        // that happens.
        var command = new PlaceOrderCommand(
            await SignedInCustomer.ReadCartLookupAsync(httpContext),
            request?.CheckoutKey ?? Guid.Empty,
            request?.FirstName,
            request?.LastName,
            request?.Email,
            request?.Phone,
            request?.DeliveryMethodId,
            request?.DistrictId,
            request?.PickupLocationId,
            request?.Address,
            request?.PaymentMethodCode,
            request?.ExpectedGrandTotal,
            request?.CustomerNote,

            // Doc 11 P6: "Customer IP is stored with the order for fraud checks and deleted after 90
            // days" (doc 08 §8.4 owns the purge, which is not built — see the task report). Read from the
            // connection rather than from a header the client controls; the forwarded-headers middleware
            // is what makes this the real address behind Caddy.
            httpContext.Connection.RemoteIpAddress?.ToString(),

            // ADM-13. Sent on every submit, not held on the server: the code travels on the quote request
            // too, so it is revalidated on both (doc 11 P3 step 12). A body that omits it is an order
            // placed without a code, which is what a shopper who cleared the box asked for.
            request?.PromoCode)
        {
            // **The one field that decides whether this order is in My account** (ACC-02). It was
            // never set before W3-01's tester pass, so every order placed while signed in was stored as
            // a guest order and never appeared in the history. Read from the customer session cookie,
            // never from the body.
            CustomerUserId = await SignedInCustomer.ReadIdAsync(httpContext),
        };

        var result = await handler.HandleAsync(command, cancellationToken);

        NoStore(httpContext.Response);

        switch (result.Status)
        {
            case PlaceOrderStatus.Placed:
                // 201 with the order's own address in Location. The resource that was created is the
                // order, and that is the address a shopper can come back to.
                return TypedResults.Created(
                    $"/api/orders/{result.Confirmation!.PublicId:D}",
                    result.Confirmation);

            case PlaceOrderStatus.AlreadyPlaced:
                // 200 and not 201: this request created nothing. The body is the order that was created
                // the first time, flagged as a replay, so a client that double-submitted still lands on
                // the right success page instead of being shown an error for succeeding twice.
                return TypedResults.Ok(result.Confirmation!);

            case PlaceOrderStatus.Invalid:
                return ToValidationProblem(result.Errors);

            case PlaceOrderStatus.Blocked:
                // 409: the request was well formed and the shop's state refused it. The same reasoning
                // the bag's out-of-stock answer records — a 400 would blame the shopper's input for
                // something that is nothing to do with it.
                return TypedResults.Problem(
                    title: "Checkout is not possible",
                    detail: BlockedDetail(result.Blockers),
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["blockers"] = result.Blockers.Select(blocker => blocker.ToString()).ToArray(),
                    });

            case PlaceOrderStatus.OutOfStock:
                return TypedResults.Problem(
                    title: "Out of stock",
                    detail:
                        "Some of your items sold out while you were checking out. Your order has not been "
                        + "placed and nothing has been charged.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        // Keyed by variant id so the page can mark the line it belongs to rather than
                        // showing one sentence about the whole bag.
                        ["shortLines"] = result.ShortLines.ToDictionary(
                            line => line.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            line => line.Value),
                    });

            case PlaceOrderStatus.TotalChanged:
                return TypedResults.Problem(
                    title: "Prices changed",
                    detail:
                        "The total has changed since this page was loaded. Review the new total and place "
                        + "your order again. Nothing has been placed.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["totals"] = result.Totals,
                    });

            case PlaceOrderStatus.PromoRejected:
                // 409 and not a 400 field error, although a withdrawn *delivery option* is reported as a
                // 400 on `deliveryMethodId` a few lines up. The difference is what the shopper has to do
                // next: a withdrawn delivery option leaves them on a form with one control to change,
                // while a refused code changes the total they were about to agree to, so the page has to
                // re-quote and ask again — the same shape as `TotalChanged`, which is also a 409.
                //
                // The reason travels as a name and the storefront writes the sentence, exactly as
                // `blockers` does, so the wording of "that code is fully used" lives in one file and not
                // two. `promoMinimumSpend` is sent only with `MinimumSpend`, because that is the one
                // refusal with a figure in it.
                return TypedResults.Problem(
                    title: "Promo code not applied",
                    detail:
                        "That promo code could not be used for this order. Your order has not been placed "
                        + "and nothing has been charged.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["promoRefusal"] = result.PromoRefusal!.Value.ToString(),
                        ["promoMinimumSpend"] = result.PromoMinimumSpend,
                    });

            case PlaceOrderStatus.RateLimited:
                // Issue #107. 429 and not a 409: the shop's state did not refuse this order, the caller's
                // own request rate did, and a client that understands the status can wait rather than ask the
                // shopper to change something. The whole window, because that is what the limiter reports for
                // a refused permit — the same figure the newsletter, contact and account endpoints send.
                //
                // **The sentence says what did not happen before it says what to do**, because a shopper
                // who reads "too many" at the last step of a checkout needs to know first that nothing was
                // charged and nothing is lost: the bag is untouched and the same key is still good.
                httpContext.Response.Headers.RetryAfter =
                    ((int)throttle.Window.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

                return TypedResults.Problem(
                    title: "Too many orders",
                    detail:
                        "Too many orders have been placed from your connection in a short time, so this one "
                        + "has not been placed. Nothing has been charged and your bag is unchanged. Please "
                        + "wait a few minutes and try again.",
                    statusCode: StatusCodes.Status429TooManyRequests,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "rate_limited",
                    });

            default:
                throw new InvalidOperationException($"Unhandled placement status {result.Status}.");
        }
    }

    private static async Task<Results<Ok<OrderConfirmationDto>, NotFound>> GetOrderAsync(
        Guid publicId,
        IQueryHandler<GetOrderQuery, OrderConfirmationDto?> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var order = await handler.HandleAsync(new GetOrderQuery(publicId), cancellationToken);

        NoStore(httpContext.Response);

        // A bare 404 with no body. "No such order" and "that order is not yours" must be the same answer:
        // telling them apart would confirm that a guessed token names a real order.
        return order is null ? TypedResults.NotFound() : TypedResults.Ok(order);
    }

    /// <summary>
    /// Doc 11 P10 flow A: the shopper's own Cancel order.
    /// </summary>
    /// <remarks>
    /// <b>No antiforgery token and no authentication, deliberately and consistently with the rest of this
    /// file.</b> There is no customer sign-in anywhere in this build (ACC-01 is W3-01) and a guest's order
    /// link is the only credential there is, so there is no session for a forged request to ride on —
    /// which is the thing antiforgery protects. <b>And the sharper question, asked rather than assumed:</b>
    /// what a cross-site form <i>could</i> do here is cancel an order whose token the attacker already
    /// knows — and knowing the token is already enough to read the whole order, including the customer's
    /// name, address and telephone number. So this route gives away no capability the existing
    /// <c>payment-reference</c> route does not. It is still the most destructive thing a token can do, and
    /// the mitigations that would matter are the ones doc 11 §7 G-41 asks for on the order <i>link</i> —
    /// rate limiting, and an expiring link — which are an architect decision rather than this task's.
    /// <para>
    /// <b>No body at all.</b> Nothing is read from the request, so there is nothing for issue #47's
    /// malformed-JSON family to go wrong with; a client that sends a body is not refused for it.
    /// </para>
    /// </remarks>
    private static async Task<IResult> CancelOrderAsync(
        HttpContext httpContext,
        ICommandHandler<CancelOwnOrderCommand, CancelOwnOrderResult> handler,
        Guid publicId,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        var result = await handler.HandleAsync(new CancelOwnOrderCommand(publicId), cancellationToken);

        return result switch
        {
            CancelOwnOrderResult.Cancelled cancelled => TypedResults.Ok(
                new CancelOrderDto(cancelled.OrderNumber)),

            // A bare 404, the same answer GetOrder and the reference route give. "No such order", "that
            // order is paid" and "that order expired a minute ago" must be indistinguishable.
            CancelOwnOrderResult.NotCancellable => TypedResults.NotFound(),

            _ => throw new InvalidOperationException($"Unhandled result {result.GetType().Name}."),
        };
    }

    /// <summary>One sentence per blocker, in the order the use case put them.</summary>
    /// <summary>
    /// Doc 11 P7 step 3's reference, written on to the order's open bank payment.
    /// </summary>
    /// <remarks>
    /// <b>No antiforgery token and no authentication, deliberately and consistently with the rest of this
    /// file.</b> There is no customer sign-in anywhere in this build (ACC-01 is W3-01) and a guest's order
    /// link is the only credential there is, so there is no session for a forged request to ride on — which
    /// is the thing antiforgery protects. What a cross-site form <i>could</i> do is write a reference on to
    /// an order whose token the attacker already knows, and knowing the token is already enough to read the
    /// whole order. The admin's own decisions are where antiforgery matters and every one of them has it.
    /// <para>
    /// <b>The body is read as a <c>JsonObject</c></b>, the pattern <c>AdminSettingsEndpoints</c>
    /// established: <c>{"reference": 123}</c> then names the field instead of answering "malformed JSON",
    /// which is issue #47's family.
    /// </para>
    /// </remarks>
    private static async Task<IResult> SetPaymentReferenceAsync(
        HttpContext httpContext,
        ICommandHandler<SubmitBankTransferReferenceCommand, SubmitBankTransferReferenceResult> handler,
        Guid publicId,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(
            httpContext.Request,
            cancellationToken);

        if (problem is not null)
        {
            return problem;
        }

        string? reference = null;

        if (body!.TryGetPropertyValue("reference", out var node) && node is not null)
        {
            if (node.GetValueKind() != JsonValueKind.String)
            {
                return ToValidationProblem(
                [
                    new ValidationError(
                        "reference",
                        "Send the reference as text, or as null to clear it."),
                ]);
            }

            reference = node.GetValue<string>();
        }

        var result = await handler.HandleAsync(
            new SubmitBankTransferReferenceCommand(publicId, reference),
            cancellationToken);

        return result switch
        {
            SubmitBankTransferReferenceResult.Saved saved => TypedResults.Ok(
                new OrderPaymentProofDto(saved.Reference, null, null, null, null)),

            SubmitBankTransferReferenceResult.Invalid invalid => ToValidationProblem(invalid.Errors),

            // A bare 404, the same answer GetOrder gives: "no such order", "that is a PayPal order" and
            // "that order can no longer be paid" must be indistinguishable.
            SubmitBankTransferReferenceResult.NotFound => TypedResults.NotFound(),

            _ => throw new InvalidOperationException($"Unhandled result {result.GetType().Name}."),
        };
    }

    private static string BlockedDetail(IReadOnlyList<CheckoutBlocker> blockers) =>
        string.Join(
            ' ',
            blockers.Select(blocker => blocker switch
            {
                CheckoutBlocker.EmptyBag => "Your bag is empty.",
                CheckoutBlocker.LineNotPurchasable =>
                    "Something in your bag cannot be bought right now. Go back to your bag to adjust it.",
                CheckoutBlocker.NoDeliveryMethod =>
                    "We cannot deliver to that district at the moment.",
                CheckoutBlocker.NoPaymentMethod =>
                    "No payment method is available at the moment. Please try again shortly.",
                _ => "Checkout is not available.",
            }));

    /// <summary>
    /// Nothing about a checkout may be stored by a shared cache: it holds a name, an address, a phone
    /// number and the idempotency token that places the order. <c>Vary: Cookie</c> as well as
    /// <c>no-store</c>, because the two say different things and a cache honouring only one of them has
    /// handed one shopper another shopper's checkout.
    /// </summary>
    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, private";
        response.Headers.Vary = "Cookie";
    }

    private static ValidationProblem ToValidationProblem(IReadOnlyList<ValidationError> errors) =>
        TypedResults.ValidationProblem(
            errors
                .GroupBy(error => error.Field, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Message).ToArray(),
                    StringComparer.Ordinal));
}

/// <summary>
/// Place order's body (CHK-01..04).
/// </summary>
/// <remarks>
/// <b>Every string is nullable and so is the record itself — issue #47.</b> A well-formed JSON object that
/// omits a required string binds <see langword="null"/> into a non-nullable reference property, because the
/// annotation is not a runtime check, and the public API answered <b>500</b> for that until a tester found
/// it. The shapes that matter are <c>{}</c> and <c>{"email":"a@b.c"}</c>, and both are integration-tested
/// against this endpoint. A checkout body is almost all strings, which is why this is spelled out here
/// rather than assumed.
/// </remarks>
/// <param name="CheckoutKey">
/// From <c>GET /api/checkout</c>. Sending it twice returns the same order rather than placing a second one.
/// </param>
/// <param name="ExpectedGrandTotal">
/// The total shown on the page. The order is refused if the freshly priced total differs (G-10), so an
/// order is never placed at a figure the customer has not seen.
/// </param>
/// <param name="PromoCode">
/// The promo code the shopper applied, or <see langword="null"/> for none (ADM-13). Case does not matter.
/// It is revalidated at placement, so a code that has since expired, filled up or stopped meeting its
/// minimum spend refuses the order with 409 <c>promoRefusal</c> rather than being quietly dropped — the
/// total the shopper agreed to included it.
/// </param>
public sealed record PlaceOrderRequest(
    Guid CheckoutKey,
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone,
    int? DeliveryMethodId,
    int? DistrictId,
    int? PickupLocationId,
    CheckoutAddressInput? Address,
    string? PaymentMethodCode,
    decimal? ExpectedGrandTotal,
    string? CustomerNote,
    string? PromoCode);

/// <summary>
/// The bank reference a customer quotes for their transfer (PAY-01, doc 11 P7 step 3).
/// </summary>
/// <remarks>
/// <b>The OpenAPI shape only; the endpoint reads the body as a <c>JsonObject</c>.</b> One field, and
/// nullable because that is the truth about what JSON can send (issue #47): a non-nullable
/// <see langword="string"/> binds a missing property to null anyway and the useful answer names the field.
/// </remarks>
/// <param name="Reference">
/// What the customer's bank gave them. Null or blank clears it, which a customer who mistyped it needs.
/// </param>
public sealed record SetPaymentReferenceRequest(string? Reference);
