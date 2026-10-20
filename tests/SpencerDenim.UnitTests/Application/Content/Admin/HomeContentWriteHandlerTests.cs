using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Content;
using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Application.Content.Admin.Home;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.UnitTests.Application.Content.Admin;

/// <summary>
/// The ADM-17 homepage writes (#192): the block rules, the field rules, the section row version standing
/// guard over item writes, the audit row and the cache eviction doc 08 §3.7 asks of every save.
/// </summary>
public sealed class HomeContentWriteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);

    private readonly FakeHomeContentAdminStore _store = new();
    private readonly RecordingAuditLogWriter _audit = new();
    private readonly RecordingHomePageCache _cache = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private HomeContentWriteHandlers Handlers() =>
        new(_store, _audit, _cache, new CatalogOptions(), new FixedClock(Now));

    // ================================================================== sections

    [Fact]
    public async Task A_section_write_without_the_current_row_version_is_refused_and_nothing_is_saved()
    {
        foreach (var version in new string?[] { null, "", "AAAAAAAAAAA=", "not base64!" })
        {
            var result = await Handlers().HandleAsync(
                Section(HomeSectionType.Newsletter, title: "Stay in touch") with { RowVersion = version }, Token);

            var conflict = Assert.IsType<ContentWriteResult.Conflict>(result);
            Assert.Equal(ContentConflictCodes.StaleRowVersion, conflict.Code);
        }

        Assert.Equal(0, _store.Saves);
        Assert.Equal(0, _cache.Evictions);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task An_unknown_section_is_not_found()
    {
        var result = await Handlers().HandleAsync(Section(HomeSectionType.Newsletter) with { SectionId = 999 }, Token);

        Assert.IsType<ContentWriteResult.NotFound>(result);
    }

    [Fact]
    public async Task A_change_is_saved_audited_as_a_diff_and_evicts_the_homepage()
    {
        var result = await Handlers().HandleAsync(
            Section(HomeSectionType.Newsletter, title: "Stay in touch", subtitle: "Sale news first.") with { SortOrder = 12 },
            Token);

        var saved = Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.Equal((int)HomeSectionType.Newsletter, saved.EntityId);

        var section = _store.Section(HomeSectionType.Newsletter);
        Assert.Equal("Stay in touch", section.Title);
        Assert.Equal("Sale news first.", section.Subtitle);
        Assert.Equal(12, section.SortOrder);
        Assert.True(section.IsEnabled);

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.Update, row.Action);
        Assert.Equal(HomeContentWriteHandlers.SectionEntityType, row.EntityType);
        Assert.Equal("10", row.EntityId);
        Assert.Contains("\"title\":\"Stay in touch\"", row.AfterJson, StringComparison.Ordinal);
        Assert.DoesNotContain("startsAtUtc", row.AfterJson, StringComparison.Ordinal);

        Assert.Equal(1, _store.Saves);
        Assert.Equal(1, _cache.Evictions);
    }

    [Fact]
    public async Task A_form_saved_untouched_is_not_an_event()
    {
        var section = _store.Section(HomeSectionType.NewArrivals);
        section.Title = "New arrivals";
        section.IsEnabled = true;
        section.SortOrder = 3;
        section.SettingsJson = """{"mode":"auto","count":8,"listingUrl":"/new-arrivals","linkLabel":"View all"}""";

        var result = await Handlers().HandleAsync(
            Section(HomeSectionType.NewArrivals, title: "New arrivals") with
            {
                SortOrder = 3,
                Count = 8,
                ListingUrl = "/new-arrivals",
                LinkLabel = "View all",
            },
            Token);

        Assert.IsType<ContentWriteResult.Unchanged>(result);
        Assert.Equal(0, _store.Saves);
        Assert.Equal(0, _cache.Evictions);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// The stored spelling of the settings is not this class's: a seeded <c>{"mode":"manual"}</c> means
    /// count 8 as surely as the long form does, and an untouched save must not rewrite it.
    /// </summary>
    [Fact]
    public async Task Settings_that_mean_the_same_keep_their_stored_spelling()
    {
        var section = _store.Section(HomeSectionType.TrendingItems);
        section.SortOrder = 4;
        section.IsEnabled = true;
        section.SettingsJson = """{"mode":"manual"}""";

        var result = await Handlers().HandleAsync(
            Section(HomeSectionType.TrendingItems) with { SortOrder = 4, Mode = HomeSectionMode.Manual, Count = 8 }, Token);

        Assert.IsType<ContentWriteResult.Unchanged>(result);
        Assert.Equal("""{"mode":"manual"}""", section.SettingsJson);
    }

    [Fact]
    public async Task Only_the_keys_a_block_uses_are_written_to_its_settings()
    {
        await Handlers().HandleAsync(Section(HomeSectionType.CampaignBanner) with { Overlay = "LIGHT" }, Token);
        Assert.Equal("""{"overlay":"light"}""", _store.Section(HomeSectionType.CampaignBanner).SettingsJson);

        await Handlers().HandleAsync(
            Section(HomeSectionType.SaleSection) with { Count = 12, ListingUrl = "/sale?sort=newest", LinkLabel = "Shop the sale" },
            Token);
        Assert.Equal(
            """{"mode":"auto","count":12,"listingUrl":"/sale?sort=newest","linkLabel":"Shop the sale"}""",
            _store.Section(HomeSectionType.SaleSection).SettingsJson);

        // A heading-only block keeps no settings at all.
        _store.Section(HomeSectionType.Benefits).SettingsJson = """{"overlay":"dark"}""";
        await Handlers().HandleAsync(Section(HomeSectionType.Benefits, title: "Why shop here"), Token);
        Assert.Null(_store.Section(HomeSectionType.Benefits).SettingsJson);
    }

    [Theory]
    [InlineData(HomeSectionType.NewArrivals, "overlay")]
    [InlineData(HomeSectionType.SaleSection, "mode")]
    [InlineData(HomeSectionType.Benefits, "count")]
    [InlineData(HomeSectionType.HeroVideo, "listingUrl")]
    [InlineData(HomeSectionType.Newsletter, "linkLabel")]
    public async Task A_setting_the_block_does_not_use_is_refused_by_name(HomeSectionType type, string field)
    {
        var command = Section(type) with
        {
            Count = HomeBlockRules.IsProductRow(type) ? 8 : null,
            ListingUrl = HomeBlockRules.IsProductRow(type) ? "/new-arrivals" : null,
        };

        command = field switch
        {
            "overlay" => command with { Overlay = "dark" },
            "mode" => command with { Mode = HomeSectionMode.Manual },
            "count" => command with { Count = 4 },
            "listingUrl" => command with { ListingUrl = "/women" },
            "linkLabel" => command with { LinkLabel = "See more" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var result = await Handlers().HandleAsync(command, Token);

        var invalid = Assert.IsType<ContentWriteResult.Invalid>(result);
        Assert.Contains(invalid.Errors, error => error.Field == field && error.Message.Contains("does not use", StringComparison.Ordinal));
        Assert.Equal(0, _store.Saves);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(24, true)]
    [InlineData(25, false)]
    [InlineData(null, false)]
    public async Task A_product_row_shows_between_one_and_twenty_four_products(int? count, bool accepted)
    {
        var result = await Handlers().HandleAsync(
            Section(HomeSectionType.NewArrivals) with { Count = count, ListingUrl = "/new-arrivals" }, Token);

        if (accepted)
        {
            Assert.IsType<ContentWriteResult.Succeeded>(result);
        }
        else
        {
            var invalid = Assert.IsType<ContentWriteResult.Invalid>(result);
            Assert.Contains(invalid.Errors, error => error.Field == "count");
        }
    }

    [Theory]
    [InlineData(null, "Say which listing")]
    [InlineData("https://elsewhere.example/sale", "path on this site")]
    [InlineData("//elsewhere.example/sale", "path on this site")]
    [InlineData("javascript:alert(1)", "path on this site")]
    [InlineData("/women\\..\\x", "path on this site")]
    [InlineData("/women?minPrice=900&maxPrice=100", "cannot show that listing")]
    public async Task An_automatic_row_needs_a_listing_on_this_site_the_catalogue_can_answer(string? listingUrl, string message)
    {
        var result = await Handlers().HandleAsync(
            Section(HomeSectionType.SaleSection) with { Count = 8, ListingUrl = listingUrl }, Token);

        var invalid = Assert.IsType<ContentWriteResult.Invalid>(result);
        var error = Assert.Single(invalid.Errors);
        Assert.Equal("listingUrl", error.Field);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hand_picked_trending_row_needs_no_listing_but_does_need_a_mode()
    {
        var manual = await Handlers().HandleAsync(
            Section(HomeSectionType.TrendingItems) with { Mode = HomeSectionMode.Manual, Count = 8 }, Token);
        Assert.IsType<ContentWriteResult.Succeeded>(manual);

        var noMode = await Handlers().HandleAsync(
            Section(HomeSectionType.TrendingItems) with { Mode = null, Count = 8, RowVersion = _store.RowVersionOf(HomeSectionType.TrendingItems) },
            Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(noMode).Errors, error => error.Field == "mode");
    }

    [Theory]
    [InlineData("2026-11-01T00:00", "2026-11-01T00:00")]
    [InlineData("2026-11-02T00:00", "2026-11-01T23:59")]
    public async Task A_schedule_must_end_after_it_starts(string starts, string ends)
    {
        var result = await Handlers().HandleAsync(
            Section(HomeSectionType.Newsletter) with { StartsAt = DateTime.Parse(starts, System.Globalization.CultureInfo.InvariantCulture), EndsAt = DateTime.Parse(ends, System.Globalization.CultureInfo.InvariantCulture) },
            Token);

        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == "endsAt");
    }

    /// <summary>The form speaks Colombo wall-clock and the column holds UTC: 00:00 +05:30 is 18:30 the day before.</summary>
    [Fact]
    public async Task A_schedule_typed_in_shop_time_is_stored_as_the_instant_it_names()
    {
        await Handlers().HandleAsync(
            Section(HomeSectionType.Newsletter) with
            {
                StartsAt = new DateTime(2026, 11, 1, 0, 0, 0),
                EndsAt = new DateTime(2026, 11, 8, 6, 15, 42),
            },
            Token);

        var section = _store.Section(HomeSectionType.Newsletter);
        Assert.Equal(new DateTime(2026, 10, 31, 18, 30, 0, DateTimeKind.Utc), section.StartsAtUtc);

        // To the minute: the seconds a form cannot show do not become a boundary nobody can see.
        Assert.Equal(new DateTime(2026, 11, 8, 0, 45, 0, DateTimeKind.Utc), section.EndsAtUtc);
        Assert.Equal(DateTimeKind.Utc, section.EndsAtUtc!.Value.Kind);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1000)]
    [InlineData(null)]
    public async Task A_position_is_between_zero_and_nine_hundred_and_ninety_nine(int? sortOrder)
    {
        var result = await Handlers().HandleAsync(Section(HomeSectionType.Newsletter) with { SortOrder = sortOrder }, Token);

        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == "sortOrder");
    }

    [Theory]
    [InlineData("Line one\nline two")]
    [InlineData("Tab\there")]
    public async Task A_heading_is_one_line_of_plain_text(string title)
    {
        var result = await Handlers().HandleAsync(Section(HomeSectionType.Newsletter, title: title), Token);

        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == "title");
    }

    [Fact]
    public async Task A_heading_longer_than_its_column_is_refused_and_one_at_the_limit_is_kept()
    {
        var atLimit = await Handlers().HandleAsync(
            Section(HomeSectionType.Newsletter, title: new string('a', ContentFieldLimits.TitleMaxLength)), Token);
        Assert.IsType<ContentWriteResult.Succeeded>(atLimit);

        var over = await Handlers().HandleAsync(
            Section(HomeSectionType.Newsletter, title: new string('b', ContentFieldLimits.TitleMaxLength + 1)), Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(over).Errors, error => error.Field == "title");
    }

    /// <summary>
    /// Plain text is stored as typed — it is escaped where it is shown, by interpolation — so markup is
    /// neither stripped nor refused: "<b>" in a heading is text.
    /// </summary>
    [Fact]
    public async Task Markup_in_a_heading_is_stored_as_the_text_it_is()
    {
        await Handlers().HandleAsync(Section(HomeSectionType.Newsletter, title: "Tops <b>& tees</b>"), Token);

        Assert.Equal("Tops <b>& tees</b>", _store.Section(HomeSectionType.Newsletter).Title);
    }

    [Fact]
    public async Task A_save_the_database_refuses_as_stale_is_a_conflict_and_does_not_evict()
    {
        _store.NextOutcome = ContentSaveOutcome.ConcurrencyConflict;

        var result = await Handlers().HandleAsync(Section(HomeSectionType.Newsletter, title: "Changed"), Token);

        Assert.Equal(ContentConflictCodes.StaleRowVersion, Assert.IsType<ContentWriteResult.Conflict>(result).Code);
        Assert.Equal(0, _cache.Evictions);
    }

    // ================================================================== items

    [Theory]
    [InlineData(HomeSectionType.NewArrivals)]
    [InlineData(HomeSectionType.SaleSection)]
    [InlineData(HomeSectionType.Newsletter)]
    public async Task A_block_with_no_items_of_its_own_refuses_one(HomeSectionType type)
    {
        var result = await Handlers().HandleAsync(CreateItem(type, Item(title: "x")), Token);

        Assert.Equal(ContentConflictCodes.ItemsNotSupported, Assert.IsType<ContentWriteResult.Conflict>(result).Code);
    }

    [Fact]
    public async Task A_full_block_refuses_another_item()
    {
        _store.AddExisting(HomeSectionType.CampaignBanner);

        var result = await Handlers().HandleAsync(CreateItem(HomeSectionType.CampaignBanner, Item(title: "Second banner")), Token);

        var conflict = Assert.IsType<ContentWriteResult.Conflict>(result);
        Assert.Equal(ContentConflictCodes.SectionFull, conflict.Code);
        Assert.Equal(1, conflict.Details!["maxItems"]);
    }

    [Fact]
    public async Task Creating_an_item_moves_the_section_version_records_its_media_and_evicts()
    {
        var result = await Handlers().HandleAsync(
            CreateItem(
                HomeSectionType.CampaignBanner,
                Item(title: "The new season", linkUrl: "/new-arrivals") with
                {
                    ImagePublicId = TestMedia.Photo,
                    MobileImagePublicId = TestMedia.OtherPhoto,
                }),
            Token);

        var saved = Assert.IsType<ContentWriteResult.Succeeded>(result);
        var item = Assert.Single(_store.Section(HomeSectionType.CampaignBanner).Items);
        Assert.Equal(item.Id, saved.EntityId);
        Assert.Equal(101, item.ImageAssetId);
        Assert.Equal(102, item.MobileImageAssetId);

        // The block is the aggregate: the item write is checked against, and moves, the block's version.
        Assert.Contains(_store.Section(HomeSectionType.CampaignBanner), _store.Changed);
        Assert.Equal(_store.RowVersionOf(HomeSectionType.CampaignBanner), saved.RowVersion);

        Assert.Equal(MediaUsageOwnerTypes.HomeSectionItem, _store.LastUsage!.OwnerType);
        Assert.Equal(item.Id, _store.LastUsageOwnerId);
        Assert.Equal(
            new[] { new ContentMediaUsage(101, MediaUsageRoles.Banner), new ContentMediaUsage(102, MediaUsageRoles.MobileImage) }.ToHashSet(),
            _store.LastUsage.Desired.ToHashSet());

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.Create, row.Action);
        Assert.Equal(HomeContentWriteHandlers.ItemEntityType, row.EntityType);
        Assert.Equal(1, _cache.Evictions);
    }

    [Fact]
    public async Task A_hero_video_needs_a_still_and_lands_in_the_video_role()
    {
        var noStill = await Handlers().HandleAsync(
            CreateItem(HomeSectionType.HeroVideo, Item() with { VideoPublicId = TestMedia.Film }), Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(noStill).Errors, error => error.Field == "imagePublicId");

        var withStill = await Handlers().HandleAsync(
            CreateItem(HomeSectionType.HeroVideo, Item() with { VideoPublicId = TestMedia.Film, MobileImagePublicId = TestMedia.Photo }),
            Token);
        Assert.IsType<ContentWriteResult.Succeeded>(withStill);
        Assert.Contains(new ContentMediaUsage(103, MediaUsageRoles.Video), _store.LastUsage!.Desired);
        Assert.Contains(new ContentMediaUsage(101, MediaUsageRoles.MobileImage), _store.LastUsage.Desired);
    }

    [Theory]
    [InlineData("unknown", "not in the media library")]
    [InlineData("film", "not a video")]
    [InlineData("noalt", "no alt text")]
    [InlineData("processing", "not ready")]
    [InlineData("private", "not ready")]
    public async Task An_image_slot_takes_only_a_ready_public_image_with_alt_text(string which, string message)
    {
        var id = which switch
        {
            "unknown" => TestMedia.Unknown,
            "film" => TestMedia.Film,
            "noalt" => TestMedia.NoAlt,
            "processing" => TestMedia.Processing,
            _ => TestMedia.PrivateSlip,
        };

        var result = await Handlers().HandleAsync(
            CreateItem(HomeSectionType.SocialGallery, Item(title: "Instagram") with { ImagePublicId = id }), Token);

        var error = Assert.Single(Assert.IsType<ContentWriteResult.Invalid>(result).Errors);
        Assert.Equal("imagePublicId", error.Field);
        Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The hero and both banners are nothing without a picture — the storefront draws them only with
    /// artwork — so an item there can be switched on only with one, and saved switched off without it.
    /// </summary>
    [Theory]
    [InlineData(HomeSectionType.HeroVideo)]
    [InlineData(HomeSectionType.CampaignBanner)]
    [InlineData(HomeSectionType.ActivewearFeature)]
    public async Task A_hero_or_banner_item_is_switched_on_only_with_a_picture(HomeSectionType type)
    {
        var on = await Handlers().HandleAsync(CreateItem(type, Item(title: "Words only")), Token);
        var error = Assert.Single(Assert.IsType<ContentWriteResult.Invalid>(on).Errors);
        Assert.Equal("imagePublicId", error.Field);

        var off = await Handlers().HandleAsync(CreateItem(type, Item(title: "Words only") with { IsEnabled = false }), Token);
        Assert.IsType<ContentWriteResult.Succeeded>(off);

        var item = _store.Section(type).Items.Single();
        var withPhonePicture = await Handlers().HandleAsync(
            new UpdateHomeSectionItemCommand(item.Id, Item(title: "Words only") with { MobileImagePublicId = TestMedia.Photo }, _store.RowVersionOf(type), TestActors.Editor),
            Token);
        Assert.IsType<ContentWriteResult.Succeeded>(withPhonePicture);
    }

    [Fact]
    public async Task Tiles_and_benefits_need_no_picture_to_be_switched_on()
    {
        Assert.IsType<ContentWriteResult.Succeeded>(
            await Handlers().HandleAsync(CreateItem(HomeSectionType.TrendingItems, Item(title: "Linen", linkUrl: "/women")), Token));
        Assert.IsType<ContentWriteResult.Succeeded>(
            await Handlers().HandleAsync(CreateItem(HomeSectionType.Benefits, Item(title: "Delivery") with { IconKey = "delivery" }), Token));
    }

    [Fact]
    public async Task A_video_slot_refuses_an_image()
    {
        var result = await Handlers().HandleAsync(
            CreateItem(HomeSectionType.HeroVideo, Item() with { VideoPublicId = TestMedia.Photo, ImagePublicId = TestMedia.OtherPhoto }),
            Token);

        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == "videoPublicId");
    }

    [Theory]
    [InlineData("https://instagram.com/colombowardrobe", HomeSectionType.SocialGallery, true)]
    [InlineData("http://instagram.com/x", HomeSectionType.SocialGallery, true)]
    [InlineData("/about", HomeSectionType.SocialGallery, true)]
    [InlineData("javascript:alert(1)", HomeSectionType.SocialGallery, false)]
    [InlineData("data:text/html,<script>alert(1)</script>", HomeSectionType.SocialGallery, false)]
    [InlineData("https://instagram.com@evil.example/", HomeSectionType.SocialGallery, false)]
    [InlineData("https://instagram.com/a b", HomeSectionType.SocialGallery, false)]
    [InlineData("https://shop.example/women", HomeSectionType.TrendingItems, false)]
    [InlineData("//evil.example/women", HomeSectionType.TrendingItems, false)]
    [InlineData("/women\\@evil.example", HomeSectionType.TrendingItems, false)]
    [InlineData("/women/jeans?fit=wide-leg", HomeSectionType.TrendingItems, true)]
    public async Task An_item_link_is_a_path_on_this_site_or_where_the_block_allows_it_a_web_address(
        string linkUrl,
        HomeSectionType type,
        bool accepted)
    {
        var fields = Item(title: "Tile", linkUrl: linkUrl) with
        {
            ImagePublicId = type == HomeSectionType.SocialGallery ? TestMedia.Photo : null,
        };

        var result = await Handlers().HandleAsync(CreateItem(type, fields), Token);

        if (accepted)
        {
            Assert.IsType<ContentWriteResult.Succeeded>(result);
            Assert.Equal(linkUrl, _store.Section(type).Items.Single().LinkUrl);
        }
        else
        {
            Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(result).Errors, error => error.Field == "linkUrl");
        }
    }

    [Fact]
    public async Task A_benefit_needs_an_icon_the_storefront_can_draw()
    {
        var missing = await Handlers().HandleAsync(CreateItem(HomeSectionType.Benefits, Item(title: "Delivery")), Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(missing).Errors, error => error.Field == "iconKey");

        var unknown = await Handlers().HandleAsync(
            CreateItem(HomeSectionType.Benefits, Item(title: "Delivery") with { IconKey = "rocket" }), Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(unknown).Errors, error => error.Field == "iconKey");

        var known = await Handlers().HandleAsync(
            CreateItem(HomeSectionType.Benefits, Item(title: "Delivery") with { IconKey = " Delivery " }), Token);
        Assert.IsType<ContentWriteResult.Succeeded>(known);
        Assert.Equal("delivery", _store.Section(HomeSectionType.Benefits).Items.Single().IconKey);
    }

    [Fact]
    public async Task A_department_tile_needs_a_department_that_exists()
    {
        var missing = await Handlers().HandleAsync(CreateItem(HomeSectionType.ShopByDepartment, Item()), Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(missing).Errors, error => error.Field == "departmentId");

        var unknown = await Handlers().HandleAsync(
            CreateItem(HomeSectionType.ShopByDepartment, Item() with { DepartmentId = 99 }), Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(unknown).Errors, error => error.Field == "departmentId");

        var known = await Handlers().HandleAsync(
            CreateItem(HomeSectionType.ShopByDepartment, Item() with { DepartmentId = 2 }), Token);
        Assert.IsType<ContentWriteResult.Succeeded>(known);
    }

    [Fact]
    public async Task A_trending_tile_needs_a_heading_and_may_name_a_product_by_its_address()
    {
        var noTitle = await Handlers().HandleAsync(CreateItem(HomeSectionType.TrendingItems, Item(linkUrl: "/women")), Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(noTitle).Errors, error => error.Field == "title");

        var badProduct = await Handlers().HandleAsync(
            CreateItem(HomeSectionType.TrendingItems, Item(title: "Linen") with { ProductSlug = "nope" }), Token);
        Assert.Contains(Assert.IsType<ContentWriteResult.Invalid>(badProduct).Errors, error => error.Field == "productSlug");

        var product = await Handlers().HandleAsync(
            CreateItem(HomeSectionType.TrendingItems, Item(title: "Linen") with { ProductSlug = "linen-shirt" }), Token);
        Assert.IsType<ContentWriteResult.Succeeded>(product);
        Assert.Equal(41, _store.Section(HomeSectionType.TrendingItems).Items.Single().ProductId);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("iconKey")]
    [InlineData("departmentId")]
    [InlineData("productSlug")]
    [InlineData("body")]
    [InlineData("buttonLabel")]
    public async Task An_item_field_the_block_does_not_use_is_refused_by_name(string field)
    {
        var fields = Item(title: "Instagram") with { ImagePublicId = TestMedia.Photo };
        fields = field switch
        {
            "video" => fields with { VideoPublicId = TestMedia.Film },
            "iconKey" => fields with { IconKey = "delivery" },
            "departmentId" => fields with { DepartmentId = 1 },
            "productSlug" => fields with { ProductSlug = "linen-shirt" },
            "body" => fields with { Body = "Words" },
            _ => fields with { ButtonLabel = "Go" },
        };

        var result = await Handlers().HandleAsync(CreateItem(HomeSectionType.SocialGallery, fields), Token);

        var expectedField = field == "video" ? "videoPublicId" : field;
        Assert.Contains(
            Assert.IsType<ContentWriteResult.Invalid>(result).Errors,
            error => error.Field == expectedField && error.Message.Contains("does not use", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_write_with_a_stale_section_version_is_refused()
    {
        var item = _store.AddExisting(HomeSectionType.Benefits, row => row.IconKey = "delivery");

        var result = await Handlers().HandleAsync(
            new UpdateHomeSectionItemCommand(item.Id, Item(title: "Delivery") with { IconKey = "delivery" }, "AAAAAAAAAAE=", TestActors.Editor),
            Token);

        Assert.Equal(ContentConflictCodes.StaleRowVersion, Assert.IsType<ContentWriteResult.Conflict>(result).Code);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task Editing_an_item_audits_only_what_changed_and_an_untouched_save_is_not_an_event()
    {
        var item = _store.AddExisting(HomeSectionType.Benefits, row =>
        {
            row.IconKey = "delivery";
            row.Title = "Island-wide delivery";
            row.Body = "We deliver.";
            row.SortOrder = 1;
        });

        var same = Item(title: "Island-wide delivery") with { IconKey = "delivery", Body = "We deliver.", SortOrder = 1 };

        var untouched = await Handlers().HandleAsync(
            new UpdateHomeSectionItemCommand(item.Id, same, _store.RowVersionOf(HomeSectionType.Benefits), TestActors.Editor), Token);
        Assert.IsType<ContentWriteResult.Unchanged>(untouched);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _cache.Evictions);

        var changed = await Handlers().HandleAsync(
            new UpdateHomeSectionItemCommand(item.Id, same with { Body = "We deliver everywhere." }, _store.RowVersionOf(HomeSectionType.Benefits), TestActors.Editor),
            Token);
        Assert.IsType<ContentWriteResult.Succeeded>(changed);

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.Update, row.Action);
        Assert.Equal("{\"body\":\"We deliver.\"}", row.BeforeJson);
        Assert.Equal("{\"body\":\"We deliver everywhere.\"}", row.AfterJson);
        Assert.Equal(1, _cache.Evictions);
    }

    [Fact]
    public async Task Removing_a_picture_from_an_item_removes_its_usage_row()
    {
        var item = _store.AddExisting(HomeSectionType.SocialGallery, row =>
        {
            row.Title = "Instagram";
            row.ImageAssetId = 101;
            row.MobileImageAssetId = 102;
        });

        await Handlers().HandleAsync(
            new UpdateHomeSectionItemCommand(
                item.Id,
                Item(title: "Instagram") with { ImagePublicId = TestMedia.Photo },
                _store.RowVersionOf(HomeSectionType.SocialGallery),
                TestActors.Editor),
            Token);

        // The social block has no mobile slot, so the update that does not send one must not be refused —
        // and the usage rows end as exactly the one picture left.
        Assert.Null(item.MobileImageAssetId);
        Assert.Equal([new ContentMediaUsage(101, MediaUsageRoles.Image)], _store.LastUsage!.Desired);
    }

    [Fact]
    public async Task Deleting_an_item_audits_it_drops_its_usage_rows_and_evicts()
    {
        var item = _store.AddExisting(HomeSectionType.TrendingItems, row =>
        {
            row.Title = "Linen shirts";
            row.ImageAssetId = 101;
        });

        var result = await Handlers().HandleAsync(
            new DeleteHomeSectionItemCommand(item.Id, _store.RowVersionOf(HomeSectionType.TrendingItems), TestActors.Editor), Token);

        Assert.IsType<ContentWriteResult.Succeeded>(result);
        Assert.Contains(item, _store.Removed);
        Assert.Empty(_store.LastUsage!.Desired);
        Assert.Equal(item.Id, _store.LastUsageOwnerId);

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.Delete, row.Action);
        Assert.Contains("Linen shirts", row.BeforeJson, StringComparison.Ordinal);
        Assert.Null(row.AfterJson);
        Assert.Equal(1, _cache.Evictions);
    }

    [Fact]
    public async Task An_unknown_item_is_not_found()
    {
        var result = await Handlers().HandleAsync(
            new DeleteHomeSectionItemCommand(12345, _store.RowVersionOf(HomeSectionType.TrendingItems), TestActors.Editor), Token);

        Assert.IsType<ContentWriteResult.NotFound>(result);
    }

    // ================================================================== helpers

    private UpdateHomeSectionCommand Section(HomeSectionType type, string? title = null, string? subtitle = null) =>
        new(
            (int)type,
            title,
            subtitle,
            IsEnabled: true,
            SortOrder: (short)type,
            StartsAt: null,
            EndsAt: null,
            Mode: type == HomeSectionType.TrendingItems ? HomeSectionMode.Manual : null,
            Count: HomeBlockRules.IsProductRow(type) ? 8 : null,
            ListingUrl: type is HomeSectionType.NewArrivals or HomeSectionType.SaleSection ? "/new-arrivals" : null,
            LinkLabel: null,
            Overlay: null,
            RowVersion: _store.RowVersionOf(type),
            Actor: TestActors.Editor);

    private CreateHomeSectionItemCommand CreateItem(HomeSectionType type, HomeSectionItemFields fields) =>
        new((int)type, fields, _store.RowVersionOf(type), TestActors.Editor);

    private static HomeSectionItemFields Item(string? title = null, string? linkUrl = null) =>
        new(
            title,
            Subtitle: null,
            Body: null,
            ButtonLabel: null,
            linkUrl,
            ImagePublicId: null,
            MobileImagePublicId: null,
            VideoPublicId: null,
            DepartmentId: null,
            ProductSlug: null,
            IconKey: null,
            SortOrder: 1,
            IsEnabled: true,
            StartsAt: null,
            EndsAt: null);
}
