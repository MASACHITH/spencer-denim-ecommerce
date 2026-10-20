using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Content;
using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Content.Admin.Home;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.UnitTests.Application.Content.Admin;

/// <summary>
/// Independent tester's additions for #192 (ADM-17): hostile link and text shapes, the block-rule table
/// pinned cell by cell, and every unused field refused <b>by name</b> on every block — the parts of the
/// write path a mutation of one table entry or one <c>RefuseUnused</c> call would otherwise slip past.
/// </summary>
public sealed class ContentWriteAttackTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ================================================================== links

    /// <summary>Shapes a browser or the router could turn into an off-site or script navigation.</summary>
    [Theory]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData(" javascript:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("//evil.example")]
    [InlineData("///evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("/\r\n//evil.example")]
    [InlineData("/\n/evil.example")]
    [InlineData("/{U+2028}/evil.example")]
    [InlineData("/{U+00A0}/evil.example")]
    [InlineData("/\u0085/evil.example")]
    [InlineData("{U+FF0F}{U+FF0F}evil.example")]
    [InlineData("https://shop.example/women")]
    [InlineData("evil.example")]
    public void A_hostile_shape_is_never_a_site_path(string value)
    {
        value = Decode(value);
        Assert.False(ContentFieldRules.IsSitePath(value), $"'{value}' was accepted as a site path.");

        var errors = new List<ValidationError>();
        Assert.Null(ContentFieldRules.OptionalLink(errors, "linkUrl", value, mayLeaveSite: false));
        Assert.Contains(errors, error => error.Field == "linkUrl");
    }

    [Theory]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://")]
    [InlineData("https://good.example\\@evil.example")]
    [InlineData("https://good.example/\r\nSet-Cookie:x")]
    [InlineData("https://good.example/{U+2028}x")]
    [InlineData("https://user@evil.example")]
    [InlineData("//evil.example")]
    public void A_hostile_shape_is_never_a_web_address(string value)
    {
        value = Decode(value);
        Assert.False(ContentFieldRules.IsWebAddress(value), $"'{value}' was accepted as a web address.");

        var errors = new List<ValidationError>();
        Assert.Null(ContentFieldRules.OptionalLink(errors, "linkUrl", value, mayLeaveSite: true));
        Assert.Single(errors);
    }

    [Fact]
    public void A_full_address_is_refused_where_the_block_keeps_shoppers_on_the_site()
    {
        var errors = new List<ValidationError>();

        Assert.Null(ContentFieldRules.OptionalLink(errors, "linkUrl", "https://instagram.com/x", mayLeaveSite: false));
        Assert.Equal("linkUrl", Assert.Single(errors).Field);

        errors.Clear();
        Assert.Equal(
            "https://instagram.com/x",
            ContentFieldRules.OptionalLink(errors, "linkUrl", "  https://instagram.com/x  ", mayLeaveSite: true));
        Assert.Empty(errors);
    }

    [Fact]
    public void A_link_is_bounded_at_exactly_its_column_width()
    {
        var atLimit = "/" + new string('a', ContentFieldLimits.LinkUrlMaxLength - 1);
        var errors = new List<ValidationError>();

        Assert.Equal(atLimit, ContentFieldRules.OptionalLink(errors, "linkUrl", atLimit, mayLeaveSite: false));
        Assert.Empty(errors);

        Assert.Null(ContentFieldRules.OptionalLink(errors, "linkUrl", atLimit + "a", mayLeaveSite: false));
        Assert.Contains("500", Assert.Single(errors).Message, StringComparison.Ordinal);
    }

    // ================================================================== text

    [Theory]
    [InlineData("Line one\nline two")]
    [InlineData("Tab\there")]
    [InlineData("NUL\0")]
    [InlineData("Next\u0085line")]
    [InlineData("Escape\u001b[31m")]
    public void A_control_character_is_refused_in_plain_text(string value)
    {
        value = Decode(value);
        var errors = new List<ValidationError>();

        Assert.Null(ContentFieldRules.OptionalText(errors, "title", value, 150));
        Assert.Equal("title", Assert.Single(errors).Field);
    }

    /// <summary>
    /// Markup is not refused — it is stored as typed and escaped where shown — and Sinhala, Tamil and a bidi
    /// mark (format characters, not control characters) are ordinary copy.
    /// </summary>
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("ශ්‍රී ලංකා")]
    [InlineData("இலங்கை")]
    [InlineData("Sale {U+202E}ends{U+202C} today")]
    public void Markup_and_non_latin_copy_are_stored_as_typed(string value)
    {
        value = Decode(value);
        var errors = new List<ValidationError>();

        Assert.Equal(value, ContentFieldRules.OptionalText(errors, "title", value, 150));
        Assert.Empty(errors);
    }

    [Fact]
    public void Text_is_bounded_after_trimming_at_exactly_its_limit()
    {
        var errors = new List<ValidationError>();

        Assert.Equal(new string('a', 40), ContentFieldRules.OptionalText(errors, "buttonLabel", "  " + new string('a', 40) + "  ", 40));
        Assert.Empty(errors);

        Assert.Null(ContentFieldRules.OptionalText(errors, "buttonLabel", new string('a', 41), 40));
        Assert.Single(errors);
    }

    // ================================================================== the block-rule table, pinned

    /// <summary>
    /// Every cell of the table, written out. The storefront draws exactly these, and the admin screen builds
    /// its form from them: one field added to or dropped from a block here is a silent change on both.
    /// </summary>
    public static TheoryData<HomeSectionType, string[], string[], string[], int, bool> Table => new()
    {
        {
            HomeSectionType.HeroVideo,
            ["title", "subtitle", "overlay"],
            ["title", "subtitle", "buttonLabel", "linkUrl", "image", "mobileImage", "video"],
            [], 1, false
        },
        {
            HomeSectionType.ShopByDepartment,
            ["title", "subtitle"],
            ["title", "buttonLabel", "linkUrl", "image", "mobileImage", "department"],
            ["department"], 6, false
        },
        {
            HomeSectionType.NewArrivals,
            ["title", "subtitle", "count", "listingUrl", "linkLabel"],
            [], [], 0, false
        },
        {
            HomeSectionType.TrendingItems,
            ["title", "subtitle", "count", "listingUrl", "linkLabel", "mode"],
            ["title", "subtitle", "linkUrl", "image", "product"],
            ["title"], 12, false
        },
        {
            HomeSectionType.CampaignBanner,
            ["title", "subtitle", "overlay"],
            ["title", "subtitle", "body", "buttonLabel", "linkUrl", "image", "mobileImage"],
            [], 1, false
        },
        {
            HomeSectionType.ActivewearFeature,
            ["title", "subtitle", "overlay"],
            ["title", "subtitle", "body", "buttonLabel", "linkUrl", "image", "mobileImage"],
            [], 1, false
        },
        {
            HomeSectionType.SaleSection,
            ["title", "subtitle", "count", "listingUrl", "linkLabel"],
            [], [], 0, false
        },
        {
            HomeSectionType.SocialGallery,
            ["title", "subtitle"],
            ["title", "linkUrl", "image"],
            ["image"], 12, true
        },
        {
            HomeSectionType.Benefits,
            ["title", "subtitle"],
            ["title", "body", "iconKey"],
            ["iconKey"], 6, false
        },
        {
            HomeSectionType.Newsletter,
            ["title", "subtitle"],
            [], [], 0, false
        },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Each_block_uses_exactly_these_fields(
        HomeSectionType type,
        string[] sectionFields,
        string[] itemFields,
        string[] required,
        int maxItems,
        bool linkMayLeaveSite)
    {
        var rule = HomeBlockRules.For(type);

        Assert.Equal(sectionFields.Order(StringComparer.Ordinal), rule.SectionFields.Order(StringComparer.Ordinal));
        Assert.Equal(itemFields.Order(StringComparer.Ordinal), rule.ItemFields.Order(StringComparer.Ordinal));
        Assert.Equal(required.Order(StringComparer.Ordinal), rule.RequiredItemFields.Order(StringComparer.Ordinal));
        Assert.Equal(maxItems, rule.MaxItems);
        Assert.Equal(linkMayLeaveSite, rule.LinkMayLeaveSite);
    }

    // ================================================================== unused fields refused by name

    private static readonly string[] AllItemFields =
    [
        "title", "subtitle", "body", "buttonLabel", "linkUrl", "image", "mobileImage", "video", "department", "product", "iconKey",
    ];

    public static TheoryData<HomeSectionType, string> UnusedItemFields()
    {
        var data = new TheoryData<HomeSectionType, string>();

        foreach (var type in Enum.GetValues<HomeSectionType>())
        {
            var rule = HomeBlockRules.For(type);
            if (rule.MaxItems == 0)
            {
                continue;
            }

            foreach (var field in AllItemFields.Where(field => !rule.UsesItemField(field)))
            {
                data.Add(type, field);
            }
        }

        return data;
    }

    /// <summary>A value in a field the block does not draw is refused, and the error names that field.</summary>
    [Theory]
    [MemberData(nameof(UnusedItemFields))]
    public async Task An_item_field_the_block_does_not_use_is_refused_by_name(HomeSectionType type, string field)
    {
        var store = new FakeHomeContentAdminStore();
        var cache = new RecordingHomePageCache();
        var handlers = new HomeContentWriteHandlers(store, new RecordingAuditLogWriter(), cache, new CatalogOptions(), new FixedClock(Now));

        var fields = BaseItem() switch
        {
            var b when field == "title" => b with { Title = "x" },
            var b when field == "subtitle" => b with { Subtitle = "x" },
            var b when field == "body" => b with { Body = "x" },
            var b when field == "buttonLabel" => b with { ButtonLabel = "x" },
            var b when field == "linkUrl" => b with { LinkUrl = "/women" },
            var b when field == "image" => b with { ImagePublicId = TestMedia.Photo },
            var b when field == "mobileImage" => b with { MobileImagePublicId = TestMedia.Photo },
            var b when field == "video" => b with { VideoPublicId = TestMedia.Film, ImagePublicId = null },
            var b when field == "department" => b with { DepartmentId = 1 },
            var b when field == "product" => b with { ProductSlug = "linen-shirt" },
            var b when field == "iconKey" => b with { IconKey = "delivery" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var result = await handlers.HandleAsync(
            new CreateHomeSectionItemCommand((int)type, fields, store.RowVersionOf(type), TestActors.Editor), Token);

        var invalid = Assert.IsType<ContentWriteResult.Invalid>(result);
        var requestField = RequestFieldOf(field);
        Assert.Contains(
            invalid.Errors,
            error => error.Field == requestField && error.Message.Contains("does not use", StringComparison.Ordinal));
        Assert.Equal(0, store.Saves);
        Assert.Equal(0, cache.Evictions);
    }

    public static TheoryData<HomeSectionType, string> UnusedSectionFields()
    {
        var data = new TheoryData<HomeSectionType, string>();
        string[] all = ["subtitle", "mode", "count", "listingUrl", "linkLabel", "overlay"];

        foreach (var type in Enum.GetValues<HomeSectionType>())
        {
            foreach (var field in all.Where(field => !HomeBlockRules.For(type).UsesSectionField(field)))
            {
                data.Add(type, field);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(UnusedSectionFields))]
    public async Task A_section_field_the_block_does_not_use_is_refused_by_name(HomeSectionType type, string field)
    {
        var store = new FakeHomeContentAdminStore();
        var handlers = new HomeContentWriteHandlers(
            store, new RecordingAuditLogWriter(), new RecordingHomePageCache(), new CatalogOptions(), new FixedClock(Now));

        var command = new UpdateHomeSectionCommand(
            (int)type,
            "Heading",
            Subtitle: null,
            IsEnabled: true,
            SortOrder: 1,
            StartsAt: null,
            EndsAt: null,
            Mode: type == HomeSectionType.TrendingItems ? HomeSectionMode.Auto : null,
            Count: HomeBlockRules.IsProductRow(type) ? 8 : null,
            ListingUrl: HomeBlockRules.IsProductRow(type) ? "/new-arrivals" : null,
            LinkLabel: null,
            Overlay: null,
            RowVersion: store.RowVersionOf(type),
            Actor: TestActors.Editor);

        command = field switch
        {
            "subtitle" => command with { Subtitle = "x" },
            "mode" => command with { Mode = HomeSectionMode.Manual },
            "count" => command with { Count = 4 },
            "listingUrl" => command with { ListingUrl = "/women" },
            "linkLabel" => command with { LinkLabel = "View all" },
            "overlay" => command with { Overlay = "dark" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var result = await handlers.HandleAsync(command, Token);

        var invalid = Assert.IsType<ContentWriteResult.Invalid>(result);
        Assert.Contains(invalid.Errors, error => error.Field == field);
        Assert.Equal(0, store.Saves);
    }

    /// <summary>The overlay is one of the three values the storefront reads, in any case; anything else is refused.</summary>
    [Theory]
    [InlineData("purple", null)]
    [InlineData("dark; background:url(x)", null)]
    [InlineData("DARK", "dark")]
    [InlineData(" light ", "light")]
    [InlineData("none", "none")]
    public async Task An_overlay_is_one_of_the_three_the_storefront_draws(string overlay, string? stored)
    {
        var store = new FakeHomeContentAdminStore();
        var handlers = new HomeContentWriteHandlers(
            store, new RecordingAuditLogWriter(), new RecordingHomePageCache(), new CatalogOptions(), new FixedClock(Now));

        var result = await handlers.HandleAsync(
            new UpdateHomeSectionCommand(
                (int)HomeSectionType.HeroVideo,
                "Hero",
                Subtitle: null,
                IsEnabled: true,
                SortOrder: 1,
                StartsAt: null,
                EndsAt: null,
                Mode: null,
                Count: null,
                ListingUrl: null,
                LinkLabel: null,
                Overlay: overlay,
                RowVersion: store.RowVersionOf(HomeSectionType.HeroVideo),
                Actor: TestActors.Editor),
            Token);

        if (stored is null)
        {
            var invalid = Assert.IsType<ContentWriteResult.Invalid>(result);
            Assert.Equal("overlay", Assert.Single(invalid.Errors).Field);
            Assert.Equal(0, store.Saves);
        }
        else
        {
            Assert.IsType<ContentWriteResult.Succeeded>(result);
            Assert.Equal(stored, HomeSectionSettings.Parse(store.Section(HomeSectionType.HeroVideo).SettingsJson).Overlay);
        }
    }

    /// <summary>
    /// Every item write moves the block's version — a delete as much as an add or an edit — so the UPDATE
    /// carries the block's row-version predicate and an editor holding the old token is refused afterwards.
    /// </summary>
    [Fact]
    public async Task Adding_editing_and_removing_an_item_each_move_the_blocks_version()
    {
        var store = new FakeHomeContentAdminStore();
        var handlers = new HomeContentWriteHandlers(
            store, new RecordingAuditLogWriter(), new RecordingHomePageCache(), new CatalogOptions(), new FixedClock(Now));
        var section = store.Section(HomeSectionType.TrendingItems);

        var opened = store.RowVersionOf(HomeSectionType.TrendingItems);
        var created = Assert.IsType<ContentWriteResult.Succeeded>(await handlers.HandleAsync(
            new CreateHomeSectionItemCommand(section.Id, BaseItem() with { Title = "Linen" }, opened, TestActors.Editor), Token));
        Assert.Contains(section, store.Changed);
        Assert.NotEqual(opened, store.RowVersionOf(HomeSectionType.TrendingItems));

        store.Changed.Clear();
        section.RowVersion = [1, 2, 3, 4, 5, 6, 7, 8];
        Assert.IsType<ContentWriteResult.Succeeded>(await handlers.HandleAsync(
            new UpdateHomeSectionItemCommand(created.EntityId, BaseItem() with { Title = "Linen shirts" }, store.RowVersionOf(HomeSectionType.TrendingItems), TestActors.Editor), Token));
        Assert.Contains(section, store.Changed);

        store.Changed.Clear();
        section.RowVersion = [1, 2, 3, 4, 5, 6, 7, 9];
        var beforeDelete = store.RowVersionOf(HomeSectionType.TrendingItems);
        Assert.IsType<ContentWriteResult.Succeeded>(await handlers.HandleAsync(
            new DeleteHomeSectionItemCommand(created.EntityId, beforeDelete, TestActors.Editor), Token));
        Assert.Contains(section, store.Changed);
        Assert.NotEqual(beforeDelete, store.RowVersionOf(HomeSectionType.TrendingItems));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(999, true)]
    [InlineData(1000, false)]
    [InlineData(-1, false)]
    public void A_position_runs_from_zero_to_exactly_999(int position, bool accepted)
    {
        var errors = new List<ValidationError>();

        var stored = ContentFieldRules.SortOrder(errors, "sortOrder", position);

        Assert.Equal(accepted, errors.Count == 0);
        if (accepted)
        {
            Assert.Equal(position, stored);
        }
    }

    /// <summary>A full block refuses another item, but the item it already holds can still be edited.</summary>
    [Fact]
    public async Task The_one_item_of_a_full_block_can_still_be_edited()
    {
        var store = new FakeHomeContentAdminStore();
        var handlers = new HomeContentWriteHandlers(
            store, new RecordingAuditLogWriter(), new RecordingHomePageCache(), new CatalogOptions(), new FixedClock(Now));
        var existing = store.AddExisting(HomeSectionType.CampaignBanner, row => row.Title = "The new season");

        var result = await handlers.HandleAsync(
            new UpdateHomeSectionItemCommand(
                existing.Id,
                BaseItem() with { Title = "The linen edit" },
                store.RowVersionOf(HomeSectionType.CampaignBanner),
                TestActors.Editor),
            Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.Equal("The linen edit", existing.Title);
    }

    /// <summary>The announcement strip may link off the site (a full https address), as the API documents.</summary>
    [Fact]
    public async Task An_announcement_may_link_to_a_full_https_address()
    {
        var store = new FakeAnnouncementAdminStore(Now.UtcDateTime);
        var handlers = new SpencerDenim.Application.Content.Admin.Announcements.AnnouncementWriteHandlers(
            store, new RecordingAuditLogWriter(), new RecordingHomePageCache(), new FixedClock(Now));

        var result = await handlers.HandleAsync(
            new SpencerDenim.Application.Content.Admin.Announcements.CreateAnnouncementCommand(
                new SpencerDenim.Application.Content.Admin.Announcements.AnnouncementFields(
                    "Follow us", "https://instagram.com/colombowardrobe", 1, true, null, null),
                TestActors.Editor),
            Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.Equal("https://instagram.com/colombowardrobe", Assert.Single(store.Messages).LinkUrl);
    }

    // ================================================================== media slots

    /// <summary>A phone still alone is enough for a hero video — the schema's own CK says either still will do.</summary>
    [Fact]
    public async Task A_hero_video_with_only_a_phone_still_is_accepted()
    {
        var store = new FakeHomeContentAdminStore();
        var handlers = new HomeContentWriteHandlers(
            store, new RecordingAuditLogWriter(), new RecordingHomePageCache(), new CatalogOptions(), new FixedClock(Now));

        var result = await handlers.HandleAsync(
            new CreateHomeSectionItemCommand(
                (int)HomeSectionType.HeroVideo,
                BaseItem() with { VideoPublicId = TestMedia.Film, MobileImagePublicId = TestMedia.Photo },
                store.RowVersionOf(HomeSectionType.HeroVideo),
                TestActors.Editor),
            Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
    }

    public static TheoryData<string, Guid> RefusedAssets => new()
    {
        { "imagePublicId", TestMedia.Film },
        { "imagePublicId", TestMedia.NoAlt },
        { "imagePublicId", TestMedia.Processing },
        { "imagePublicId", TestMedia.PrivateSlip },
        { "imagePublicId", TestMedia.Unknown },
        { "mobileImagePublicId", TestMedia.Film },
        { "mobileImagePublicId", TestMedia.NoAlt },
        { "videoPublicId", TestMedia.Photo },
        { "videoPublicId", TestMedia.Unknown },
    };

    /// <summary>The wrong kind of file, one with no alt text, one not ready, private or missing: each refused on its own slot.</summary>
    [Theory]
    [MemberData(nameof(RefusedAssets))]
    public async Task A_hero_media_slot_refuses_an_asset_it_cannot_show(string slot, Guid asset)
    {
        var store = new FakeHomeContentAdminStore();
        var handlers = new HomeContentWriteHandlers(
            store, new RecordingAuditLogWriter(), new RecordingHomePageCache(), new CatalogOptions(), new FixedClock(Now));

        // A good still is always present, so a refusal can only be about the slot under test.
        var fields = slot switch
        {
            "imagePublicId" => BaseItem() with { ImagePublicId = asset, MobileImagePublicId = TestMedia.Photo },
            "mobileImagePublicId" => BaseItem() with { ImagePublicId = TestMedia.Photo, MobileImagePublicId = asset },
            _ => BaseItem() with { ImagePublicId = TestMedia.Photo, VideoPublicId = asset },
        };

        var result = await handlers.HandleAsync(
            new CreateHomeSectionItemCommand(
                (int)HomeSectionType.HeroVideo, fields, store.RowVersionOf(HomeSectionType.HeroVideo), TestActors.Editor),
            Token);

        var invalid = Assert.IsType<ContentWriteResult.Invalid>(result);
        Assert.Equal(slot, Assert.Single(invalid.Errors).Field);
        Assert.Equal(0, store.Saves);
    }

    // ================================================================== helpers

    /// <summary>Test data spells invisible characters as <c>{U+XXXX}</c> so the source stays readable.</summary>
    private static string Decode(string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            value,
            @"\{U\+(?<hex>[0-9A-F]{4})\}",
            match => ((char)Convert.ToInt32(match.Groups["hex"].Value, 16)).ToString());

    private static HomeSectionItemFields BaseItem() =>
        new(
            Title: null,
            Subtitle: null,
            Body: null,
            ButtonLabel: null,
            LinkUrl: null,
            ImagePublicId: null,
            MobileImagePublicId: null,
            VideoPublicId: null,
            DepartmentId: null,
            ProductSlug: null,
            IconKey: null,
            SortOrder: 1,
            IsEnabled: false,
            StartsAt: null,
            EndsAt: null);

    private static string RequestFieldOf(string ruleField) => ruleField switch
    {
        "image" => "imagePublicId",
        "mobileImage" => "mobileImagePublicId",
        "video" => "videoPublicId",
        "department" => "departmentId",
        "product" => "productSlug",
        _ => ruleField,
    };

    private sealed class FixedClock(DateTimeOffset now) : SpencerDenim.Application.Abstractions.Time.IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
