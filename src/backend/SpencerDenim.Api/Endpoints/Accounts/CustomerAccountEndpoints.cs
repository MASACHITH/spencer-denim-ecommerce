using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Accounts;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Infrastructure.Identity.Customers;

namespace SpencerDenim.Api.Endpoints.Accounts;

/// <summary>
/// My account: the profile, the address book and the order history (ACC-02, doc 11 P11 steps 10–12).
/// </summary>
/// <remarks>
/// <b>Every route reads its owner from the session cookie and never from the request.</b> There is no
/// route here that takes a customer id, and none of the ports has an overload that would accept one —
/// which is the structural version of the rule rather than a convention each endpoint has to remember.
/// <para>
/// <b>Every response is <c>no-store</c> with <c>Vary: Cookie</c>.</b> These payloads hold a name, a
/// telephone number, a street address and an order history; <c>CheckoutEndpoints</c> makes the same note
/// for the same reason — "the two say different things and a cache honouring only one of them has handed
/// one shopper another shopper's checkout".
/// </para>
/// <para>
/// <b>There is deliberately no order-detail route here.</b> Doc 11 P11 step 12's detail is exactly what
/// <c>GET /api/orders/{token}</c> already answers for a guest, and that page is built and tested. A
/// second detail projection would be a second answer to "what does this order say", and the two would
/// drift — <c>ICustomerOrderReader</c> records the argument, and <c>CustomerOrderSummaryDto.Token</c>
/// records why handing a token to its own owner is safe.
/// </para>
/// </remarks>
internal static class CustomerAccountEndpoints
{
    public static IEndpointRouteBuilder MapCustomerAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var api = endpoints
            .MapGroup("/api/account")
            .WithTags("Customer Account")
            .RequireAuthorization(CustomerAuthorization.PolicyName);

        api.MapGet("/profile", GetProfileAsync)
            .WithName("CustomerProfile")
            .WithSummary("The customer's own name, telephone number and default district (doc 11 P11 step 10).")
            .WithDescription(
                "**The email address is read-only here.** Doc 11 P11 step 10 includes changing it, and "
                + "that flow is not built: it needs the uniqueness check across both user types, a "
                + "confirmation token minted against the new address, the old address left working until "
                + "the new one is confirmed, and a decision about whether the old address is notified "
                + "(doc 08 §3.1 R10 gives that rule for staff only). Half of it would be worse than none.")
            .Produces<CustomerProfile>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        api.MapPut("/profile", SaveProfileAsync)
            .WithName("CustomerSaveProfile")
            .WithSummary("Save the customer's own profile (doc 11 P11 step 10).")
            .WithDescription(
                "The default district drives GLB-06's delivery estimate before checkout (doc 08 §3.5 "
                + "R7). A district that is unknown or no longer served is **dropped rather than "
                + "refused**, and the response shows it unset: refusing the whole save would lose a name "
                + "change for a reason that has nothing to do with the name.")
            .Produces<CustomerProfile>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        api.MapGet("/addresses", GetAddressesAsync)
            .WithName("CustomerAddresses")
            .WithSummary("The customer's saved addresses, default first (doc 11 P11 step 11).")
            .Produces<IReadOnlyList<CustomerAddressDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        api.MapPost("/addresses", AddAddressAsync)
            .WithName("CustomerAddAddress")
            .WithSummary("Save a new address (doc 11 P11 step 11).")
            .WithDescription(
                "Up to **10** per customer (doc 08 §3.1's app cap), counted inside the transaction that "
                + "inserts, so two tabs cannot both add an eleventh. The first address a customer saves "
                + "is their default whatever the form said — a book with one entry and no default leaves "
                + "checkout nothing to pre-select. Answers the **whole book** afterwards, because setting "
                + "a default clears the previous one.")
            .Produces<IReadOnlyList<CustomerAddressDto>>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/addresses/{addressId:int}", UpdateAddressAsync)
            .WithName("CustomerUpdateAddress")
            .WithSummary("Replace one saved address (doc 11 P11 step 11).")
            .WithDescription(
                "404 for an address that is not this customer's **and** for one that does not exist — one "
                + "refusal, because \"that address is not yours\" tells a caller the id exists. Ticking "
                + "the default box moves the default here; **unticking it does not remove the default**, "
                + "because a book with entries and no default leaves checkout nothing to pre-select.")
            .Produces<IReadOnlyList<CustomerAddressDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapDelete("/addresses/{addressId:int}", DeleteAddressAsync)
            .WithName("CustomerDeleteAddress")
            .WithSummary("Remove one saved address (doc 11 P11 step 11).")
            .WithDescription(
                "A hard delete, and **no order is affected**: `sales.Orders.ShipAddress` is a snapshot "
                + "with no foreign key to this table, so a past order keeps showing where it was "
                + "actually sent. Deleting the default promotes the newest remaining address.")
            .Produces<IReadOnlyList<CustomerAddressDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/addresses/{addressId:int}/default", SetDefaultAddressAsync)
            .WithName("CustomerSetDefaultAddress")
            .WithSummary("Make one saved address the default (doc 11 P11 step 11).")
            .WithDescription(
                "One default per customer is `UX_CustomerAddresses_UserId_Default`, a unique filtered "
                + "index — so the previous default is cleared and the new one set in one transaction, in "
                + "that order, because the other order violates the index.")
            .Produces<IReadOnlyList<CustomerAddressDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/orders", GetOrdersAsync)
            .WithName("CustomerOrders")
            .WithSummary("The customer's own orders, newest first, with their tracking (doc 11 P11 step 12).")
            .WithDescription(
                "**This is the gap W3-01 closes**: before it, a shopper who placed an order had no way "
                + "to look it up again — the tracking number existed only in the admin panel. Each row "
                + "carries the order number, the date, the total, the status as the shopper reads it, and "
                + "a `tracking` block with the courier's name, the tracking number and a link built from "
                + "that courier's current template (doc 08 §3.5). The block is **absent** rather than "
                + "full of nulls when there is nothing to track: an order that has not shipped, or a "
                + "store-pickup order, which has no courier at all.\n\n"
                + "Orders placed as a guest with the same address appear **only after the address is "
                + "confirmed** (doc 08 §3.1 D5). That is specified behaviour, not a gap — doc 11 P11's "
                + "own exception says \"customer never confirms → can sign in but guest orders are not "
                + "linked\" — and `emailConfirmed` on `/api/account/me` is how the page explains it.\n\n"
                + "`token` is the order's own unguessable `PublicId`, which is the address of the order "
                + "page (`/order/{token}`). Sending it to the customer it belongs to does not weaken it: "
                + "this payload needs a session cookie and lists only orders whose `CustomerUserId` is "
                + "the caller's.")
            .Produces<CustomerOrderPageDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return endpoints;
    }

    private static async Task<Results<Ok<CustomerProfile>, UnauthorizedHttpResult>> GetProfileAsync(
        IQueryHandler<GetCustomerProfileQuery, CustomerProfile?> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!CustomerSessionCookie.TryGetUserId(httpContext.User, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var profile = await handler.HandleAsync(new GetCustomerProfileQuery(userId), cancellationToken);

        return profile is null ? TypedResults.Unauthorized() : TypedResults.Ok(profile);
    }

    private static async Task<Results<Ok<CustomerProfile>, ValidationProblem, UnauthorizedHttpResult>>
        SaveProfileAsync(
            [FromBody] CustomerProfileRequest? request,
            ICommandHandler<SaveCustomerProfileCommand, Result<CustomerProfile?>> handler,
            HttpContext httpContext,
            CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!CustomerSessionCookie.TryGetUserId(httpContext.User, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var result = await handler.HandleAsync(
            new SaveCustomerProfileCommand(
                userId,
                request?.FirstName,
                request?.LastName,
                request?.Phone,
                request?.DefaultDistrictId),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return ToValidationProblem(result.Errors);
        }

        return result.Value is null ? TypedResults.Unauthorized() : TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<IReadOnlyList<CustomerAddressDto>>, UnauthorizedHttpResult>>
        GetAddressesAsync(
            IQueryHandler<GetCustomerAddressesQuery, IReadOnlyList<CustomerAddressDto>> handler,
            HttpContext httpContext,
            CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!CustomerSessionCookie.TryGetUserId(httpContext.User, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var addresses = await handler.HandleAsync(new GetCustomerAddressesQuery(userId), cancellationToken);

        return TypedResults.Ok(addresses);
    }

    private static async Task<IResult> AddAddressAsync(
        [FromBody] CustomerAddressRequest? request,
        ICommandHandler<SaveCustomerAddressCommand, Result<CustomerAddressResult>> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        await SaveAddressAsync(request, addressId: null, handler, httpContext, cancellationToken);

    private static async Task<IResult> UpdateAddressAsync(
        int addressId,
        [FromBody] CustomerAddressRequest? request,
        ICommandHandler<SaveCustomerAddressCommand, Result<CustomerAddressResult>> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        await SaveAddressAsync(request, addressId, handler, httpContext, cancellationToken);

    private static async Task<IResult> SaveAddressAsync(
        CustomerAddressRequest? request,
        int? addressId,
        ICommandHandler<SaveCustomerAddressCommand, Result<CustomerAddressResult>> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!CustomerSessionCookie.TryGetUserId(httpContext.User, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var result = await handler.HandleAsync(
            new SaveCustomerAddressCommand(
                userId,
                addressId,
                request?.Label,
                request?.RecipientName,
                request?.Phone,
                request?.Line1,
                request?.Line2,
                request?.City,

                // An omitted number binds 0 on a non-nullable property, and 0 is indistinguishable from
                // "they sent zero" — so the request type makes it nullable and the validator is told 0,
                // which it refuses with "Choose a district." rather than with a wrong message.
                request?.DistrictId ?? 0,
                request?.PostalCode,
                request?.Landmark,
                request?.IsDefault ?? false),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return ToValidationProblem(result.Errors);
        }

        return ToAddressResponse(result.Value, created: addressId is null);
    }

    private static async Task<IResult> DeleteAddressAsync(
        int addressId,
        ICommandHandler<DeleteCustomerAddressCommand, CustomerAddressResult> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!CustomerSessionCookie.TryGetUserId(httpContext.User, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var result = await handler.HandleAsync(
            new DeleteCustomerAddressCommand(userId, addressId),
            cancellationToken);

        return ToAddressResponse(result, created: false);
    }

    private static async Task<IResult> SetDefaultAddressAsync(
        int addressId,
        ICommandHandler<SetDefaultCustomerAddressCommand, CustomerAddressResult> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!CustomerSessionCookie.TryGetUserId(httpContext.User, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var result = await handler.HandleAsync(
            new SetDefaultCustomerAddressCommand(userId, addressId),
            cancellationToken);

        return ToAddressResponse(result, created: false);
    }

    private static async Task<Results<Ok<CustomerOrderPageDto>, UnauthorizedHttpResult>> GetOrdersAsync(
        IQueryHandler<GetCustomerOrdersQuery, CustomerOrderPageDto> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 0)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!CustomerSessionCookie.TryGetUserId(httpContext.User, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var orders = await handler.HandleAsync(
            new GetCustomerOrdersQuery(userId, page, pageSize),
            cancellationToken);

        return TypedResults.Ok(orders);
    }

    /// <summary>
    /// One mapping for all four address writes, so the four cannot answer differently about the same
    /// four outcomes.
    /// </summary>
    /// <remarks>
    /// <b>The whole book comes back, not the row that changed</b> (<c>CustomerAddressResult.Saved</c>
    /// records why): setting a default clears the previous one and deleting the default promotes another,
    /// so a response carrying one row would leave the screen showing two defaults or none.
    /// </remarks>
    private static IResult ToAddressResponse(CustomerAddressResult result, bool created) => result switch
    {
        CustomerAddressResult.Saved saved when created =>
            TypedResults.Created((string?)null, saved.Addresses),

        CustomerAddressResult.Saved saved => TypedResults.Ok(saved.Addresses),

        CustomerAddressResult.LimitReached limit => TypedResults.Problem(
            title: "That is as many addresses as we can save",
            detail:
                $"You can keep up to {limit.Limit} saved addresses. Remove one you no longer use and "
                + "try again.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "address_limit_reached",
                ["limit"] = limit.Limit,
            }),

        CustomerAddressResult.UnknownDistrict => TypedResults.ValidationProblem(
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["districtId"] = ["We do not deliver to that district. Choose another."],
            },
            title: "That district cannot be used"),

        // One refusal for "no such address" and "not yours", because the second tells a caller the id
        // exists — the convention the guest order link's single opaque refusal sets.
        _ => TypedResults.Problem(
            title: "We could not find that address",
            detail: "It may already have been removed.",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "address_not_found",
            }),
    };

    private static ValidationProblem ToValidationProblem(IReadOnlyList<ValidationError> errors) =>
        TypedResults.ValidationProblem(
            errors
                .GroupBy(error => error.Field, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Message).ToArray(),
                    StringComparer.Ordinal));
}
