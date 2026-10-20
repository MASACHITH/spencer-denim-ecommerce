using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Application.Content.Admin.SizeGuides;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Content;

namespace SpencerDenim.UnitTests.Application.Content.Admin;

/// <summary>
/// The size-guide writes (PDP-04, ADM-17; #192) and issue #70's second half: the two HTML fields the
/// PDP-04 modal renders as markup go through the allow-list sanitiser on save.
/// </summary>
public sealed class SizeGuideWriteHandlerTests
{
    private const string Table = """{"columns":["Size","Chest (cm)"],"rows":[["S","86-91"],["M","91-96"]]}""";

    private static readonly DateTimeOffset Now = new(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);

    private readonly FakeSizeGuideAdminStore _store = new();
    private readonly RecordingAuditLogWriter _audit = new();
    private readonly RecordingHomePageCache _cache = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SizeGuideWriteHandlers Handlers() => new(_store, new HtmlSanitiser(), _audit, _cache, new FixedClock(Now));

    [Fact]
    public async Task Script_and_handlers_are_cleaned_out_of_both_html_fields_and_the_editor_is_told()
    {
        var result = await Handlers().HandleAsync(
            new CreateSizeGuideCommand(
                Fields(
                    intro: """<p onclick="steal()">Measure <script>alert(1)</script>yourself.</p>""",
                    tips: """<p>Use a tape.</p><iframe src="https://evil.example"></iframe><a href="javascript:x()">x</a>"""),
                TestActors.Editor),
            Token);

        var saved = Assert.IsType<ContentWriteResult.Succeeded>(result);
        var guide = Assert.Single(_store.Guides);

        Assert.DoesNotContain("script", guide.IntroHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", guide.IntroHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Measure", guide.IntroHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("iframe", guide.MeasuringTipsHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript", guide.MeasuringTipsHtml, StringComparison.OrdinalIgnoreCase);

        Assert.NotEmpty(saved.RemovedFromBody);
        Assert.NotEmpty(saved.RemovedFromSecondary!);
        Assert.Equal(1, _cache.Evictions);
    }

    [Fact]
    public async Task The_table_is_stored_canonically_and_a_bad_one_is_a_field_error()
    {
        var good = await Handlers().HandleAsync(
            new CreateSizeGuideCommand(Fields(table: """{"columns":["Size"],"rows":[[8]]}"""), TestActors.Editor), Token);
        Assert.IsType<ContentWriteResult.Succeeded>(good);
        Assert.Equal("""{"columns":["Size"],"rows":[["8"]]}""", _store.Guides.Single().TableJson);

        var bad = await Handlers().HandleAsync(
            new CreateSizeGuideCommand(Fields(table: """{"columns":["Size"],"rows":[["S","extra"]]}"""), TestActors.Editor), Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(bad).Errors, error => error.Field == "tableJson");
    }

    [Fact]
    public async Task Markup_too_deep_to_parse_safely_is_refused_on_its_field()
    {
        var deep = string.Concat(Enumerable.Repeat("<div>", HtmlSanitiser.MaxNestingDepth + 5));

        var result = await Handlers().HandleAsync(new CreateSizeGuideCommand(Fields(tips: deep), TestActors.Editor), Token);

        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == "measuringTipsHtml");
        Assert.Empty(_store.Guides);
    }

    [Fact]
    public async Task Html_longer_than_the_limit_after_cleaning_is_refused()
    {
        var at = "<p>" + new string('a', SizeGuideWriteHandlers.MaxHtmlLength - 7) + "</p>";
        var over = "<p>" + new string('a', SizeGuideWriteHandlers.MaxHtmlLength) + "</p>";

        Assert.IsType<ContentWriteResult.Succeeded>(
            await Handlers().HandleAsync(new CreateSizeGuideCommand(Fields(intro: at), TestActors.Editor), Token));

        var result = await Handlers().HandleAsync(new CreateSizeGuideCommand(Fields(intro: over), TestActors.Editor), Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == "introHtml");
    }

    [Fact]
    public async Task An_image_in_the_html_needs_alt_text()
    {
        var result = await Handlers().HandleAsync(
            new CreateSizeGuideCommand(Fields(intro: """<p><img src="/media/a.webp"></p>"""), TestActors.Editor), Token);

        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == "introHtml");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task A_guide_needs_a_name(string? name)
    {
        var result = await Handlers().HandleAsync(new CreateSizeGuideCommand(Fields(name: name), TestActors.Editor), Token);

        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == "name");
    }

    [Fact]
    public async Task The_image_is_a_ready_public_image_and_lands_in_media_usages()
    {
        var bad = await Handlers().HandleAsync(
            new CreateSizeGuideCommand(Fields() with { ImagePublicId = TestMedia.Film }, TestActors.Editor), Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(bad).Errors, error => error.Field == "imagePublicId");

        var good = await Handlers().HandleAsync(
            new CreateSizeGuideCommand(Fields() with { ImagePublicId = TestMedia.Photo }, TestActors.Editor), Token);
        var saved = Assert.IsType<ContentWriteResult.Succeeded>(good);

        Assert.Equal(MediaUsageOwnerTypes.SizeGuide, _store.LastUsage!.OwnerType);
        Assert.Equal(saved.EntityId, _store.LastUsageOwnerId);
        Assert.Equal([new ContentMediaUsage(101, MediaUsageRoles.Image)], _store.LastUsage.Desired);
    }

    /// <summary>
    /// The audit row says what changed and how big it is, never the HTML itself; the table reads as its
    /// shape. Compared on the real values, so two different tables of the same shape still count.
    /// </summary>
    [Fact]
    public async Task The_audit_row_describes_the_html_and_the_table_rather_than_copying_them()
    {
        var guide = _store.AddExisting("Women's tops");
        guide.TableJson = """{"columns":["Size","Chest (cm)"],"rows":[["S","80"],["M","85"]]}""";

        var result = await Handlers().HandleAsync(
            new UpdateSizeGuideCommand(guide.Id, Fields(name: "Women's tops", intro: "<p>Secret-ish copy</p>"), Version(guide), TestActors.Editor),
            Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        var row = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.Update, row.Action);
        Assert.DoesNotContain("Secret-ish", row.AfterJson, StringComparison.Ordinal);
        Assert.Contains("characters of sanitised HTML", row.AfterJson, StringComparison.Ordinal);
        Assert.Contains("2 columns ", row.AfterJson, StringComparison.Ordinal);
        Assert.Contains(" 2 rows", row.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_needs_the_current_row_version()
    {
        var guide = _store.AddExisting("Men's shirts");

        var result = await Handlers().HandleAsync(
            new UpdateSizeGuideCommand(guide.Id, Fields(name: "Renamed"), "AAAAAAAAAAI=", TestActors.Editor), Token);

        Assert.Equal(ContentConflictCodes.StaleRowVersion, Assert.IsType<ContentWriteResult.Conflict>(result).Code);
        Assert.Equal("Men's shirts", guide.Name);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task A_guide_in_use_is_not_removed_and_the_answer_says_by_what(int categories, int products)
    {
        var guide = _store.AddExisting("Kids");
        _store.Usage[guide.Id] = (categories, products);

        var result = await Handlers().HandleAsync(new DeleteSizeGuideCommand(guide.Id, Version(guide), TestActors.Editor), Token);

        var conflict = Assert.IsType<ContentWriteResult.Conflict>(result);
        Assert.Equal(ContentConflictCodes.SizeGuideInUse, conflict.Code);
        Assert.Equal(categories, conflict.Details!["categoryCount"]);
        Assert.Equal(products, conflict.Details["productCount"]);
        Assert.False(guide.IsDeleted);
    }

    [Fact]
    public async Task An_unused_guide_is_soft_deleted_audited_and_its_usage_rows_go()
    {
        var guide = _store.AddExisting("Old chart");
        guide.ImageAssetId = 101;

        var result = await Handlers().HandleAsync(new DeleteSizeGuideCommand(guide.Id, Version(guide), TestActors.Editor), Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.True(guide.IsDeleted);
        Assert.Equal(AuditActions.SoftDelete, Assert.Single(_audit.Entries).Action);
        Assert.Empty(_store.LastUsage!.Desired);
        Assert.Equal(1, _cache.Evictions);
    }

    private static string Version(SpencerDenim.Domain.Catalog.SizeGuide guide) => Convert.ToBase64String(guide.RowVersion!);

    private static SizeGuideFields Fields(string? name = "Women's dresses", string? intro = null, string? tips = null, string table = Table) =>
        new(name, intro, table, tips, ImagePublicId: null);
}
