using System.Text;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Marketing.Newsletter.Admin;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.UnitTests.Application.Marketing;

/// <summary>The newsletter export's use case (ADM-21): the audit row, its contents, and the file.</summary>
public sealed class NewsletterExportCommandHandlerTests
{
    private static readonly AdminActor Actor = new(42, "manager@example.com", "203.0.113.5", "xunit", "trace-1");

    /// <summary>15 October 2026, 20:30 UTC — already the 16th in Colombo, which the file name must say.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 20, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_export_writes_one_Export_audit_row_with_its_shape()
    {
        var audit = new RecordingAuditLog();
        var handler = Handler(audit, Row("a@example.com"), Row("b@example.com"));

        var file = await handler.HandleAsync(
            new ExportNewsletterSubscribersCommand(
                new NewsletterSubscriberFilter("secret@example.com", null, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)),
                Actor),
            CancellationToken.None);

        Assert.Equal(2, file.RowCount);

        var row = Assert.Single(audit.Written);
        Assert.Equal(AuditActions.Export, row.Action);
        Assert.Equal(ExportNewsletterSubscribersCommandHandler.EntityType, row.EntityType);
        Assert.Equal(42, row.UserId);
        Assert.Equal("manager@example.com", row.UserEmail);
        Assert.Equal("203.0.113.5", row.IpAddress);
        Assert.Equal("xunit", row.UserAgent);
        Assert.Equal("trace-1", row.CorrelationId);
        Assert.Equal(Now.UtcDateTime, row.OccurredAtUtc);

        var after = JsonDocument.Parse(row.AfterJson!).RootElement;
        Assert.Equal(2, after.GetProperty("rows").GetInt32());
        Assert.Equal("2026-10-01", after.GetProperty("consentFrom").GetString());
        Assert.Equal("2026-10-31", after.GetProperty("consentTo").GetString());
        Assert.True(after.GetProperty("searchApplied").GetBoolean());

        // The search text — here an address — is never written to the audit log, nor is any exported one.
        Assert.DoesNotContain("secret", row.AfterJson!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", row.AfterJson!, StringComparison.Ordinal);

        // Nor in any other column (W3-07 tester: the search text written into EntityId passed every test).
        Assert.Null(row.EntityId);
        Assert.Null(row.BeforeJson);
        foreach (var column in typeof(AuditLog).GetProperties().Where(property => property.PropertyType == typeof(string)))
        {
            Assert.DoesNotContain("secret", (string?)column.GetValue(row) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>If the audit row cannot be written, no file is produced: an unaudited export never leaves.</summary>
    [Fact]
    public async Task A_failed_audit_write_fails_the_export()
    {
        var handler = Handler(new FailingAuditLog(), Row("a@example.com"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new ExportNewsletterSubscribersCommand(new NewsletterSubscriberFilter(null, null, null, null), Actor),
            CancellationToken.None));
    }

    [Fact]
    public async Task The_file_has_the_header_Colombo_times_and_a_Colombo_dated_name()
    {
        var handler = Handler(
            new RecordingAuditLog(),
            new NewsletterExportRow("=1+2@example.com", NewsletterSource.Homepage, new DateTime(2026, 10, 1, 18, 45, 0, DateTimeKind.Utc)));

        var file = await handler.HandleAsync(
            new ExportNewsletterSubscribersCommand(new NewsletterSubscriberFilter(null, null, null, null), Actor),
            CancellationToken.None);

        Assert.Equal("newsletter-subscribers-2026-10-16.csv", file.FileName);

        var text = new UTF8Encoding(false).GetString(file.Content, 3, file.Content.Length - 3);
        Assert.Equal(
            "\"Email\",\"Source\",\"Consent given (Colombo time)\"\r\n"
            + "\"'=1+2@example.com\",\"homepage\",\"2026-10-02 00:15\"\r\n",
            text);
    }

    [Fact]
    public void The_date_range_is_Colombo_midnight_to_the_next_Colombo_midnight()
    {
        var filter = new NewsletterSubscriberFilter(null, null, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1));

        Assert.Equal(new DateTime(2026, 9, 30, 18, 30, 0, DateTimeKind.Utc), filter.FromUtc);
        Assert.Equal(new DateTime(2026, 10, 1, 18, 30, 0, DateTimeKind.Utc), filter.ToUtcExclusive);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData(" Gmail ", "GMAIL")]
    public void The_search_is_trimmed_and_upper_cased(string? search, string? expected) =>
        Assert.Equal(expected, new NewsletterSubscriberFilter(search, null, null, null).NormalisedSearch);

    private static ExportNewsletterSubscribersCommandHandler Handler(IAuditLogWriter audit, params NewsletterExportRow[] rows) =>
        new(new StubReader(rows), audit, new FixedClock(Now));

    private static NewsletterExportRow Row(string email) =>
        new(email, NewsletterSource.Footer, new DateTime(2026, 10, 1, 4, 30, 0, DateTimeKind.Utc));

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubReader(IReadOnlyList<NewsletterExportRow> rows) : INewsletterSubscriberReader
    {
        public Task<NewsletterSubscriberPage> ReadPageAsync(
            NewsletterSubscriberFilter filter,
            int page,
            int pageSize,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NewsletterExportRow>> ReadSubscribedForExportAsync(
            NewsletterSubscriberFilter filter,
            CancellationToken cancellationToken) =>
            Task.FromResult(rows);
    }

    private sealed class RecordingAuditLog : IAuditLogWriter
    {
        public List<AuditLog> Written { get; } = [];

        public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken)
        {
            Written.Add(entry);
            return Task.CompletedTask;
        }

        public void Enlist(AuditLog entry) => Written.Add(entry);
    }

    private sealed class FailingAuditLog : IAuditLogWriter
    {
        public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The audit log is unavailable.");

        public void Enlist(AuditLog entry) => throw new InvalidOperationException("The audit log is unavailable.");
    }
}
