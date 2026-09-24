using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.UnitTests.Domain.Marketing;

/// <summary>
/// What the newsletter signup will and will not write into <c>marketing.NewsletterSubscribers</c>
/// (HOME-10, doc 08 §3.6).
/// </summary>
public sealed class NewsletterEmailTests
{
    [Theory]
    [InlineData("shopper@example.com")]
    [InlineData("first.last+tag@sub.example.co.uk")]
    [InlineData("  spaced@example.com  ")]
    [InlineData("UPPER@EXAMPLE.COM")]
    [InlineData("digits123@example.lk")]
    public void A_plausible_address_is_accepted(string raw)
    {
        Assert.True(NewsletterEmail.TryNormalise(raw, out var email, out var normalised));

        Assert.Equal(raw.Trim(), email);
        Assert.Equal(raw.Trim().ToUpperInvariant(), normalised);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign.example.com")]
    [InlineData("@example.com")]
    [InlineData("shopper@")]
    [InlineData("two@at@example.com")]
    [InlineData("shopper@localhost")]
    [InlineData("shopper@example.c")]
    [InlineData("shopper@.example.com")]
    [InlineData("shopper@example..com")]
    [InlineData("shopper@example.com.")]
    [InlineData("spaced address@example.com")]
    [InlineData("tabbed\taddress@example.com")]
    [InlineData("newline\naddress@example.com")]
    public void An_address_this_store_will_not_store_is_rejected(string? raw)
    {
        Assert.False(NewsletterEmail.TryNormalise(raw, out var email, out var normalised));

        Assert.Equal(string.Empty, email);
        Assert.Equal(string.Empty, normalised);
    }

    [Fact]
    public void An_address_longer_than_the_column_is_rejected_rather_than_truncated()
    {
        var local = new string('a', NewsletterEmail.MaxLength);

        Assert.False(NewsletterEmail.TryNormalise($"{local}@example.com", out _, out _));

        // One character under the limit still fits, so the boundary is the column and not an
        // arbitrary number nearby.
        var fitting = new string('a', NewsletterEmail.MaxLength - "@example.com".Length);
        Assert.True(NewsletterEmail.TryNormalise($"{fitting}@example.com", out var email, out _));
        Assert.Equal(NewsletterEmail.MaxLength, email.Length);
    }

    /// <summary>
    /// The case of the local part is kept, because some mail servers treat it as significant, and
    /// the normalised form is what the unique index sees — so two spellings of one address cannot
    /// both be inserted.
    /// </summary>
    [Fact]
    public void Two_spellings_of_one_address_normalise_to_the_same_key()
    {
        Assert.True(NewsletterEmail.TryNormalise("Shopper@Example.com", out var first, out var firstKey));
        Assert.True(NewsletterEmail.TryNormalise("shopper@example.COM", out var second, out var secondKey));

        Assert.NotEqual(first, second);
        Assert.Equal(firstKey, secondKey);
    }

    [Theory]
    [InlineData("shopper@example.com", "sh***@example.com")]
    // A local part of one or two characters gives only its first away: showing both would be
    // showing the whole thing, which is the one case where masking has to reveal less, not more.
    [InlineData("ab@example.com", "a***@example.com")]
    [InlineData("a@example.com", "a***@example.com")]
    [InlineData("nonsense", "***")]
    public void Masking_keeps_an_address_out_of_a_log_file(string email, string expected)
    {
        Assert.Equal(expected, NewsletterEmail.Mask(email));
    }
}
