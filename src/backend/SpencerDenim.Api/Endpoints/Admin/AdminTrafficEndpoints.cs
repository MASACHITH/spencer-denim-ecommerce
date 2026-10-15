using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Application.Analytics.Reporting;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// Traffic (ADM-20, the Traffic page) and the dashboard's Traffic Sources widget (ADM-06), doc 11 P16.
/// <para>
/// <b>Both need <c>Traffic.View</c></b>, including the dashboard widget: doc 11 P16 says Order Staff "see the
/// dashboard but not Reports or Traffic", and a channel breakdown of the shop's visitors is traffic whichever
/// screen it is drawn on. The dashboard shows the widget only to a role that holds the permission, as it does
/// Recent Orders for <c>Orders.View</c>.
/// </para>
/// <para>
/// <b>Dates are Colombo days</b>, as on the dashboard: <c>?from=</c>, <c>?to=</c>, both included, at most 366 days,
/// default the last 30 including today. Today and the three days before it are counted live from the raw page
/// views; older days come from the nightly tables.
/// </para>
/// </summary>
internal static class AdminTrafficEndpoints
{
    public static IEndpointRouteBuilder MapAdminTrafficEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/traffic", GetTrafficAsync)
            .RequirePermission(Permissions.Traffic.View)
            .WithTags("Admin Traffic")
            .WithName("AdminGetTraffic")
            .WithSummary("Visits, page views, channels, devices, sources and top pages for a period (ADM-20).")
            .WithDescription(
                "Visits are human sessions per Colombo day; bots are counted apart and in nothing else. Each "
                + "session's views are credited to the channel, device and source of its first view that day. "
                + "?from= and ?to= are Colombo days, both included, at most 366 days.")
            .Produces<TrafficReportDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        endpoints.MapGet("/api/admin/dashboard/traffic-sources", GetTrafficSourcesAsync)
            .RequirePermission(Permissions.Traffic.View)
            .WithTags("Admin Dashboard")
            .WithName("AdminGetTrafficSources")
            .WithSummary("Visits by channel, and the conversion rate, for the dashboard (ADM-06).")
            .WithDescription(
                "Conversion is paid orders (by paid date) ÷ visits, as a percentage; null when there were no visits.")
            .Produces<TrafficSourcesDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> GetTrafficAsync(
        HttpContext httpContext,
        IClock clock,
        IQueryHandler<GetTrafficReportQuery, TrafficReportDto> handler,
        string? from,
        string? to,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        var errors = new List<ValidationError>();
        var today = ReportPeriod.ColomboDayOf(clock.UtcNow);

        if (ReportPeriod.Parse(from, to, today, errors) is not { } period)
        {
            return Invalid(errors);
        }

        return TypedResults.Ok(await handler.HandleAsync(new GetTrafficReportQuery(period, today), cancellationToken));
    }

    private static async Task<IResult> GetTrafficSourcesAsync(
        HttpContext httpContext,
        IClock clock,
        IQueryHandler<GetTrafficSourcesQuery, TrafficSourcesDto> handler,
        string? from,
        string? to,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        var errors = new List<ValidationError>();
        var today = ReportPeriod.ColomboDayOf(clock.UtcNow);

        if (ReportPeriod.Parse(from, to, today, errors) is not { } period)
        {
            return Invalid(errors);
        }

        return TypedResults.Ok(await handler.HandleAsync(new GetTrafficSourcesQuery(period, today), cancellationToken));
    }

    private static ValidationProblem Invalid(List<ValidationError> errors) =>
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
