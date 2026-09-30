using SpencerDenim.Application.Media;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.UnitTests.Infrastructure.Media;

/// <summary>The size ceilings of doc 08 §3.3, which are the thing a reader is most likely to get wrong by eye.</summary>
public sealed class MediaOptionsTests
{
    [Fact]
    public void The_video_and_private_ceilings_are_the_ones_the_check_constraint_enforces()
    {
        Assert.Equal(50 * 1024 * 1024, MediaLimits.MaxVideoBytes);
        Assert.Equal(5 * 1024 * 1024, MediaLimits.MaxPrivateBytes);
    }

    [Fact]
    public void The_effective_ceiling_follows_the_visibility_and_the_type()
    {
        var options = new MediaOptions();

        Assert.Equal(options.MaxImageBytes, options.MaxBytesFor(MediaVisibility.Public, MediaAssetType.Image));
        Assert.Equal(MediaLimits.MaxVideoBytes, options.MaxBytesFor(MediaVisibility.Public, MediaAssetType.Video));
        Assert.Equal(MediaLimits.MaxPrivateBytes, options.MaxBytesFor(MediaVisibility.Private, MediaAssetType.Image));
        Assert.Equal(MediaLimits.MaxPrivateBytes, options.MaxBytesFor(MediaVisibility.Private, MediaAssetType.Document));
    }

    /// <summary>
    /// Configuration may lower a ceiling and must never raise it past the check constraint: the file would
    /// be written and the row then refused, which is the half-done state the pipeline exists to prevent.
    /// </summary>
    [Fact]
    public void Configuration_cannot_raise_a_ceiling_past_the_check_constraint()
    {
        var options = new MediaOptions
        {
            MaxVideoBytes = long.MaxValue,
            MaxPrivateBytes = long.MaxValue,
        };

        Assert.Equal(MediaLimits.MaxVideoBytes, options.MaxBytesFor(MediaVisibility.Public, MediaAssetType.Video));
        Assert.Equal(MediaLimits.MaxPrivateBytes, options.MaxBytesFor(MediaVisibility.Private, MediaAssetType.Image));
    }

    /// <summary>A video carries its poster in the same request, so the body ceiling has to cover both.</summary>
    [Fact]
    public void The_request_ceiling_leaves_room_for_a_video_and_its_poster()
    {
        var options = new MediaOptions();

        Assert.True(options.MaxRequestBytes > MediaLimits.MaxVideoBytes + options.MaxImageBytes - 1);
    }

    /// <summary>
    /// A label font is a host resource, so it is off unless a deployment names one (doc 03 §7). The
    /// placeholders are unique and unmistakable without it.
    /// </summary>
    [Fact]
    public void No_placeholder_font_is_assumed_by_default()
    {
        Assert.Equal(string.Empty, new MediaOptions().PlaceholderFontPath);
    }
}
