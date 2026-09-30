using SpencerDenim.Domain.Media;

namespace SpencerDenim.UnitTests.Domain.Media;

/// <summary>
/// The traversal guard. A storage key reaches the filesystem, so every one of these is a path the server
/// must refuse to build — and the grammar is a whitelist, so a case nobody thought of is refused too.
/// </summary>
public sealed class MediaStorageKeyTests
{
    private static readonly Guid Id = Guid.Parse("2f1c0c8e-9a4b-4d31-8f2e-6b7c5a4d3e2f");

    [Fact]
    public void An_original_key_is_built_from_the_public_id_and_the_detected_extension()
    {
        var key = MediaStorageKey.ForOriginal(MediaVisibility.Public, Id, ".jpg");

        Assert.Equal("public/originals/2f1c0c8e-9a4b-4d31-8f2e-6b7c5a4d3e2f.jpg", key);
        Assert.True(MediaStorageKey.IsPublic(key));
    }

    [Fact]
    public void A_private_original_lands_on_the_private_volume_and_is_not_public()
    {
        var key = MediaStorageKey.ForOriginal(MediaVisibility.Private, Id, ".pdf");

        Assert.StartsWith(MediaStorageKey.PrivatePrefix, key, StringComparison.Ordinal);
        Assert.True(MediaStorageKey.IsValid(key));
        Assert.False(MediaStorageKey.IsPublic(key));

        Assert.True(MediaStorageKey.Validate(key, out var container, out _));
        Assert.Equal(MediaStorageContainer.Private, container);
    }

    /// <summary>Doc 08 §3.3's rendition convention, which has no table and must therefore be exact.</summary>
    [Fact]
    public void A_rendition_key_follows_the_documented_convention()
    {
        Assert.Equal(
            "public/renditions/2f1c0c8e-9a4b-4d31-8f2e-6b7c5a4d3e2f/card.webp",
            MediaStorageKey.ForRendition(Id, MediaRenditions.Card));

        Assert.Equal(
            "public/renditions/2f1c0c8e-9a4b-4d31-8f2e-6b7c5a4d3e2f",
            MediaStorageKey.RenditionFolderFor(Id));
    }

    [Fact]
    public void An_unknown_rendition_code_cannot_be_used_to_build_a_key()
    {
        Assert.Throws<ArgumentException>(() => MediaStorageKey.ForRendition(Id, "original"));
        Assert.Throws<ArgumentException>(() => MediaStorageKey.ForRendition(Id, "../../etc/passwd"));
    }

    /// <summary>
    /// The extension never comes from the upload — it is chosen from the detected signature — but the key
    /// builder refuses anything that is not a dot and a few letters anyway, so it cannot be turned into a
    /// path however it is called.
    /// </summary>
    [Theory]
    [InlineData(".jpg/../../etc/passwd")]
    [InlineData("jpg")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData(".toolongextension")]
    [InlineData(".jp g")]
    [InlineData(".jp\\g")]
    public void An_extension_that_is_not_a_plain_dot_and_letters_is_refused(string extension)
    {
        Assert.False(MediaStorageKey.IsSafeExtension(extension));
        Assert.Throws<ArgumentException>(() => MediaStorageKey.ForOriginal(MediaVisibility.Public, Id, extension));
    }

    /// <summary>
    /// <c>.exe</c> is a perfectly well-formed path component, so the key grammar accepts it. What stops an
    /// executable reaching storage is the signature allow-list, not this — the two answer different
    /// questions and the test says so, so nobody later conflates them.
    /// </summary>
    [Fact]
    public void A_dangerous_looking_extension_is_the_signature_checks_problem_not_the_key_grammars()
    {
        Assert.True(MediaStorageKey.IsSafeExtension(".exe"));
        Assert.Null(MediaFileSignature.ExtensionFor(MediaFileKind.WindowsExecutable));
    }

    [Theory]

    // Traversal, in the spellings that get tried.
    [InlineData("public/../private/originals/slip.pdf")]
    [InlineData("public/originals/../../../../etc/passwd")]
    [InlineData("public/originals/..")]
    [InlineData("public/./originals/a.jpg")]
    [InlineData("../public/originals/a.jpg")]
    [InlineData("public/originals/..%2f..%2fetc%2fpasswd")]
    [InlineData("public/originals/%2e%2e/%2e%2e/etc/passwd")]

    // Absolute and host-specific paths.
    [InlineData("/etc/passwd")]
    [InlineData("/public/originals/a.jpg")]
    [InlineData("C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("public\\originals\\a.jpg")]
    [InlineData("//server/share/a.jpg")]
    [InlineData("\\\\server\\share\\a.jpg")]

    // Neither container.
    [InlineData("originals/a.jpg")]
    [InlineData("renditions/a/card.webp")]
    [InlineData("publicx/originals/a.jpg")]

    // Shapes that resolve to the same file two ways, or to nothing.
    [InlineData("public//originals/a.jpg")]
    [InlineData("public/originals/")]
    [InlineData("public/originals/.hidden")]
    [InlineData("public/originals/a.jpg.")]
    [InlineData("public/")]
    [InlineData("public")]
    [InlineData("")]

    // Windows device names, which open a device rather than a file.
    [InlineData("public/originals/CON")]
    [InlineData("public/originals/nul.jpg")]
    [InlineData("public/originals/LPT1.png")]

    // Control characters and separators that a filesystem or a header would read differently.
    [InlineData("public/originals/a\0.jpg")]
    [InlineData("public/originals/a\n.jpg")]
    [InlineData("public/originals/a b.jpg")]
    [InlineData("public/originals/a:b.jpg")]
    [InlineData("public/originals/a\"b.jpg")]
    public void A_key_outside_the_grammar_is_refused(string key)
    {
        Assert.False(MediaStorageKey.IsValid(key), $"'{key}' should not be a valid storage key.");
    }

    [Theory]
    [InlineData("public/originals/demo/relaxed-linen-blend-shirt-1.jpg")]
    [InlineData("public/originals/2f1c0c8e-9a4b-4d31-8f2e-6b7c5a4d3e2f.webp")]
    [InlineData("public/renditions/2f1c0c8e-9a4b-4d31-8f2e-6b7c5a4d3e2f/zoom.webp")]
    [InlineData("private/originals/2f1c0c8e-9a4b-4d31-8f2e-6b7c5a4d3e2f.pdf")]
    [InlineData("public/a_b-c.1.png")]
    public void A_key_inside_the_grammar_is_accepted(string key)
    {
        Assert.True(MediaStorageKey.IsValid(key), $"'{key}' should be a valid storage key.");
    }

    /// <summary>The column is <c>varchar(400)</c>, so a longer key would be truncated or rejected by SQL.</summary>
    [Fact]
    public void A_key_longer_than_the_column_is_refused()
    {
        var key = "public/originals/" + new string('a', MediaStorageKey.MaxLength);

        Assert.False(MediaStorageKey.IsValid(key));
    }

    [Fact]
    public void A_segment_longer_than_the_limit_is_refused()
    {
        var key = "public/originals/" + new string('a', MediaStorageKey.MaxSegmentLength + 1) + ".jpg";

        Assert.False(MediaStorageKey.IsValid(key));
    }

    [Fact]
    public void The_reason_a_key_was_refused_is_reported_for_the_log()
    {
        Assert.False(MediaStorageKey.Validate("public/../private/x.pdf", out _, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    /// <summary>
    /// The one that matters most: no private key ever passes the public check, whatever it is spelled like.
    /// </summary>
    [Theory]
    [InlineData("private/originals/slip.pdf")]
    [InlineData("private/slip.pdf")]
    [InlineData("public/../private/slip.pdf")]
    [InlineData("PRIVATE/slip.pdf")]
    public void No_private_key_is_ever_public(string key)
    {
        Assert.False(MediaStorageKey.IsPublic(key));
    }
}
