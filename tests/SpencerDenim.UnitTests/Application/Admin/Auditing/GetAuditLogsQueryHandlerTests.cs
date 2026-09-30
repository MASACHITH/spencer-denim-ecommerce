using SpencerDenim.Application.Admin.Auditing;
using SpencerDenim.Application.Admin.Auditing.Contracts;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.UnitTests.Application.Admin.Auditing;

/// <summary>The handler is a thin pass-through to the read port, exactly like <c>GetProductsQueryHandler</c>.</summary>
public sealed class GetAuditLogsQueryHandlerTests
{
    private sealed class RecordingReader(PagedResult<AuditLogEntryDto> result) : IAuditLogReader
    {
        public GetAuditLogsQuery? ReceivedQuery { get; private set; }

        public Task<PagedResult<AuditLogEntryDto>> ListAsync(GetAuditLogsQuery query, CancellationToken cancellationToken)
        {
            ReceivedQuery = query;
            return Task.FromResult(result);
        }
    }

    private static GetAuditLogsQuery Accepted(AuditLogListCriteria criteria)
    {
        var result = GetAuditLogsQuery.Create(criteria);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    [Fact]
    public async Task The_query_is_forwarded_to_the_reader_unchanged()
    {
        var empty = PagedResult.Empty<AuditLogEntryDto>(1, GetAuditLogsQuery.DefaultPageSize);
        var reader = new RecordingReader(empty);
        var handler = new GetAuditLogsQueryHandler(reader);
        var query = Accepted(new AuditLogListCriteria(EntityType: "Product", UserId: "3"));

        await handler.HandleAsync(query, TestContext.Current.CancellationToken);

        Assert.Same(query, reader.ReceivedQuery);
    }

    [Fact]
    public async Task The_readers_page_is_returned_as_is()
    {
        var entry = new AuditLogEntryDto(
            1,
            new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
            5,
            "staff@example.com",
            "Update",
            "Product",
            "42",
            null,
            null,
            "203.0.113.1",
            "test-agent",
            "corr-1");
        var page = new PagedResult<AuditLogEntryDto>([entry], 1, 25, 1);
        var handler = new GetAuditLogsQueryHandler(new RecordingReader(page));

        var result = await handler.HandleAsync(Accepted(new AuditLogListCriteria()), TestContext.Current.CancellationToken);

        Assert.Same(page, result);
        Assert.Same(entry, Assert.Single(result.Items));
    }

    [Fact]
    public async Task A_null_query_throws()
    {
        var handler = new GetAuditLogsQueryHandler(new RecordingReader(PagedResult.Empty<AuditLogEntryDto>(1, 25)));

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!, CancellationToken.None));
    }
}
