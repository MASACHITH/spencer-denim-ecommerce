using Microsoft.AspNetCore.Antiforgery;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Sales.Delivery.Admin;
using SpencerDenim.Application.Sales.Payments.Admin;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// Owner decision 2026-10-21 (Sri Lanka plus worldwide delivery; PayPal the only payment method):
/// <list type="bullet">
/// <item><description>
/// <c>/api/admin/delivery/overseas</c> — the Overseas delivery screen: the International delivery switch, the
/// regions and the countries. <c>Settings.View</c> to read, <c>Settings.Edit</c> to change, the permissions a
/// district's zone already uses.
/// </description></item>
/// <item><description>
/// <c>/api/admin/settings/payment-providers</c> — the Payment methods switch. <c>Settings.Payments</c> only (Super
/// Admin), audited, and raising the <c>SensitiveSettingsChanged</c> alert.
/// </description></item>
/// </list>
/// Every write needs the antiforgery header and the row's <c>rowVersion</c> (or <c>If-Match</c>), and answers with
/// the whole screen's new state, so the page never has to guess what the server now holds.
/// </summary>
internal static class AdminDeliveryEndpoints
{
    public static IEndpointRouteBuilder MapAdminDeliveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var overseas = endpoints.MapGroup("/api/admin/delivery/overseas").WithTags("Admin Delivery");

        overseas.MapGet(string.Empty, GetOverseasAsync)
            .RequirePermission(Permissions.Settings.View)
            .WithName("AdminGetOverseasDelivery")
            .WithSummary("International delivery's switch, the overseas regions and every country's fee.")
            .WithDescription(
                "Each country carries its own switch and optional overrides, and the effective fee and estimate a "
                + "shopper is charged once its region is applied (or the reason it cannot be ordered to). Sri "
                + "Lanka is not listed: it is priced by district.")
            .Produces<OverseasDeliveryAdminDto>()
            .Produces(StatusCodes.Status403Forbidden);

        overseas.MapPut("/international", SetInternationalAsync)
            .RequirePermission(Permissions.Settings.Edit)
            .WithName("AdminSetInternationalDelivery")
            .WithSummary("Switch every overseas order on or off.")
            .Accepts<SetInternationalDeliveryRequest>("application/json")
            .Produces<OverseasDeliveryAdminDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        overseas.MapPut("/regions/{code}", UpdateRegionAsync)
            .RequirePermission(Permissions.Settings.Edit)
            .WithName("AdminUpdateDeliveryRegion")
            .WithSummary("One region's fee (LKR), estimated days and switch.")
            .WithDescription(
                "PUT replaces: send fee (null = not priced, so its countries cannot be ordered to unless they "
                + "override it), estimatedDaysMin and estimatedDaysMax (1 to 90), isEnabled and rowVersion.")
            .Accepts<UpdateDeliveryRegionRequest>("application/json")
            .Produces<OverseasDeliveryAdminDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        overseas.MapPut("/countries/{code}", UpdateCountryAsync)
            .RequirePermission(Permissions.Settings.Edit)
            .WithName("AdminUpdateCountryDelivery")
            .WithSummary("One country's switch and its own fee and estimate (null = the region's).")
            .Accepts<UpdateCountryDeliveryRequest>("application/json")
            .Produces<OverseasDeliveryAdminDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var payments = endpoints.MapGroup("/api/admin/settings/payment-providers").WithTags("Admin Settings");

        payments.MapGet(string.Empty, GetPaymentProvidersAsync)
            .RequirePermission(Permissions.Settings.Payments)
            .WithName("AdminGetPaymentProviders")
            .WithSummary("Every payment method, whether it is switched on and whether checkout offers it.")
            .Produces<PaymentProvidersAdminDto>()
            .Produces(StatusCodes.Status403Forbidden);

        payments.MapPut("/{code}", SetPaymentProviderAsync)
            .RequirePermission(Permissions.Settings.Payments)
            .WithName("AdminSetPaymentProvider")
            .WithSummary("Switch one payment method on or off (Super Admin).")
            .WithDescription(
                "Switching on needs the method to be ready (PayPal: credentials, webhook ID and a fresh rate). "
                + "Switching off is always allowed, the last one included — checkout then refuses every order. "
                + "Orders already placed are not affected. Audited, and raises a SensitiveSettingsChanged alert.")
            .Accepts<SetPaymentProviderRequest>("application/json")
            .Produces<PaymentProvidersAdminDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> GetOverseasAsync(
        IQueryHandler<GetOverseasDeliveryAdminQuery, OverseasDeliveryAdminDto> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);
        return TypedResults.Ok(await handler.HandleAsync(new GetOverseasDeliveryAdminQuery(), cancellationToken));
    }

    private static async Task<IResult> SetInternationalAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SetInternationalDeliveryCommand, OverseasDeliveryWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<SetInternationalDeliveryRequest>(
            httpContext.Request,
            cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var result = await handler.HandleAsync(
            new SetInternationalDeliveryCommand(
                body!.IsEnabled,
                AdminWriteRequest.RowVersion(httpContext.Request, body.RowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(httpContext, result);
    }

    private static async Task<IResult> UpdateRegionAsync(
        string code,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateDeliveryRegionCommand, OverseasDeliveryWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<UpdateDeliveryRegionRequest>(
            httpContext.Request,
            cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var result = await handler.HandleAsync(
            new UpdateDeliveryRegionCommand(
                code,
                body!.Fee,
                body.EstimatedDaysMin,
                body.EstimatedDaysMax,
                body.IsEnabled,
                AdminWriteRequest.RowVersion(httpContext.Request, body.RowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(httpContext, result);
    }

    private static async Task<IResult> UpdateCountryAsync(
        string code,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateCountryDeliveryCommand, OverseasDeliveryWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<UpdateCountryDeliveryRequest>(
            httpContext.Request,
            cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var result = await handler.HandleAsync(
            new UpdateCountryDeliveryCommand(
                code,
                body!.IsShippingEnabled,
                body.DeliveryFeeOverride,
                body.EstimatedDaysMinOverride,
                body.EstimatedDaysMaxOverride,
                AdminWriteRequest.RowVersion(httpContext.Request, body.RowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(httpContext, result);
    }

    private static async Task<IResult> GetPaymentProvidersAsync(
        IQueryHandler<GetPaymentProvidersAdminQuery, PaymentProvidersAdminDto> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);
        return TypedResults.Ok(await handler.HandleAsync(new GetPaymentProvidersAdminQuery(), cancellationToken));
    }

    private static async Task<IResult> SetPaymentProviderAsync(
        string code,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SetPaymentProviderEnabledCommand, PaymentProviderWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<SetPaymentProviderRequest>(
            httpContext.Request,
            cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var result = await handler.HandleAsync(
            new SetPaymentProviderEnabledCommand(
                code,
                body!.IsEnabled,
                AdminWriteRequest.RowVersion(httpContext.Request, body.RowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        NoStore(httpContext.Response);

        return result switch
        {
            PaymentProviderWriteResult.Saved saved => TypedResults.Ok(saved.Panel),
            PaymentProviderWriteResult.Invalid invalid => Validation(invalid.Errors),
            PaymentProviderWriteResult.NotFound => AdminWriteRequest.Problem(
                StatusCodes.Status404NotFound,
                "No such payment method",
                "There is no payment method with that code.",
                "not_found"),
            PaymentProviderWriteResult.Forbidden => TypedResults.Forbid(),
            PaymentProviderWriteResult.Conflict conflict => AdminWriteRequest.Problem(
                StatusCodes.Status409Conflict,
                "Changed by somebody else",
                conflict.Message,
                "stale_row_version"),
            _ => throw new InvalidOperationException($"Unhandled result {result.GetType().Name}."),
        };
    }

    private static IResult ToHttp(HttpContext httpContext, OverseasDeliveryWriteResult result)
    {
        NoStore(httpContext.Response);

        return result switch
        {
            OverseasDeliveryWriteResult.Saved saved => TypedResults.Ok(saved.Screen),
            OverseasDeliveryWriteResult.Invalid invalid => Validation(invalid.Errors),
            OverseasDeliveryWriteResult.NotFound => AdminWriteRequest.Problem(
                StatusCodes.Status404NotFound,
                "Not found",
                "There is no overseas region or country with that code.",
                "not_found"),
            OverseasDeliveryWriteResult.Conflict conflict => AdminWriteRequest.Problem(
                StatusCodes.Status409Conflict,
                "Changed by somebody else",
                conflict.Message,
                "stale_row_version"),
            _ => throw new InvalidOperationException($"Unhandled result {result.GetType().Name}."),
        };
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem Validation(
        IReadOnlyList<ValidationError> errors) =>
        TypedResults.ValidationProblem(
            errors
                .GroupBy(error => error.Field, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(error => error.Message).ToArray()));

    private static void NoStore(HttpResponse response) =>
        response.Headers.CacheControl = "no-store, no-cache, private";
}

/// <summary>The International delivery switch.</summary>
/// <param name="IsEnabled">True to accept overseas orders.</param>
/// <param name="RowVersion">The token from the screen.</param>
public sealed record SetInternationalDeliveryRequest(bool? IsEnabled, string? RowVersion);

/// <summary>One region, replaced.</summary>
/// <param name="Fee">LKR, 0 to 1,000,000, or null for "not priced".</param>
/// <param name="EstimatedDaysMin">1 to 90.</param>
/// <param name="EstimatedDaysMax">1 to 90, at least the minimum.</param>
/// <param name="IsEnabled">Off switches off every country in the region.</param>
/// <param name="RowVersion">The token from the screen.</param>
public sealed record UpdateDeliveryRegionRequest(
    decimal? Fee,
    int? EstimatedDaysMin,
    int? EstimatedDaysMax,
    bool? IsEnabled,
    string? RowVersion);

/// <summary>One country, replaced.</summary>
/// <param name="IsShippingEnabled">Whether the country is delivered to.</param>
/// <param name="DeliveryFeeOverride">LKR, or null to use the region's fee.</param>
/// <param name="EstimatedDaysMinOverride">With the maximum, or both null to use the region's estimate.</param>
/// <param name="EstimatedDaysMaxOverride">With the minimum.</param>
/// <param name="RowVersion">The token from the screen.</param>
public sealed record UpdateCountryDeliveryRequest(
    bool? IsShippingEnabled,
    decimal? DeliveryFeeOverride,
    int? EstimatedDaysMinOverride,
    int? EstimatedDaysMaxOverride,
    string? RowVersion);

/// <summary>One payment method's switch.</summary>
/// <param name="IsEnabled">True to offer it at checkout.</param>
/// <param name="RowVersion">The token from the panel.</param>
public sealed record SetPaymentProviderRequest(bool? IsEnabled, string? RowVersion);
