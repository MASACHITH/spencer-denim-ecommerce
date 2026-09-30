using SpencerDenim.Infrastructure.Media;

namespace SpencerDenim.UnitTests.Infrastructure.Media;

/// <summary>
/// The uploaded filename is display data and nothing else — it never builds a key, a path or a content
/// type. It is still echoed back in JSON and shown in the library, so a malicious one has to come out.
/// </summary>
public sealed class SanitisedFileNameTests
{
    [Theory]
    [InlineData("lookbook.jpg", ".jpg", "lookbook.jpg")]
    [InlineData("Autumn Lookbook (final).png", ".png", "Autumn Lookbook (final).png")]

    // A path, in both spellings, keeps only the leaf.
    [InlineData("../../../../etc/passwd", ".jpg", "passwd.jpg")]
    [InlineData("C:\\Users\\admin\\secret.jpg", ".jpg", "secret.jpg")]
    [InlineData("/var/www/html/shell.php", ".jpg", "shell.jpg")]

    // A name that would break a header, a shell or a screen.
    [InlineData("a\"b;rm -rf /.jpg", ".jpg", "upload.jpg")]
    [InlineData("<script>alert(1)</script>.png", ".png", "script.png")]
    [InlineData("photo\r\nX-Injected: yes.jpg", ".jpg", "photoX-Injected yes.jpg")]

    // Nothing usable left, or nothing sent.
    [InlineData("", ".jpg", "upload.jpg")]
    [InlineData("...", ".jpg", "upload.jpg")]
    [InlineData("\u0000\u0001\u0002", ".webp", "upload.webp")]
    public void A_filename_is_reduced_to_something_safe_to_display(string input, string extension, string expected)
    {
        Assert.Equal(expected, MediaService.SanitiseFileName(input, extension));
    }

    /// <summary>
    /// The extension always comes from the detected signature, never from the name: a script called
    /// <c>.jpg</c> and a JPEG called <c>.exe</c> both end up named for what they actually are.
    /// </summary>
    [Fact]
    public void The_extension_is_the_detected_one_not_the_one_that_was_sent()
    {
        Assert.Equal("payload.jpg", MediaService.SanitiseFileName("payload.exe", ".jpg"));
        Assert.Equal("photo.webp", MediaService.SanitiseFileName("photo.jpg", ".webp"));
    }

    [Fact]
    public void A_very_long_name_is_cut_to_fit_the_column_and_the_filesystem()
    {
        var name = new string('x', 5000) + ".jpg";

        var sanitised = MediaService.SanitiseFileName(name, ".jpg");

        Assert.True(sanitised.Length <= 255);
        Assert.EndsWith(".jpg", sanitised, StringComparison.Ordinal);
    }
}
