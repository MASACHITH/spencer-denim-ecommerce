using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.UnitTests.Infrastructure.Identity;

public sealed class TwoFactorCodeFormatTests
{
    [Theory]
    [InlineData("123456", "123456")]
    [InlineData(" 123 456 ", "123456")]
    [InlineData("123-456", "123456")]
    [InlineData("12a456", "12a456")]
    [InlineData("   ", "")]
    public void Authenticator_codes_lose_only_spaces_and_hyphens(string typed, string expected) =>
        Assert.Equal(expected, TwoFactorCodeFormat.NormaliseAuthenticatorCode(typed));

    [Theory]
    [InlineData("ABCDE-FGHIJ", "ABCDE-FGHIJ")]
    [InlineData(" abcde-fghij ", "ABCDE-FGHIJ")]
    [InlineData("ABCDE - FGHIJ", "ABCDE-FGHIJ")]
    [InlineData("  ", "")]
    public void Recovery_codes_lose_spaces_and_case_but_keep_the_hyphen(string typed, string expected) =>
        Assert.Equal(expected, TwoFactorCodeFormat.NormaliseRecoveryCode(typed));

    [Fact]
    public void The_shared_key_is_shown_in_lower_case_groups_of_four()
    {
        Assert.Equal("abcd efgh ijkl mnop qr", TwoFactorCodeFormat.FormatSharedKey("ABCDEFGHIJKLMNOPQR"));
    }

    [Fact]
    public void The_authenticator_uri_escapes_the_label_and_repeats_the_issuer()
    {
        var uri = TwoFactorCodeFormat.BuildAuthenticatorUri("Spencer Denim Admin", "ada+ops@example.com", "KEY234");

        Assert.Equal(
            "otpauth://totp/Spencer%20Denim%20Admin:ada%2Bops%40example.com?secret=KEY234&issuer=Spencer%20Denim%20Admin&digits=6",
            uri);
    }
}
