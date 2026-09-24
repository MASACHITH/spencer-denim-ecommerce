using SpencerDenim.Domain.Content;

namespace SpencerDenim.UnitTests.Domain.Content;

/// <summary>
/// The rule that decides whether a homepage section, one of its items or an announcement line is on
/// the storefront (doc 08 §3.7). Every reader applies it, so it is pinned once here.
/// </summary>
public sealed class ContentScheduleTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_switched_off_row_is_never_live_however_wide_its_window()
    {
        Assert.False(ContentSchedule.IsLive(isEnabled: false, startsAtUtc: null, endsAtUtc: null, Now));
        Assert.False(ContentSchedule.IsLive(
            isEnabled: false,
            startsAtUtc: Now.AddYears(-1),
            endsAtUtc: Now.AddYears(1),
            Now));
    }

    [Fact]
    public void A_switched_on_row_with_no_window_is_always_live()
    {
        Assert.True(ContentSchedule.IsLive(isEnabled: true, startsAtUtc: null, endsAtUtc: null, Now));
    }

    [Theory]
    // The start is inclusive: a campaign set to begin at noon is on at noon, not a tick later.
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    public void The_start_is_inclusive(int offsetMilliseconds, bool expected)
    {
        var starts = Now.AddMilliseconds(offsetMilliseconds);

        Assert.Equal(expected, ContentSchedule.IsLive(true, starts, endsAtUtc: null, Now));
    }

    [Theory]
    // The end is exclusive, so a window ending at noon is over at noon and the next one may begin
    // there without the two overlapping for a millisecond.
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    public void The_end_is_exclusive(int offsetMilliseconds, bool expected)
    {
        var ends = Now.AddMilliseconds(offsetMilliseconds);

        Assert.Equal(expected, ContentSchedule.IsLive(true, startsAtUtc: null, ends, Now));
    }

    [Fact]
    public void Two_consecutive_windows_never_both_apply_at_the_boundary()
    {
        var boundary = Now;

        var first = ContentSchedule.IsLive(true, boundary.AddHours(-1), boundary, boundary);
        var second = ContentSchedule.IsLive(true, boundary, boundary.AddHours(1), boundary);

        Assert.False(first);
        Assert.True(second);
    }

    [Fact]
    public void A_window_that_ends_before_it_starts_is_never_live()
    {
        Assert.False(ContentSchedule.IsLive(true, Now.AddDays(1), Now.AddDays(-1), Now));
    }

    [Fact]
    public void The_entities_all_ask_the_same_question()
    {
        var section = new HomeSection { IsEnabled = true, EndsAtUtc = Now.AddMinutes(-1) };
        var item = new HomeSectionItem { IsEnabled = true, StartsAtUtc = Now.AddMinutes(1) };
        var message = new AnnouncementMessage { IsActive = true, StartsAtUtc = Now.AddMinutes(-1) };

        Assert.False(section.IsLiveAt(Now));
        Assert.False(item.IsLiveAt(Now));
        Assert.True(message.IsLiveAt(Now));
    }

    /// <summary>
    /// Doc 08 §3.7's <c>CK_HomeSectionItems_VideoFallback</c>, as the application sees it: a hero
    /// video always has a still for a phone, a data-saver and reduced motion (HOME-01).
    /// </summary>
    [Fact]
    public void A_video_item_needs_a_still_to_fall_back_to()
    {
        Assert.True(new HomeSectionItem().HasStaticFallback);
        Assert.False(new HomeSectionItem { VideoAssetId = 1 }.HasStaticFallback);
        Assert.True(new HomeSectionItem { VideoAssetId = 1, ImageAssetId = 2 }.HasStaticFallback);
        Assert.True(new HomeSectionItem { VideoAssetId = 1, MobileImageAssetId = 3 }.HasStaticFallback);
    }
}
