using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Admin.Exports;
using SpencerDenim.Application.Marketing.Newsletter.Admin;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The newsletter subscriber list and its export (ADM-21, doc 11 P15 step 4).
/// <para>
/// <b>Two permissions, doc 11 P15's own table:</b> the list needs <c>Newsletter.View</c>, the export needs
/// <c>Newsletter.Export</c>. They are separate because an export is a bulk copy of personal data leaving
/// the system, which is a different act from reading a page of it on screen.
/// </para>
/// <para>
/// <b>The export is a POST</b>, with the antiforgery token, because it writes an audit row: a GET with a
/// side effect is one a prefetching browser or a pasted link can trigger.
/// </para>
/// </summary>
internal static class AdminNewsletterEndpoints
{
    public static IEndpointRouteBuilder MapAdminNewsletterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/newsletter/subscribers").WithTags("Admin Newsletter");

        api.MapGet("/", GetListAsync)
            .RequirePermission(Permissions.Newsletter.View)
            .WithName("AdminGetNewsletterSubscribers")
            .WithSummary("Newsletter subscribers, most recent consent first (doc 11 P15 step 4).")
            .WithDescription(
                "Filter by ?status=Subscribed or Unsubscribed, by consent date with ?from= and ?to= "
                + "(yyyy-MM-dd, Colombo calendar days, both inclusive), and by part of the address with "
                + "?search=. The two counts are over the whole list and ignore the filters.")
            .Produces<NewsletterSubscriberPage>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        api.MapPost("/export", ExportAsync)
            .RequirePermission(Permissions.Newsletter.Export)
            .WithName("AdminExportNewsletterSubscribers")
            .WithSummary("Download the subscribed addresses as CSV (doc 11 P15 step 4).")
            .WithDescription(
                "**Subscribed addresses only, whatever ?status= says** — an unsubscribed address is a "
                + "person who asked not to be mailed. ?from=, ?to= and ?search= narrow it exactly as they "
                + "narrow the list. UTF-8 with a byte-order mark so Excel reads Sinhala and Tamil "
                + "correctly; a cell that would start like a spreadsheet formula is prefixed with a single "
                + "quote. Every export writes an Export row to the audit log before the file is sent.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> GetListAsync(
        HttpContext httpContext,
        IQueryHandler<GetNewsletterSubscribersQuery, NewsletterSubscriberPage> handler,
        string? search,
        string? status,
        string? from,
        string? to,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        if (ReadFilter(search, status, from, to, out var filter) is { } invalid)
        {
            return invalid;
        }

        return TypedResults.Ok(
            await handler.HandleAsync(new GetNewsletterSubscribersQuery(filter, page, pageSize), cancellationToken));
    }

    private static async Task<IResult> ExportAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<ExportNewsletterSubscribersCommand, NewsletterExportFile> handler,
        string? search,
        string? from,
        string? to,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        // No status parameter is read at all: the export is subscribed addresses, and the reader enforces
        // that in its own WHERE clause.
        if (ReadFilter(search, null, from, to, out var filter) is { } invalid)
        {
            return invalid;
        }

        var file = await handler.HandleAsync(
            new ExportNewsletterSubscribersCommand(filter, AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return TypedResults.File(file.Content, CsvDocument.ContentType, file.FileName);
    }

    private static ValidationProblem? ReadFilter(
        string? search,
        string? status,
        string? from,
        string? to,
        out NewsletterSubscriberFilter filter)
    {
        filter = new NewsletterSubscriberFilter(null, null, null, null);
        var errors = new List<ValidationError>();

        NewsletterSubscriberStatus? parsedStatus = null;

        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            // Names only. Enum.TryParse also accepts "1", which would make the database's number part of
            // the API's contract (doc 08 §1.4 keeps it the database's).
            if (char.IsAsciiDigit(status.Trim()[0])
                || !Enum.TryParse(status, ignoreCase: true, out NewsletterSubscriberStatus value)
                || !Enum.IsDefined(value))
            {
                errors.Add(new ValidationError(
                    "status",
                    "Use status=Subscribed or status=Unsubscribed, or leave it out for both."));
            }
            else
            {
                parsedStatus = value;
            }
        }

        var fromDay = ReadDay(from, "from", errors);
        var toDay = ReadDay(to, "to", errors);

        if (fromDay is { } start && toDay is { } end && start > end)
        {
            errors.Add(new ValidationError("to", "The end date is before the start date."));
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(
                errors
                    .GroupBy(error => error.Field, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(error => error.Message).Distinct(StringComparer.Ordinal).ToArray(),
                        StringComparer.Ordinal),
                title: "One or more fields are invalid.");
        }

        filter = new NewsletterSubscriberFilter(search, parsedStatus, fromDay, toDay);
        return null;
    }

    private static DateOnly? ReadDay(string? raw, string field, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return day;
        }

        errors.Add(new ValidationError(field, "Use a date in the form yyyy-MM-dd."));
        return null;
    }

    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, private";
        response.Headers.Vary = "Cookie";
    }
}
