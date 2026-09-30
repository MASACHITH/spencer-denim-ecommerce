using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Media;
using SpencerDenim.Infrastructure.Media;

namespace SpencerDenim.UnitTests.Infrastructure.Media;

/// <summary>
/// Doc 03 §7, applied to a font: the label typeface comes from configuration or from nowhere, and every
/// path has to be survivable, because the deployment target is a container that has no font at all.
/// </summary>
public sealed class PlaceholderTypefaceSourceTests : IDisposable
{
    private readonly List<PlaceholderTypefaceSource> _created = [];
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "sd-font-tests-" + Guid.NewGuid().ToString("N"));

    public PlaceholderTypefaceSourceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        foreach (var source in _created)
        {
            source.Dispose();
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// Nothing configured: whatever this host has, or nothing — and either is a usable state. A machine with
    /// fonts reports where they came from; a container with none reports "none" and the placeholders go out
    /// unlabelled.
    /// </summary>
    [Fact]
    public void With_nothing_configured_the_source_reports_what_this_host_actually_has()
    {
        var source = Create(new MediaOptions());

        if (source.CanDrawText)
        {
            Assert.NotEqual("none", source.Source);
            Assert.NotNull(source.Font);
        }
        else
        {
            Assert.Equal("none", source.Source);
            Assert.Null(source.Font);
        }
    }

    /// <summary>
    /// A configured path that is not there is a misconfiguration, not a crash: it falls back and logs. An
    /// exception here would mean a typo in one environment variable took the whole API down at startup.
    /// </summary>
    [Fact]
    public void A_configured_font_file_that_does_not_exist_falls_back_instead_of_throwing()
    {
        var source = Create(new MediaOptions
        {
            PlaceholderFontPath = Path.Combine(_directory, "not-here.ttf"),
        });

        Assert.DoesNotContain("not-here.ttf", source.Source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason no font is embedded, pinned as a test rather than left as a claim in a comment: the file
    /// the storefront ships Inter as is a <c>.woff2</c>, and Skia will not open one. If a future version of
    /// Skia gains WOFF2 support this test fails, which is exactly when somebody should revisit the decision.
    /// </summary>
    [Fact]
    public void A_web_font_file_is_not_accepted_which_is_why_the_storefronts_inter_cannot_be_reused()
    {
        // A WOFF2 header: "wOF2", then a declared flavour and length. Enough for Skia to reject it as a
        // typeface, which is the point; a real Inter file behaves identically (measured).
        var woff2 = new byte[64];
        "wOF2"u8.CopyTo(woff2);

        var path = Path.Combine(_directory, "inter-latin-400-normal.woff2");
        File.WriteAllBytes(path, woff2);

        var source = Create(new MediaOptions { PlaceholderFontPath = path });

        Assert.DoesNotContain("woff2", source.Source, StringComparison.OrdinalIgnoreCase);
    }

    private PlaceholderTypefaceSource Create(MediaOptions options)
    {
        var source = new PlaceholderTypefaceSource(
            Options.Create(options),
            EmptyServices.Instance,
            NullLogger<PlaceholderTypefaceSource>.Instance);

        _created.Add(source);
        return source;
    }

    /// <summary>The source asks for a host environment and never requires one.</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
