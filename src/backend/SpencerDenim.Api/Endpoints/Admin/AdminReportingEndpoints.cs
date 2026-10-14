using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Exports;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Application.Admin.Reporting.Contracts;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The dashboard (ADM-04, ADM-05, ADM-07, ADM-08) and the reports (ADM-19), doc 11 P16.
/// <para>
/// <b>Permissions are doc 11 P16's own table:</b> the dashboard needs <c>Dashboard.View</c>; a report needs
/// <c>Reports.View</c> and its CSV <c>Reports.Export</c>. Order Staff see the dashboard and not the reports
/// (P16's last edge case), which is what the seeded roles already give them.
/// </para>
/// <para>
/// <b>Recent orders are not here.</b> The dashboard's searchable recent-orders widget reads the admin order
/// list (<c>GET /api/admin/orders</c>, <c>Orders.View</c>) — the same search, the same rows, the same links
/// to the order screen — rather than a second copy of it, and the screen shows the widget only to a role
/// that holds <c>Orders.View</c>.
/// </para>
/// <para>
/// <b>Traffic (ADM-06, ADM-20) is not here either</b>: it needs doc 08 §3.8's <c>analytics</c> tables, which
/// do not exist. Nothing on these routes stands in for it.
/// </para>
/// <para>
/// <b>Dates are Colombo calendar days</b>, <c>yyyy-MM-dd</c>, both ends included; with none given the period
/// is the last 30 days including today. An export is a POST with the antiforgery token, because it writes an
/// audit row before the file is sent.
/// </para>
/// </summary>
internal static class AdminReportingEndpoints
{
    public static IEndpointRouteBuilder MapAdminReportingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var dashboard = endpoints.MapGroup("/api/admin/dashboard").WithTags("Admin Dashboard");

        dashboard.MapGet("/", GetDashboardAsync)
            .RequirePermission(Permissions.Dashboard.View)
            .WithName("AdminGetDashboard")
            .WithSummary("KPIs against the previous period, and the sales widgets (doc 11 P16).")
            .WithDescription(
                "?from= and ?to= are Colombo days (yyyy-MM-dd), both included, at most 366 days; the "
                + "previous period is the same number of days immediately before. Gross revenue is the "
                + "grand total of orders by paid date, refunded orders included; orders are counted by "
                + "placed date. Department, category and product figures are order-line totals after "
                + "promo and exclude delivery, so they do not add up to gross revenue.")
            .Produces<AdminDashboardDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        dashboard.MapGet("/trend", GetTrendAsync)
            .RequirePermission(Permissions.Dashboard.View)
            .WithName("AdminGetSalesTrend")
            .WithSummary("Revenue and orders over twelve months, by ISO week or by month (ADM-04).")
            .WithDescription(
                "?interval=Week or Month (default Month). ?to= is the last Colombo day (default today); "
                + "the window is that month and the eleven before it, or that ISO week and the 51 before it.")
            .Produces<SalesTrendDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        dashboard.MapGet("/low-stock", GetLowStockAsync)
            .RequirePermission(Permissions.Dashboard.View)
            .WithName("AdminGetLowStock")
            .WithSummary("Variants to replenish: 0 < available ≤ threshold, lowest first (ADM-08).")
            .WithDescription(
                "Live. A variant's own threshold applies, or the shop default when it has none. The total "
                + "counts every low variant; the rows are the lowest ten.")
            .Produces<LowStockDto>()
            .Produces(StatusCodes.Status403Forbidden);

        var reports = endpoints.MapGroup("/api/admin/reports").WithTags("Admin Reports");

        reports.MapGet("/sales", GetSalesReportAsync)
            .RequirePermission(Permissions.Reports.View)
            .WithName("AdminGetSalesReport")
            .WithSummary("Sales by day, ISO week or month, with a total (ADM-19).")
            .WithDescription("?from=, ?to= as the dashboard; ?groupBy=Day, Week or Month (default Day).")
            .Produces<SalesReportDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        reports.MapPost("/sales/export", ExportSalesReportAsync)
            .RequirePermission(Permissions.Reports.Export)
            .WithName("AdminExportSalesReport")
            .WithSummary("Download the sales report as CSV (audited).")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        reports.MapGet("/inventory", GetInventoryReportAsync)
            .RequirePermission(Permissions.Reports.View)
            .WithName("AdminGetInventoryReport")
            .WithSummary("Stock per active variant, now (ADM-19).")
            .WithDescription(
                "?status=All, InStock, LowStock or OutOfStock; ?search= matches part of a product name or "
                + "the start of a SKU or product code. Paged, 25 by default, at most 100.")
            .Produces<InventoryReportPage>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        reports.MapPost("/inventory/export", ExportInventoryReportAsync)
            .RequirePermission(Permissions.Reports.Export)
            .WithName("AdminExportInventoryReport")
            .WithSummary("Download the inventory report as CSV (audited): every matching variant.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        reports.MapGet("/customers", GetCustomerReportAsync)
            .RequirePermission(Permissions.Reports.View)
            .WithName("AdminGetCustomerReport")
            .WithSummary("Everyone who paid for an order in the period, highest spend first (ADM-19).")
            .WithDescription(
                "By email address, guests included. ?search= is the start of an address. Paged, 25 by "
                + "default, at most 100.")
            .Produces<CustomerReportPage>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        reports.MapPost("/customers/export", ExportCustomerReportAsync)
            .RequirePermission(Permissions.Reports.Export)
            .WithName("AdminExportCustomerReport")
            .WithSummary("Download the customers report as CSV (audited): every buyer in the period.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> GetDashboardAsync(
        HttpContext httpContext,
        IClock clock,
        IQueryHandler<GetAdminDashboardQuery, AdminDashboardDto> handler,
        string? from,
        string? to,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        var errors = new List<ValidationError>();

        if (ReportPeriod.Parse(from, to, Today(clock), errors) is not { } period)
        {
            return Invalid(errors);
        }

        return TypedResults.Ok(await handler.HandleAsync(new GetAdminDashboardQuery(period), cancellationToken));
    }

    private static async Task<IResult> GetTrendAsync(
        HttpContext httpContext,
        IClock clock,
        IQueryHandler<GetSalesTrendQuery, SalesTrendDto> handler,
        string? interval,
        string? to,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        var errors = new List<ValidationError>();
        var chosen = ReadInterval(interval, "interval", ReportInterval.Month, errors, ReportInterval.Week, ReportInterval.Month);
        var lastDay = ReportPeriod.ReadDay(to, "to", errors) ?? Today(clock);

        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        return TypedResults.Ok(await handler.HandleAsync(new GetSalesTrendQuery(chosen, lastDay), cancellationToken));
    }

    private static async Task<IResult> GetLowStockAsync(
        HttpContext httpContext,
        IQueryHandler<GetLowStockQuery, LowStockDto> handler,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        return TypedResults.Ok(
            await handler.HandleAsync(new GetLowStockQuery(GetLowStockQueryHandler.DefaultTake), cancellationToken));
    }

    private static async Task<IResult> GetSalesReportAsync(
        HttpContext httpContext,
        IClock clock,
        IQueryHandler<GetSalesReportQuery, SalesReportDto> handler,
        string? from,
        string? to,
        string? groupBy,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        if (ReadSalesFilter(clock, from, to, groupBy, out var period, out var grouping) is { } invalid)
        {
            return invalid;
        }

        return TypedResults.Ok(await handler.HandleAsync(new GetSalesReportQuery(period!, grouping), cancellationToken));
    }

    private static async Task<IResult> ExportSalesReportAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IClock clock,
        ICommandHandler<ExportSalesReportCommand, ReportExportFile> handler,
        string? from,
        string? to,
        string? groupBy,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        if (ReadSalesFilter(clock, from, to, groupBy, out var period, out var grouping) is { } invalid)
        {
            return invalid;
        }

        var file = await handler.HandleAsync(
            new ExportSalesReportCommand(period!, grouping, AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return TypedResults.File(file.Content, CsvDocument.ContentType, file.FileName);
    }

    private static async Task<IResult> GetInventoryReportAsync(
        HttpContext httpContext,
        IQueryHandler<GetInventoryReportQuery, InventoryReportPage> handler,
        string? status,
        string? search,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        if (ReadInventoryFilter(status, search, out var filter) is { } invalid)
        {
            return invalid;
        }

        return TypedResults.Ok(
            await handler.HandleAsync(new GetInventoryReportQuery(filter, page, pageSize), cancellationToken));
    }

    private static async Task<IResult> ExportInventoryReportAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<ExportInventoryReportCommand, ReportExportFile> handler,
        string? status,
        string? search,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        if (ReadInventoryFilter(status, search, out var filter) is { } invalid)
        {
            return invalid;
        }

        var file = await handler.HandleAsync(
            new ExportInventoryReportCommand(filter, AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return TypedResults.File(file.Content, CsvDocument.ContentType, file.FileName);
    }

    private static async Task<IResult> GetCustomerReportAsync(
        HttpContext httpContext,
        IClock clock,
        IQueryHandler<GetCustomerReportQuery, CustomerReportPage> handler,
        string? from,
        string? to,
        string? search,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        var errors = new List<ValidationError>();

        if (ReportPeriod.Parse(from, to, Today(clock), errors) is not { } period)
        {
            return Invalid(errors);
        }

        return TypedResults.Ok(
            await handler.HandleAsync(new GetCustomerReportQuery(period, search, page, pageSize), cancellationToken));
    }

    private static async Task<IResult> ExportCustomerReportAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IClock clock,
        ICommandHandler<ExportCustomerReportCommand, ReportExportFile> handler,
        string? from,
        string? to,
        string? search,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var errors = new List<ValidationError>();

        if (ReportPeriod.Parse(from, to, Today(clock), errors) is not { } period)
        {
            return Invalid(errors);
        }

        var file = await handler.HandleAsync(
            new ExportCustomerReportCommand(period, search, AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return TypedResults.File(file.Content, CsvDocument.ContentType, file.FileName);
    }

    private static ValidationProblem? ReadSalesFilter(
        IClock clock,
        string? from,
        string? to,
        string? groupBy,
        out ReportPeriod? period,
        out ReportInterval grouping)
    {
        var errors = new List<ValidationError>();

        grouping = ReadInterval(
            groupBy,
            "groupBy",
            ReportInterval.Day,
            errors,
            ReportInterval.Day,
            ReportInterval.Week,
            ReportInterval.Month);
        period = ReportPeriod.Parse(from, to, Today(clock), errors);

        return errors.Count > 0 ? Invalid(errors) : null;
    }

    private static ValidationProblem? ReadInventoryFilter(string? status, string? search, out InventoryReportFilter filter)
    {
        var errors = new List<ValidationError>();
        var chosen = InventoryStockFilter.All;

        if (!string.IsNullOrWhiteSpace(status))
        {
            // Names only: Enum.TryParse also accepts "2", which would make a number part of the contract.
            if (char.IsAsciiDigit(status.Trim()[0])
                || !Enum.TryParse(status.Trim(), ignoreCase: true, out chosen)
                || !Enum.IsDefined(chosen))
            {
                errors.Add(new ValidationError("status", "Use status=All, InStock, LowStock or OutOfStock."));
                chosen = InventoryStockFilter.All;
            }
        }

        filter = new InventoryReportFilter(search, chosen);

        return errors.Count > 0 ? Invalid(errors) : null;
    }

    /// <summary>Reads an interval by name, refusing a number and any interval not in <paramref name="allowed"/>.</summary>
    private static ReportInterval ReadInterval(
        string? raw,
        string field,
        ReportInterval fallback,
        List<ValidationError> errors,
        params ReportInterval[] allowed)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!char.IsAsciiDigit(raw.Trim()[0])
            && Enum.TryParse(raw.Trim(), ignoreCase: true, out ReportInterval parsed)
            && allowed.Contains(parsed))
        {
            return parsed;
        }

        errors.Add(new ValidationError(field, $"Use {field}={string.Join(", ", allowed)}."));
        return fallback;
    }

    private static DateOnly Today(IClock clock) => ReportPeriod.ColomboDayOf(clock.UtcNow);

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
        // Revenue, names and addresses: never cached by a browser or a proxy.
        response.Headers.CacheControl = "no-store, no-cache, private";
        response.Headers.Vary = "Cookie";
    }
}
