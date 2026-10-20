using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Content.Admin.Announcements;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.UnitTests.Application.Content.Admin;

/// <summary>The announcement strip's writes (GLB-03, doc 11 P13 part C; #192).</summary>
public sealed class AnnouncementWriteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);

    private readonly FakeAnnouncementAdminStore _store = new(Now.UtcDateTime.AddTicks(1234));
    private readonly RecordingAuditLogWriter _audit = new();
    private readonly RecordingHomePageCache _cache = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private AnnouncementWriteHandlers Handlers() => new(_store, _audit, _cache, new FixedClock(Now));

    [Fact]
    public async Task A_message_is_added_audited_and_evicts_and_answers_with_its_version()
    {
        var result = await Handlers().HandleAsync(
            new CreateAnnouncementCommand(Fields("  Free delivery this weekend  ", "/sale"), TestActors.Editor), Token);

        var saved = Assert.IsType<ContentWriteResult.Succeeded>(result);
        var row = Assert.Single(_store.Messages);
        Assert.Equal("Free delivery this weekend", row.Message);
        Assert.Equal("/sale", row.LinkUrl);
        Assert.Equal(AnnouncementVersion.Of(row.CreatedAtUtc, row.UpdatedAtUtc), saved.RowVersion);

        var audit = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.Create, audit.Action);
        Assert.Equal(AnnouncementWriteHandlers.EntityType, audit.EntityType);
        Assert.Equal(1, _cache.Evictions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("Two\nlines")]
    public async Task A_message_is_one_line_of_text_that_says_something(string? message)
    {
        var result = await Handlers().HandleAsync(new CreateAnnouncementCommand(Fields(message), TestActors.Editor), Token);

        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == "message");
        Assert.Empty(_store.Messages);
    }

    [Fact]
    public async Task Two_hundred_characters_is_the_limit_doc_11_names()
    {
        var atLimit = await Handlers().HandleAsync(
            new CreateAnnouncementCommand(Fields(new string('a', 200)), TestActors.Editor), Token);
        Assert.IsType<ContentWriteResult.Succeeded>(atLimit);

        var over = await Handlers().HandleAsync(
            new CreateAnnouncementCommand(Fields(new string('a', 201)), TestActors.Editor), Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(over).Errors, error => error.Field == "message");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example")]
    [InlineData("ftp://files.example/x")]
    [InlineData("https://a@evil.example")]
    public async Task A_link_must_be_safe(string linkUrl)
    {
        var result = await Handlers().HandleAsync(
            new CreateAnnouncementCommand(Fields("Sale", linkUrl), TestActors.Editor), Token);

        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == "linkUrl");
    }

    [Fact]
    public async Task The_strip_holds_twenty_messages_at_most()
    {
        for (var index = 0; index < AnnouncementWriteHandlers.MaxMessages; index++)
        {
            _store.AddExisting($"Message {index}", Now.UtcDateTime);
        }

        var result = await Handlers().HandleAsync(new CreateAnnouncementCommand(Fields("One more"), TestActors.Editor), Token);

        Assert.Equal(ContentConflictCodes.TooManyAnnouncements, Assert.IsType<ContentWriteResult.Conflict>(result).Code);
        Assert.Equal(AnnouncementWriteHandlers.MaxMessages, _store.Messages.Count);
    }

    [Fact]
    public async Task An_edit_needs_the_version_it_was_read_at()
    {
        var row = _store.AddExisting("Old", Now.UtcDateTime.AddDays(-1));

        foreach (var version in new string?[] { null, "", "12345" })
        {
            var result = await Handlers().HandleAsync(
                new UpdateAnnouncementCommand(row.Id, Fields("New"), version, TestActors.Editor), Token);

            Assert.Equal(ContentConflictCodes.StaleRowVersion, Assert.IsType<ContentWriteResult.Conflict>(result).Code);
        }

        Assert.Equal("Old", row.Message);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task An_edit_with_the_current_version_is_saved_and_an_untouched_one_is_not_an_event()
    {
        var row = _store.AddExisting("Island-wide delivery", Now.UtcDateTime.AddDays(-1));
        row.SortOrder = 1;
        var version = AnnouncementVersion.Of(row.CreatedAtUtc, row.UpdatedAtUtc);

        var untouched = await Handlers().HandleAsync(
            new UpdateAnnouncementCommand(row.Id, Fields("Island-wide delivery"), version, TestActors.Editor), Token);
        Assert.IsType<ContentWriteResult.Unchanged>(untouched);
        Assert.Equal(0, _cache.Evictions);

        var changed = await Handlers().HandleAsync(
            new UpdateAnnouncementCommand(row.Id, Fields("Island-wide delivery, every day"), version, TestActors.Editor), Token);
        var saved = Assert.IsType<ContentWriteResult.Succeeded>(changed);
        Assert.NotEqual(version, saved.RowVersion);
        Assert.Equal("{\"message\":\"Island-wide delivery\"}", Assert.Single(_audit.Entries).BeforeJson);
        Assert.Equal(1, _cache.Evictions);
    }

    [Fact]
    public async Task Removing_a_message_is_audited_and_evicts()
    {
        var row = _store.AddExisting("Gone soon", Now.UtcDateTime);

        var result = await Handlers().HandleAsync(
            new DeleteAnnouncementCommand(row.Id, AnnouncementVersion.Of(row.CreatedAtUtc, null), TestActors.Editor), Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.Empty(_store.Messages);
        var audit = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.Delete, audit.Action);
        Assert.Contains("Gone soon", audit.BeforeJson, StringComparison.Ordinal);
        Assert.Equal(1, _cache.Evictions);
    }

    [Fact]
    public async Task An_unknown_message_is_not_found()
    {
        var result = await Handlers().HandleAsync(new DeleteAnnouncementCommand(42, "1", TestActors.Editor), Token);

        Assert.IsType<ContentWriteResult.NotFound>(result);
    }

    /// <summary>
    /// The token is the millisecond the row last changed: the column is datetime2(3), so the ticks below a
    /// millisecond that the save stamped in memory are gone when the row is read back.
    /// </summary>
    [Fact]
    public void The_version_is_the_last_change_to_the_millisecond()
    {
        var created = new DateTime(2026, 10, 20, 6, 0, 0, 123, DateTimeKind.Utc);

        Assert.Equal(AnnouncementVersion.Of(created, null), AnnouncementVersion.Of(created.AddTicks(9_999), null));
        Assert.NotEqual(AnnouncementVersion.Of(created, null), AnnouncementVersion.Of(created, created.AddMilliseconds(1)));
        Assert.Equal(AnnouncementVersion.Of(created, created.AddSeconds(5)), AnnouncementVersion.Of(DateTime.MinValue, created.AddSeconds(5)));
    }

    private static AnnouncementFields Fields(string? message, string? linkUrl = null) =>
        new(message, linkUrl, SortOrder: 1, IsActive: true, StartsAt: null, EndsAt: null);
}
