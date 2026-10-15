using SpencerDenim.Api.Authorization;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.Customers;
using SpencerDenim.Application.Admin.Customers.Contracts;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The admin customers screen (ADM-15, doc 11 P11 step 16): the list, one customer, and their order
/// history.
/// <para>
/// <b>One permission, <c>Customers.View</c>, for all three</b> — doc 11 P11's own table: "Customers list
/// and detail, order history, district | <c>Customers.View</c>". Opening one of the orders is the admin
/// order screen's business and needs <c>Orders.View</c> there; this screen links to it rather than
/// rebuilding it.
/// </para>
/// <para>
/// <b>Read only.</b> Doc 02's ADM-15 is "list, detail, order history, district". Editing a customer is not
/// in it, and anonymising one is doc 11 P17 (<c>Customers.Delete</c>), a separate task — so there is no
/// write route here and no antiforgery to check.
/// </para>
/// </summary>
internal static class AdminCustomerEndpoints
{
    public static IEndpointRouteBuilder MapAdminCustomerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/customers").WithTags("Admin Customers");

        api.MapGet("/", GetListAsync)
            .RequirePermission(Permissions.Customers.View)
            .WithName("AdminGetCustomers")
            .WithSummary("Registered customers, newest first (doc 11 P11 step 16).")
            .WithDescription(
                "Registered customer accounts only — a guest who never registered has no account, and is "
                + "found by searching the order list by email. One search box: an email address (matched "
                + "from its start), a telephone number (its last nine digits, so 0771234567 finds "
                + "+94771234567) or part of a name. ?districtId= keeps the customers whose default delivery "
                + "district is that one. Order count, lifetime spend (paid orders) and last order are "
                + "computed live from the account's own orders.")
            .Produces<AdminCustomerListPage>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        api.MapGet("/{customerId:int}", GetOneAsync)
            .RequirePermission(Permissions.Customers.View)
            .WithName("AdminGetCustomer")
            .WithSummary("One customer, with their saved addresses (doc 11 P11 step 16).")
            .WithDescription(
                "A staff account's id answers 404, the same as an id that does not exist: this screen "
                + "never shows a staff user.")
            .Produces<AdminCustomerDetailDto>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        api.MapGet("/{customerId:int}/orders", GetOrdersAsync)
            .RequirePermission(Permissions.Customers.View)
            .WithName("AdminGetCustomerOrders")
            .WithSummary("A customer's order history, newest first.")
            .WithDescription(
                "The orders on the account and no others. A guest order placed with the same email address "
                + "is not here until the customer has confirmed that address and it has been linked (doc "
                + "08 §3.1 D5).")
            .Produces<AdminCustomerOrderPage>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> GetListAsync(
        HttpContext httpContext,
        IQueryHandler<GetAdminCustomersQuery, AdminCustomerListPage> handler,
        string? search,
        int? districtId,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        // Names, addresses, telephone numbers and money: never cached anywhere.
        NoStore(httpContext.Response);

        return TypedResults.Ok(
            await handler.HandleAsync(
                new GetAdminCustomersQuery(search, districtId, page, pageSize),
                cancellationToken));
    }

    private static async Task<IResult> GetOneAsync(
        HttpContext httpContext,
        IQueryHandler<GetAdminCustomerQuery, AdminCustomerDetailDto?> handler,
        int customerId,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        return await handler.HandleAsync(new GetAdminCustomerQuery(customerId), cancellationToken) is { } customer
            ? TypedResults.Ok(customer)
            : TypedResults.NotFound();
    }

    private static async Task<IResult> GetOrdersAsync(
        HttpContext httpContext,
        IQueryHandler<GetAdminCustomerOrdersQuery, AdminCustomerOrderPage?> handler,
        int customerId,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        return await handler.HandleAsync(
                new GetAdminCustomerOrdersQuery(customerId, page, pageSize),
                cancellationToken) is { } orders
            ? TypedResults.Ok(orders)
            : TypedResults.NotFound();
    }

    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, private";
        response.Headers.Vary = "Cookie";
    }
}
