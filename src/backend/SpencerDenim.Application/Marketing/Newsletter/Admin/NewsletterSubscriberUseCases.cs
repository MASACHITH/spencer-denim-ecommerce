using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Exports;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Common;

namespace SpencerDenim.Application.Marketing.Newsletter.Admin;

/// <summary>The subscriber list (ADM-21; <c>Newsletter.View</c>).</summary>
public sealed record GetNewsletterSubscribersQuery(NewsletterSubscriberFilter Filter, int? Page, int? PageSize);

public sealed class GetNewsletterSubscribersQueryHandler(INewsletterSubscriberReader reader)
    : IQueryHandler<GetNewsletterSubscribersQuery, NewsletterSubscriberPage>
{
    public const int DefaultPageSize = 25;

    /// <summary>
    /// The ceiling. A page of addresses is a copy of personal data, and the bulk copy has its own permission
    /// and its own audit row (the export); an unbounded page would be an unaudited export.
    /// </summary>
    public const int MaxPageSize = 100;

    public Task<NewsletterSubscriberPage> HandleAsync(
        GetNewsletterSubscribersQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.ReadPageAsync(
            query.Filter,
            // Capped so (page - 1) * pageSize cannot wrap negative (see AdminCustomerPaging.MaxPage).
            Math.Clamp(query.Page ?? 1, 1, int.MaxValue / MaxPageSize),
            Math.Clamp(query.PageSize ?? DefaultPageSize, 1, MaxPageSize),
            cancellationToken);
    }
}

/// <summary>
/// Export the subscribed addresses as CSV (ADM-21, doc 11 P15 step 4; <c>Newsletter.Export</c>).
/// </summary>
/// <param name="Filter">The list's filters. Its status is ignored: only subscribed addresses are exported.</param>
/// <param name="Actor">Who is exporting, for the audit row.</param>
public sealed record ExportNewsletterSubscribersCommand(NewsletterSubscriberFilter Filter, AdminActor Actor);

/// <summary>The file, ready to send.</summary>
public sealed record NewsletterExportFile(byte[] Content, string FileName, int RowCount);

/// <summary>
/// Builds the export and audits it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audited before the file is returned, and committed on its own</b> (doc 11 P15 step 4: "The export is
/// audited"; doc 11 P16: "Every CSV export is audited (<c>Export</c>)"). An export is a bulk copy of
/// personal data leaving the system, so the record that it happened must exist whenever the data could
/// have left — if the audit write fails, the export fails and no file is produced.
/// </para>
/// <para>
/// <b>The audit row records the shape of the export and not its contents</b>: the row count, the date range
/// and whether a search narrowed it. <b>Not the search text</b>, because it may be an email address, and
/// doc 08 §3.1 step 6's anonymisation scrubs audit rows by entity and user id — it could never find an
/// address typed into a search box. Nor, for the same reason, any address in the file.
/// </para>
/// <para>
/// <b>Columns:</b> the address, where it was given, and when the current consent was given in Colombo time.
/// Not the consent IP (proof of consent, purged at 90 days, of no use to a mail tool) and not the status
/// (every row is subscribed).
/// </para>
/// </remarks>
public sealed class ExportNewsletterSubscribersCommandHandler(
    INewsletterSubscriberReader reader,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<ExportNewsletterSubscribersCommand, NewsletterExportFile>
{
    /// <summary><c>ops.AuditLogs.EntityType</c> for this export.</summary>
    public const string EntityType = "NewsletterSubscriber";

    /// <summary>The header row, in column order.</summary>
    public static readonly IReadOnlyList<string> Header = ["Email", "Source", "Consent given (Colombo time)"];

    public async Task<NewsletterExportFile> HandleAsync(
        ExportNewsletterSubscribersCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var rows = await reader.ReadSubscribedForExportAsync(command.Filter, cancellationToken);
        var now = clock.UtcNow;

        await auditLog.WriteAsync(
            new AuditLog
            {
                OccurredAtUtc = now.UtcDateTime,
                UserId = command.Actor.UserId,
                UserEmail = command.Actor.Email,
                Action = AuditActions.Export,
                EntityType = EntityType,
                EntityId = null,
                AfterJson = JsonSerializer.Serialize(new
                {
                    rows = rows.Count,
                    status = "Subscribed",
                    consentFrom = command.Filter.ConsentFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    consentTo = command.Filter.ConsentTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    searchApplied = command.Filter.NormalisedSearch is not null,
                }),
                IpAddress = command.Actor.IpAddress,
                UserAgent = command.Actor.UserAgent,
                CorrelationId = command.Actor.CorrelationId,
            },
            cancellationToken);

        var csv = new CsvDocument().Row([.. Header]);

        foreach (var row in rows)
        {
            csv.Row(
                row.Email,
                row.Source,
                ColomboTime.ToLocal(new DateTimeOffset(DateTime.SpecifyKind(row.ConsentAtUtc, DateTimeKind.Utc)))
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        var day = ColomboTime.ToLocal(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new NewsletterExportFile(csv.ToBytes(), $"newsletter-subscribers-{day}.csv", rows.Count);
    }
}
