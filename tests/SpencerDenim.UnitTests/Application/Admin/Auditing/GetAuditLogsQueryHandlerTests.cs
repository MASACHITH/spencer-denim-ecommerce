using SpencerDenim.Application.Admin.Auditing;
using SpencerDenim.Application.Admin.Auditing.Contracts;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Application.Admin.Auditing;

/// <summary>The handler is a thin pass-through to the read port, exactly like <c>GetProductsQueryHandler</c>.</summary>
public sealed class GetAuditLogsQueryHandlerTests
{
    private const int ViewerId = 9;

    private sealed class RecordingReader(PagedResult<AuditLogEntryDto> result) : IAuditLogReader
    {
        public GetAuditLogsQuery? ReceivedQuery { get; private set; }

        public bool? ReceivedIncludeGrantsAllHolders { get; private set; }

        public Task<PagedResult<AuditLogEntryDto>> ListAsync(
            GetAuditLogsQuery query,
            bool includeGrantsAllHolders,
            CancellationToken cancellationToken)
        {
            ReceivedQuery = query;
            ReceivedIncludeGrantsAllHolders = includeGrantsAllHolders;
            return Task.FromResult(result);
        }
    }

    private sealed class StubPermissions(EffectivePermissions permissions) : IEffectivePermissionsProvider
    {
        public int? LastUserIdRequested { get; private set; }

        public Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken)
        {
            LastUserIdRequested = userId;
            return Task.FromResult(permissions);
        }
    }

    private static GetAuditLogsQueryHandler Handler(IAuditLogReader reader, bool viewerIsSuperAdmin = true) =>
        new(reader, new StubPermissions(viewerIsSuperAdmin
            ? new EffectivePermissions(true, [])
            : new EffectivePermissions(false, [Permissions.AuditLog.View])));

    private static GetAuditLogsQuery Accepted(AuditLogListCriteria criteria)
    {
        var result = GetAuditLogsQuery.Create(criteria, ViewerId);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    /// <summary>
    /// Tester round 3, Major 2: the audit log is the one read surface that had no viewer-awareness, and a holder of
    /// <c>AuditLog.View</c> alone (the seeded Store Manager's shape) could read a Super Admin's identity out of it.
    /// The handler must resolve the viewer's real permissions and tell the reader whether those rows are visible.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Rows_about_a_Super_Admin_are_included_only_for_a_Super_Admin_viewer(bool viewerIsSuperAdmin, bool expectedInclude)
    {
        var reader = new RecordingReader(PagedResult.Empty<AuditLogEntryDto>(1, GetAuditLogsQuery.DefaultPageSize));
        var permissions = new StubPermissions(viewerIsSuperAdmin
            ? new EffectivePermissions(true, [])
            : new EffectivePermissions(false, [Permissions.AuditLog.View]));
        var handler = new GetAuditLogsQueryHandler(reader, permissions);

        await handler.HandleAsync(Accepted(new AuditLogListCriteria()), TestContext.Current.CancellationToken);

        Assert.Equal(expectedInclude, reader.ReceivedIncludeGrantsAllHolders);
        Assert.Equal(ViewerId, permissions.LastUserIdRequested);
    }

    [Fact]
    public async Task The_query_is_forwarded_to_the_reader_unchanged()
    {
        var empty = PagedResult.Empty<AuditLogEntryDto>(1, GetAuditLogsQuery.DefaultPageSize);
        var reader = new RecordingReader(empty);
        var handler = Handler(reader);
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
        var handler = Handler(new RecordingReader(page));

        var result = await handler.HandleAsync(Accepted(new AuditLogListCriteria()), TestContext.Current.CancellationToken);

        Assert.Same(page, result);
        Assert.Same(entry, Assert.Single(result.Items));
    }

    [Fact]
    public async Task A_null_query_throws()
    {
        var handler = Handler(new RecordingReader(PagedResult.Empty<AuditLogEntryDto>(1, 25)));

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!, CancellationToken.None));
    }
}
