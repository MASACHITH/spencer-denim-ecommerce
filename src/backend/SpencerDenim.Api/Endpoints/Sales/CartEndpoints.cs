using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Application.Sales.Cart.Contracts;

namespace SpencerDenim.Api.Endpoints.Sales;

/// <summary>
/// The shopping bag (CART-01, CART-02, GLB-04).
/// <para>
/// <b>No authentication, and that is not the same as no identity.</b> Doc 08 §3.5 makes the cart
/// server-side for everyone: a signed-in customer's bag is found by their user id, a guest's by the
/// token in <c>sd_cart</c>. There is no customer sign-in in the repository yet (ACC-01), so every
/// request is identified by <see cref="SignedInCustomer.ReadCartLookupAsync"/>: the account's bag when a customer is signed in, the guest cookie's otherwise.
/// </para>
/// </summary>
internal static class CartEndpoints
{
    public static IEndpointRouteBuilder MapCartEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var cart = endpoints.MapGroup("/api/cart").WithTags("Cart");

        // GET only, no HEAD: unlike the catalogue there is nothing here for a monitor or a link checker
        // to probe, and every answer is personal.
        cart.MapGet(string.Empty, GetCartAsync)
            .WithName("GetCart")
            .WithSummary("The visitor's bag.")
            .WithDescription(
                "CART-01, CART-02, GLB-04. Every line is repriced from the catalogue on each read " +
                "(doc 08 §6) and says whether it can still be bought. Never creates a cart: doc 08 §3.5 " +
                "creates the row on the first Add to Bag, not on a page view.")
            .Produces<CartDto>();

        cart.MapPost("/items", AddItemAsync)
            .WithName("AddToCart")
            .WithSummary("Add a variant to the bag.")
            .WithDescription(
                "CART-01, PDP-03. Adding a variant already in the bag adds to its quantity, capped at 10 " +
                "(doc 08 §3.5, DB22). Creates the bag, and the guest cookie, if this is the first item.")
            .Produces<CartDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        cart.MapPatch("/items/{variantId:int}", SetQuantityAsync)
            .WithName("SetCartItemQuantity")
            .WithSummary("Set how many of one line the bag holds.")
            .WithDescription("CART-01. 1–10. Zero is not a quantity — remove the line instead.")
            .Produces<CartDto>()
            .ProducesValidationProblem();

        cart.MapDelete("/items/{variantId:int}", RemoveItemAsync)
            .WithName("RemoveCartItem")
            .WithSummary("Take one line out of the bag.")
            .WithDescription(
                "CART-01, and the server half of Move to wishlist. Idempotent: removing a line that is " +
                "not there answers the bag, not a 404.")
            .Produces<CartDto>()
            .ProducesValidationProblem();

        return endpoints;
    }

    private static async Task<Ok<CartDto>> GetCartAsync(
        IQueryHandler<GetCartQuery, CartView> handler,
        HttpContext httpContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var view = await handler.HandleAsync(new GetCartQuery(await SignedInCustomer.ReadCartLookupAsync(httpContext)), cancellationToken);
        return Answer(httpContext, clock, view);
    }

    private static async Task<Results<Ok<CartDto>, ProblemHttpResult, ValidationProblem>> AddItemAsync(
        [FromBody] AddCartItemRequest request,
        ICommandHandler<AddToCartCommand, CartWriteResult> handler,
        HttpContext httpContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new AddToCartCommand(await SignedInCustomer.ReadCartLookupAsync(httpContext), request?.VariantId ?? 0, request?.Quantity ?? 0),
            cancellationToken);

        return Answer(httpContext, clock, result);
    }

    private static async Task<Results<Ok<CartDto>, ProblemHttpResult, ValidationProblem>> SetQuantityAsync(
        int variantId,
        [FromBody] SetCartItemQuantityRequest request,
        ICommandHandler<SetCartItemQuantityCommand, CartWriteResult> handler,
        HttpContext httpContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new SetCartItemQuantityCommand(await SignedInCustomer.ReadCartLookupAsync(httpContext), variantId, request?.Quantity ?? 0),
            cancellationToken);

        return Answer(httpContext, clock, result);
    }

    private static async Task<Results<Ok<CartDto>, ProblemHttpResult, ValidationProblem>> RemoveItemAsync(
        int variantId,
        ICommandHandler<RemoveCartItemCommand, CartWriteResult> handler,
        HttpContext httpContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new RemoveCartItemCommand(await SignedInCustomer.ReadCartLookupAsync(httpContext), variantId),
            cancellationToken);

        return Answer(httpContext, clock, result);
    }

    private static Ok<CartDto> Answer(HttpContext httpContext, TimeProvider clock, CartView view)
    {
        CartCookie.Apply(httpContext.Response, view.Cookie, view.GuestToken, clock);
        NoStore(httpContext.Response);
        return TypedResults.Ok(view.Cart);
    }

    private static Results<Ok<CartDto>, ProblemHttpResult, ValidationProblem> Answer(
        HttpContext httpContext,
        TimeProvider clock,
        CartWriteResult result)
    {
        switch (result.Status)
        {
            case CartWriteStatus.Ok:
                CartCookie.Apply(httpContext.Response, result.View!.Cookie, result.View.GuestToken, clock);
                NoStore(httpContext.Response);
                return TypedResults.Ok(result.View.Cart);

            case CartWriteStatus.OutOfStock:
                NoStore(httpContext.Response);

                // 409 and not 400: the request was well formed, the shop's state refused it, and the same
                // request may well succeed after a restock. A 400 would tell the storefront to blame the
                // shopper's input for something that is nothing to do with it.
                return TypedResults.Problem(
                    title: "Out of stock",
                    detail: "That size has just sold out. Nothing was added to your bag.",
                    statusCode: StatusCodes.Status409Conflict);

            case CartWriteStatus.Invalid:
            case CartWriteStatus.NotSellable:
            default:
                NoStore(httpContext.Response);
                return ToValidationProblem(result.Errors);
        }
    }

    /// <summary>
    /// Every cart answer is personal, so none of it may be stored by a shared cache. <c>Vary: Cookie</c>
    /// as well as <c>no-store</c>: the two say different things, and a cache that honours only one of them
    /// has handed one shopper another shopper's bag.
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

/// <summary>Add to Bag's body (CART-01, PDP-03).</summary>
/// <param name="VariantId">The colour × size the shopper chose.</param>
/// <param name="Quantity">1–10.</param>
public sealed record AddCartItemRequest(int VariantId, int Quantity);

/// <summary>A quantity change's body (CART-01).</summary>
/// <param name="Quantity">1–10. Zero is a removal, which is a different request.</param>
public sealed record SetCartItemQuantityRequest(int Quantity);
