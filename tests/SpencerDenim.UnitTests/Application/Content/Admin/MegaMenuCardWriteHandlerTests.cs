using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Application.Content.Admin.MegaMenu;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.UnitTests.Application.Content.Admin;

/// <summary>
/// The mega-menu card writes (GLB-05, ADM-17; #215): #192's field, link, media, row-version, audit and cache
/// rules applied to a new table, plus the two rules of its own — a switched-on card needs its picture, and a
/// department holds at most <see cref="MegaMenuPromoCardLimits.MaxCardsPerDepartment"/> cards.
/// </summary>
public sealed class MegaMenuCardWriteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);

    private readonly FakeMegaMenuCardAdminStore _store = new();
    private readonly RecordingAuditLogWriter _audit = new();
    private readonly RecordingHomePageCache _cache = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private MegaMenuCardWriteHandlers Handlers() => new(_store, _audit, _cache, new FixedClock(Now));

    [Fact]
    public async Task A_card_is_added_with_its_usage_row_an_audit_row_and_an_eviction()
    {
        var result = await Handlers().HandleAsync(new CreateMegaMenuCardCommand(Fields(), TestActors.Editor), Token);

        var saved = Assert.IsType<ContentWriteResult.Succeeded>(result);
        var card = Assert.Single(_store.Cards);
        Assert.Equal(saved.EntityId, card.Id);
        Assert.Equal("New in Women", card.Title);
        Assert.Equal("Shop now", card.ButtonLabel);
        Assert.Equal("/new-arrivals", card.LinkUrl);
        Assert.Equal(101, card.ImageAssetId);
        Assert.True(card.IsActive);
        Assert.NotNull(saved.RowVersion);

        Assert.Equal(MediaUsageOwnerTypes.MegaMenuPromoCard, _store.LastUsage!.OwnerType);
        Assert.Equal(card.Id, _store.LastUsageOwnerId);
        Assert.Equal([new ContentMediaUsage(101, MediaUsageRoles.Image)], _store.LastUsage.Desired);

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.Create, row.Action);
        Assert.Equal(MegaMenuCardWriteHandlers.EntityType, row.EntityType);
        Assert.Equal(1, _cache.Evictions);
    }

    [Fact]
    public async Task Text_is_stored_as_typed_trimmed_and_markup_is_not_interpreted()
    {
        var result = await Handlers().HandleAsync(
            new CreateMegaMenuCardCommand(
                Fields() with { Title = "  <b>Bold</b> & <script>x</script>  ", Subtitle = "\"quoted\" <i>", ButtonLabel = "Go >" },
                TestActors.Editor),
            Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        var card = Assert.Single(_store.Cards);
        Assert.Equal("<b>Bold</b> & <script>x</script>", card.Title);
        Assert.Equal("\"quoted\" <i>", card.Subtitle);
        Assert.Equal("Go >", card.ButtonLabel);
    }

    [Theory]
    [InlineData("title", null)]
    [InlineData("title", "  ")]
    [InlineData("title", "Line\nbreak")]
    [InlineData("buttonLabel", null)]
    [InlineData("buttonLabel", "Tab\there")]
    [InlineData("subtitle", "Line\rbreak")]
    [InlineData("linkUrl", null)]
    [InlineData("linkUrl", " ")]
    [InlineData("linkUrl", "javascript:alert(1)")]
    [InlineData("linkUrl", "//evil.example")]
    [InlineData("linkUrl", "/\\evil.example")]
    [InlineData("linkUrl", "https://user:pass@evil.example")]
    [InlineData("linkUrl", "data:text/html,x")]
    [InlineData("linkUrl", "women")]
    public async Task A_bad_value_is_a_field_error_and_nothing_is_saved(string field, string? value)
    {
        var fields = field switch
        {
            "title" => Fields() with { Title = value },
            "buttonLabel" => Fields() with { ButtonLabel = value },
            "subtitle" => Fields() with { Subtitle = value },
            _ => Fields() with { LinkUrl = value },
        };

        var result = await Handlers().HandleAsync(new CreateMegaMenuCardCommand(fields, TestActors.Editor), Token);

        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == field);
        Assert.Empty(_store.Cards);
        Assert.Equal(0, _store.Saves);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _cache.Evictions);
    }

    [Theory]
    [InlineData("/women/dresses?sort=newest")]
    [InlineData("https://instagram.com/colombowardrobe")]
    public async Task A_site_path_or_a_web_address_is_a_good_link(string link)
    {
        var result = await Handlers().HandleAsync(
            new CreateMegaMenuCardCommand(Fields() with { LinkUrl = link }, TestActors.Editor), Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.Equal(link, Assert.Single(_store.Cards).LinkUrl);
    }

    [Theory]
    [InlineData(MegaMenuPromoCardLimits.TitleMaxLength, true)]
    [InlineData(MegaMenuPromoCardLimits.TitleMaxLength + 1, false)]
    public async Task The_title_is_bounded_at_its_column(int length, bool ok)
    {
        var result = await Handlers().HandleAsync(
            new CreateMegaMenuCardCommand(Fields() with { Title = new string('a', length) }, TestActors.Editor), Token);

        Assert.Equal(ok, result is ContentWriteResult.Succeeded);
    }

    [Theory]
    [InlineData(MegaMenuPromoCardLimits.SubtitleMaxLength, true)]
    [InlineData(MegaMenuPromoCardLimits.SubtitleMaxLength + 1, false)]
    public async Task The_subtitle_is_bounded_at_its_column(int length, bool ok)
    {
        var result = await Handlers().HandleAsync(
            new CreateMegaMenuCardCommand(Fields() with { Subtitle = new string('a', length) }, TestActors.Editor), Token);

        Assert.Equal(ok, result is ContentWriteResult.Succeeded);
    }

    [Theory]
    [InlineData(ContentFieldLimits.ButtonLabelMaxLength, true)]
    [InlineData(ContentFieldLimits.ButtonLabelMaxLength + 1, false)]
    public async Task The_button_is_bounded_at_its_column(int length, bool ok)
    {
        var result = await Handlers().HandleAsync(
            new CreateMegaMenuCardCommand(Fields() with { ButtonLabel = new string('a', length) }, TestActors.Editor), Token);

        Assert.Equal(ok, result is ContentWriteResult.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(1000)]
    public async Task The_position_is_required_and_bounded(int? sortOrder)
    {
        var result = await Handlers().HandleAsync(
            new CreateMegaMenuCardCommand(Fields() with { SortOrder = sortOrder }, TestActors.Editor), Token);

        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == "sortOrder");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(99)]
    public async Task The_department_is_required_and_must_exist(int? departmentId)
    {
        var result = await Handlers().HandleAsync(
            new CreateMegaMenuCardCommand(Fields() with { DepartmentId = departmentId }, TestActors.Editor), Token);

        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == "departmentId");
    }

    [Fact]
    public async Task A_schedule_must_end_after_it_starts_and_is_stored_as_utc()
    {
        var start = new DateTime(2026, 11, 1, 9, 0, 0);

        var bad = await Handlers().HandleAsync(
            new CreateMegaMenuCardCommand(Fields() with { StartsAt = start, EndsAt = start }, TestActors.Editor), Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(bad).Errors, error => error.Field == "endsAt");

        var good = await Handlers().HandleAsync(
            new CreateMegaMenuCardCommand(Fields() with { StartsAt = start, EndsAt = start.AddMinutes(1) }, TestActors.Editor), Token);
        Assert.IsType<ContentWriteResult.Succeeded>(good);

        // Colombo is UTC+05:30: 09:00 there is 03:30 UTC.
        Assert.Equal(new DateTime(2026, 11, 1, 3, 30, 0, DateTimeKind.Utc), Assert.Single(_store.Cards).StartsAtUtc);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("noAlt")]
    [InlineData("processing")]
    [InlineData("private")]
    [InlineData("unknown")]
    public async Task The_image_must_be_a_ready_public_image_with_alt_text(string which)
    {
        var id = which switch
        {
            "video" => TestMedia.Film,
            "noAlt" => TestMedia.NoAlt,
            "processing" => TestMedia.Processing,
            "private" => TestMedia.PrivateSlip,
            _ => TestMedia.Unknown,
        };

        var result = await Handlers().HandleAsync(
            new CreateMegaMenuCardCommand(Fields() with { ImagePublicId = id }, TestActors.Editor), Token);

        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == "imagePublicId");
        Assert.Empty(_store.Cards);
    }

    [Fact]
    public async Task A_card_without_an_image_can_be_saved_switched_off_but_not_switched_on()
    {
        var on = await Handlers().HandleAsync(
            new CreateMegaMenuCardCommand(Fields() with { ImagePublicId = null, IsActive = true }, TestActors.Editor), Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(on).Errors, error => error.Field == "imagePublicId");
        Assert.Empty(_store.Cards);

        var off = await Handlers().HandleAsync(
            new CreateMegaMenuCardCommand(Fields() with { ImagePublicId = null, IsActive = false }, TestActors.Editor), Token);
        Assert.IsType<ContentWriteResult.Succeeded>(off);
        Assert.Null(Assert.Single(_store.Cards).ImageAssetId);
        Assert.Empty(_store.LastUsage!.Desired);
    }

    [Fact]
    public async Task A_department_holds_at_most_the_limit_and_the_next_is_a_conflict()
    {
        for (var index = 0; index < MegaMenuPromoCardLimits.MaxCardsPerDepartment; index++)
        {
            _store.AddExisting(departmentId: 1);
        }

        var result = await Handlers().HandleAsync(new CreateMegaMenuCardCommand(Fields(), TestActors.Editor), Token);

        Assert.Equal(ContentConflictCodes.TooManyMegaMenuCards, Assert.IsType<ContentWriteResult.Conflict>(result).Code);
        Assert.Equal(MegaMenuPromoCardLimits.MaxCardsPerDepartment, _store.Cards.Count);

        // Another department is not full.
        var other = await Handlers().HandleAsync(
            new CreateMegaMenuCardCommand(Fields() with { DepartmentId = 2 }, TestActors.Editor), Token);
        Assert.IsType<ContentWriteResult.Succeeded>(other);
    }

    [Fact]
    public async Task One_short_of_the_limit_a_card_is_still_added()
    {
        for (var index = 0; index < MegaMenuPromoCardLimits.MaxCardsPerDepartment - 1; index++)
        {
            _store.AddExisting(departmentId: 1);
        }

        var result = await Handlers().HandleAsync(new CreateMegaMenuCardCommand(Fields(), TestActors.Editor), Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
    }

    [Fact]
    public async Task Moving_a_card_into_a_full_department_is_refused_but_editing_in_place_is_not()
    {
        for (var index = 0; index < MegaMenuPromoCardLimits.MaxCardsPerDepartment; index++)
        {
            _store.AddExisting(departmentId: 1);
        }

        var inPlace = _store.Cards[0];
        var edited = await Handlers().HandleAsync(
            new UpdateMegaMenuCardCommand(inPlace.Id, Fields() with { Title = "Still Women" }, Version(inPlace), TestActors.Editor), Token);
        Assert.IsType<ContentWriteResult.Succeeded>(edited);

        var stranger = _store.AddExisting(departmentId: 2);
        var moved = await Handlers().HandleAsync(
            new UpdateMegaMenuCardCommand(stranger.Id, Fields() with { DepartmentId = 1 }, Version(stranger), TestActors.Editor), Token);
        Assert.Equal(ContentConflictCodes.TooManyMegaMenuCards, Assert.IsType<ContentWriteResult.Conflict>(moved).Code);
        Assert.Equal(2, stranger.DepartmentId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAAAAAAAAI=")]
    public async Task An_edit_needs_the_current_row_version(string? version)
    {
        var card = _store.AddExisting(departmentId: 1);

        var result = await Handlers().HandleAsync(
            new UpdateMegaMenuCardCommand(card.Id, Fields() with { Title = "Renamed" }, version, TestActors.Editor), Token);

        Assert.Equal(ContentConflictCodes.StaleRowVersion, Assert.IsType<ContentWriteResult.Conflict>(result).Code);
        Assert.Equal("Existing", card.Title);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task A_save_that_loses_the_race_is_a_stale_conflict_and_does_not_evict()
    {
        var card = _store.AddExisting(departmentId: 1);
        _store.NextOutcome = ContentSaveOutcome.ConcurrencyConflict;

        var result = await Handlers().HandleAsync(
            new UpdateMegaMenuCardCommand(card.Id, Fields() with { Title = "Renamed" }, Version(card), TestActors.Editor), Token);

        Assert.Equal(ContentConflictCodes.StaleRowVersion, Assert.IsType<ContentWriteResult.Conflict>(result).Code);
        Assert.Equal(0, _cache.Evictions);
    }

    [Fact]
    public async Task An_edit_audits_the_fields_that_changed_and_moves_the_row_version()
    {
        var card = _store.AddExisting(departmentId: 1);
        var before = Version(card);

        var result = await Handlers().HandleAsync(
            new UpdateMegaMenuCardCommand(card.Id, Fields() with { Title = "Renamed" }, before, TestActors.Editor), Token);

        var saved = Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.NotEqual(before, saved.RowVersion);
        var row = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.Update, row.Action);
        Assert.Contains("Renamed", row.AfterJson, StringComparison.Ordinal);
        Assert.Contains("Existing", row.BeforeJson, StringComparison.Ordinal);
        Assert.Equal(1, _cache.Evictions);
    }

    [Fact]
    public async Task An_untouched_resave_writes_nothing_and_audits_nothing()
    {
        var card = _store.AddExisting(departmentId: 1);
        var same = new MegaMenuCardFields(
            card.DepartmentId, card.Title, card.Subtitle, card.ButtonLabel, card.LinkUrl, TestMedia.Photo, card.SortOrder, card.IsActive, null, null);

        var result = await Handlers().HandleAsync(new UpdateMegaMenuCardCommand(card.Id, same, Version(card), TestActors.Editor), Token);

        Assert.IsType<ContentWriteResult.Unchanged>(result);
        Assert.Equal(0, _store.Saves);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _cache.Evictions);
    }

    /// <summary>
    /// Tester pass (#215): a change to any one field is a change — saved, audited by name — never an "untouched
    /// resave". With a field missing from the audit snapshot, changing only that field was silently dropped.
    /// </summary>
    [Theory]
    [InlineData("departmentId")]
    [InlineData("title")]
    [InlineData("subtitle")]
    [InlineData("buttonLabel")]
    [InlineData("linkUrl")]
    [InlineData("imageAssetId")]
    [InlineData("sortOrder")]
    [InlineData("isActive")]
    [InlineData("startsAtUtc")]
    [InlineData("endsAtUtc")]
    public async Task Changing_only_one_field_is_saved_and_audited_by_name(string field)
    {
        var card = _store.AddExisting(departmentId: 1);
        var same = new MegaMenuCardFields(
            card.DepartmentId, card.Title, card.Subtitle, card.ButtonLabel, card.LinkUrl, TestMedia.Photo, card.SortOrder, card.IsActive, null, null);

        var changed = field switch
        {
            "departmentId" => same with { DepartmentId = 2 },
            "title" => same with { Title = "Renamed" },
            "subtitle" => same with { Subtitle = "Another line" },
            "buttonLabel" => same with { ButtonLabel = "Shop the edit" },
            "linkUrl" => same with { LinkUrl = "/women" },
            "imageAssetId" => same with { ImagePublicId = TestMedia.OtherPhoto },
            "sortOrder" => same with { SortOrder = 7 },
            "isActive" => same with { IsActive = false },
            "startsAtUtc" => same with { StartsAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Unspecified) },
            "endsAtUtc" => same with { EndsAt = new DateTime(2026, 12, 1, 9, 0, 0, DateTimeKind.Unspecified) },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var result = await Handlers().HandleAsync(new UpdateMegaMenuCardCommand(card.Id, changed, Version(card), TestActors.Editor), Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.Equal(1, _store.Saves);
        var row = Assert.Single(_audit.Entries);
        Assert.Contains(field, row.AfterJson, StringComparison.Ordinal);
        Assert.Equal(1, _cache.Evictions);
    }

    [Fact]
    public async Task A_create_is_audited_with_what_was_added()
    {
        await Handlers().HandleAsync(new CreateMegaMenuCardCommand(Fields() with { Title = "Linen days" }, TestActors.Editor), Token);

        var row = Assert.Single(_audit.Entries);
        Assert.Contains("Linen days", row.AfterJson, StringComparison.Ordinal);
        Assert.Contains("/new-arrivals", row.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_picture_rewrites_the_usage_rows_to_the_new_one()
    {
        var card = _store.AddExisting(departmentId: 1);

        var result = await Handlers().HandleAsync(
            new UpdateMegaMenuCardCommand(card.Id, Fields() with { ImagePublicId = TestMedia.OtherPhoto }, Version(card), TestActors.Editor), Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.Equal(102, card.ImageAssetId);
        Assert.Equal([new ContentMediaUsage(102, MediaUsageRoles.Image)], _store.LastUsage!.Desired);
    }

    [Fact]
    public async Task A_missing_card_is_not_found()
    {
        var update = await Handlers().HandleAsync(new UpdateMegaMenuCardCommand(404, Fields(), "AAAAAAAAAAE=", TestActors.Editor), Token);
        var delete = await Handlers().HandleAsync(new DeleteMegaMenuCardCommand(404, "AAAAAAAAAAE=", TestActors.Editor), Token);

        Assert.IsType<ContentWriteResult.NotFound>(update);
        Assert.IsType<ContentWriteResult.NotFound>(delete);
    }

    [Fact]
    public async Task A_delete_needs_the_row_version_then_removes_the_card_its_usage_rows_and_evicts()
    {
        var card = _store.AddExisting(departmentId: 1);

        var stale = await Handlers().HandleAsync(new DeleteMegaMenuCardCommand(card.Id, "AAAAAAAAAAI=", TestActors.Editor), Token);
        Assert.Equal(ContentConflictCodes.StaleRowVersion, Assert.IsType<ContentWriteResult.Conflict>(stale).Code);
        Assert.Single(_store.Cards);

        var result = await Handlers().HandleAsync(new DeleteMegaMenuCardCommand(card.Id, Version(card), TestActors.Editor), Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.Empty(_store.Cards);
        Assert.Equal(MediaUsageOwnerTypes.MegaMenuPromoCard, _store.LastUsage!.OwnerType);
        Assert.Equal(card.Id, _store.LastUsageOwnerId);
        Assert.Empty(_store.LastUsage.Desired);
        Assert.Equal(AuditActions.Delete, Assert.Single(_audit.Entries).Action);
        Assert.Equal(1, _cache.Evictions);
    }

    [Fact]
    public async Task A_delete_that_loses_the_race_is_a_stale_conflict_and_does_not_evict()
    {
        var card = _store.AddExisting(departmentId: 1);
        _store.NextOutcome = ContentSaveOutcome.ConcurrencyConflict;

        var result = await Handlers().HandleAsync(new DeleteMegaMenuCardCommand(card.Id, Version(card), TestActors.Editor), Token);

        Assert.Equal(ContentConflictCodes.StaleRowVersion, Assert.IsType<ContentWriteResult.Conflict>(result).Code);
        Assert.Equal(0, _cache.Evictions);
    }

    /// <summary>
    /// Tester M2: a create, and only a move, asks the store to hold the department's limit under its lock; an edit
    /// in place asks nothing (it cannot add a card to its department).
    /// </summary>
    [Fact]
    public async Task A_create_and_a_move_ask_the_store_to_hold_the_limit_and_an_edit_in_place_does_not()
    {
        await Handlers().HandleAsync(new CreateMegaMenuCardCommand(Fields(), TestActors.Editor), Token);
        Assert.Equal(new MegaMenuCardLimit(1, MegaMenuPromoCardLimits.MaxCardsPerDepartment, null), _store.LastLimit);

        var card = _store.Cards.Single();
        await Handlers().HandleAsync(
            new UpdateMegaMenuCardCommand(card.Id, Fields() with { Title = "Renamed" }, Version(card), TestActors.Editor), Token);
        Assert.Null(_store.LastLimit);

        await Handlers().HandleAsync(
            new UpdateMegaMenuCardCommand(card.Id, Fields() with { DepartmentId = 2 }, Version(card), TestActors.Editor), Token);
        Assert.Equal(new MegaMenuCardLimit(2, MegaMenuPromoCardLimits.MaxCardsPerDepartment, card.Id), _store.LastLimit);
    }

    [Fact]
    public async Task A_limit_reached_under_the_lock_is_the_too_many_conflict_and_does_not_evict()
    {
        _store.NextOutcome = ContentSaveOutcome.LimitReached;

        var result = await Handlers().HandleAsync(new CreateMegaMenuCardCommand(Fields(), TestActors.Editor), Token);

        Assert.Equal(ContentConflictCodes.TooManyMegaMenuCards, Assert.IsType<ContentWriteResult.Conflict>(result).Code);
        Assert.Equal(0, _cache.Evictions);
    }

    [Fact]
    public async Task A_picture_gone_by_the_time_of_the_save_is_a_field_error_and_does_not_evict()
    {
        _store.NextOutcome = ContentSaveOutcome.MediaUnavailable;

        var result = await Handlers().HandleAsync(new CreateMegaMenuCardCommand(Fields(), TestActors.Editor), Token);

        var error = Assert.Single(Assert.IsType<ContentWriteResult.Invalid>(result).Errors);
        Assert.Equal("imagePublicId", error.Field);
        Assert.Equal(0, _cache.Evictions);
    }

    /// <summary>The locked re-check names each slot by the request field that carries it (homepage items have three).</summary>
    [Fact]
    public void An_unavailable_usage_is_reported_against_its_own_field()
    {
        var errors = ContentMediaRules.UnavailableErrors(
        [
            new ContentMediaUsage(1, MediaUsageRoles.Banner),
            new ContentMediaUsage(2, MediaUsageRoles.Image),
            new ContentMediaUsage(3, MediaUsageRoles.MobileImage),
            new ContentMediaUsage(4, MediaUsageRoles.Video),
        ]);

        Assert.Equal(["imagePublicId", "mobileImagePublicId", "videoPublicId"], errors.Select(error => error.Field));
    }

    private static string Version(MegaMenuPromoCard card) => Convert.ToBase64String(card.RowVersion!);

    private static MegaMenuCardFields Fields() =>
        new(
            DepartmentId: 1,
            Title: "New in Women",
            Subtitle: "Everyday essentials",
            ButtonLabel: "Shop now",
            LinkUrl: "/new-arrivals",
            ImagePublicId: TestMedia.Photo,
            SortOrder: 0,
            IsActive: true,
            StartsAt: null,
            EndsAt: null);
}

/// <summary>An in-memory mega-menu card store.</summary>
internal sealed class FakeMegaMenuCardAdminStore : IMegaMenuCardAdminStore
{
    private int _nextId = 1;
    private byte _version = 1;

    public List<MegaMenuPromoCard> Cards { get; } = [];

    public HashSet<int> Departments { get; } = [1, 2, 3];

    public int Saves { get; private set; }

    public ContentMediaUsageChange? LastUsage { get; private set; }

    public int? LastUsageOwnerId { get; private set; }

    public ContentSaveOutcome NextOutcome { get; set; } = ContentSaveOutcome.Saved;

    public MegaMenuPromoCard AddExisting(int departmentId)
    {
        var card = new MegaMenuPromoCard
        {
            Id = _nextId++,
            DepartmentId = departmentId,
            Title = "Existing",
            Subtitle = "Everyday essentials",
            ButtonLabel = "Shop now",
            LinkUrl = "/new-arrivals",
            ImageAssetId = 101,
            SortOrder = 0,
            IsActive = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, _version++],
        };
        Cards.Add(card);
        return card;
    }

    public Task<MegaMenuPromoCard?> FindAsync(int cardId, CancellationToken cancellationToken) =>
        Task.FromResult(Cards.FirstOrDefault(card => card.Id == cardId));

    public Task<bool> DepartmentExistsAsync(int departmentId, CancellationToken cancellationToken) =>
        Task.FromResult(Departments.Contains(departmentId));

    public Task<int> CountForDepartmentAsync(int departmentId, CancellationToken cancellationToken) =>
        Task.FromResult(Cards.Count(card => card.DepartmentId == departmentId));

    public Task<ContentMediaAsset?> FindMediaAsync(Guid publicId, CancellationToken cancellationToken) =>
        Task.FromResult(TestMedia.Library.GetValueOrDefault(publicId));

    public void Add(MegaMenuPromoCard card) => Cards.Add(card);

    public void Remove(MegaMenuPromoCard card) => Cards.Remove(card);

    /// <summary>The limit the last save was asked to hold under the department's lock.</summary>
    public MegaMenuCardLimit? LastLimit { get; private set; }

    public Task<ContentSaveOutcome> SaveAsync(
        int? actingUserId,
        ContentMediaUsageChange? usage,
        MegaMenuCardLimit? limit,
        CancellationToken cancellationToken)
    {
        Saves++;
        LastLimit = limit;

        if (NextOutcome != ContentSaveOutcome.Saved)
        {
            if (NextOutcome == ContentSaveOutcome.MediaUnavailable && usage is not null)
            {
                usage.Unavailable = [.. usage.Desired];
            }

            return Task.FromResult(NextOutcome);
        }

        foreach (var card in Cards)
        {
            if (card.Id == 0)
            {
                card.Id = _nextId++;
            }

            card.RowVersion = [9, 9, 9, 9, 9, 9, (byte)Saves, (byte)card.Id];
        }

        LastUsage = usage;
        LastUsageOwnerId = usage?.OwnerId();

        return Task.FromResult(ContentSaveOutcome.Saved);
    }
}
