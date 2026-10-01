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
    [InlineData("W293PDBX9F", "W293P-DBX9F")]
    [InlineData("w293p dbx9f", "W293P-DBX9F")]
    [InlineData("W29-3PD-BX9F", "W293P-DBX9F")]
    [InlineData("W293PDBX9", "W293PDBX9")]
    [InlineData("  ", "")]
    public void Recovery_codes_are_put_back_into_the_stored_shape_however_they_are_typed(string typed, string expected) =>
        Assert.Equal(expected, TwoFactorCodeFormat.NormaliseRecoveryCode(typed));

    [Fact]
    public void The_shared_key_is_shown_in_lower_case_groups_of_four()
    {
        Assert.Equal("abcd efgh ijkl mnop qr", TwoFactorCodeFormat.FormatSharedKey("ABCDEFGHIJKLMNOPQR"));
    }

    [Fact]
    public void The_authenticator_uri_escapes_the_label_and_repeats_the_issuer()
    {
        // A synthetic issuer, deliberately not the configured one: what is under test is that a space is
        // escaped and that the issuer is repeated, and both expectations are clearer written out in full
        // than assembled from the same constant the subject reads.
        var uri = TwoFactorCodeFormat.BuildAuthenticatorUri("Example Store Admin", "ada+ops@example.com", "KEY234");

        Assert.Equal(
            "otpauth://totp/Example%20Store%20Admin:ada%2Bops%40example.com?secret=KEY234&issuer=Example%20Store%20Admin&digits=6",
            uri);
    }
}
