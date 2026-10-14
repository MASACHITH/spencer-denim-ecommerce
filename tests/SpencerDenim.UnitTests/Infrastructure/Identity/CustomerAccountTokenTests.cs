using SpencerDenim.Infrastructure.Identity.Customers;

namespace SpencerDenim.UnitTests.Infrastructure.Identity;

/// <summary>
/// The one-time links a customer account needs (<see cref="CustomerAccountTokens"/>): the shape, the
/// verifier, and the three things a credential has to get right.
/// </summary>
/// <remarks>
/// <b>These are unit tests because the type is pure</b> — it mints and verifies strings and reads no
/// clock, no database and no request. What it is <i>used</i> for is covered end to end in
/// <c>GuestOrderLinkingTests</c> and <c>CustomerPasswordEndpointTests</c>, which redeem the real link out
/// of the real queued email; this file is about the primitive those rely on.
/// </remarks>
public sealed class CustomerAccountTokenTests
{
    private static readonly DateTime Expiry = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A minted token verifies against the row it was minted with, and nothing else does.</summary>
    [Fact]
    public void A_minted_token_verifies_against_its_own_stored_value()
    {
        var minted = CustomerAccountTokens.Mint(42, Expiry);

        var redeemed = CustomerAccountTokens.Verify(minted.Link, minted.StoredValue);

        Assert.NotNull(redeemed);
        Assert.Equal(Expiry, redeemed.Value.ExpiresAtUtc);
        Assert.Null(redeemed.Value.Subject);
    }

    /// <summary>
    /// <b>The stored value never contains the link.</b> A reader of the database — a backup, a staff
    /// member with a query tool, an attacker with a read — must not be able to mint a working link from a
    /// stored row. Only the SHA-256 of the verifier is kept.
    /// </summary>
    [Fact]
    public void The_stored_value_does_not_contain_the_verifier()
    {
        var minted = CustomerAccountTokens.Mint(7, Expiry);
        var verifier = minted.Link[(minted.Link.IndexOf('.', StringComparison.Ordinal) + 1)..];

        Assert.DoesNotContain(verifier, minted.StoredValue, StringComparison.Ordinal);

        // And the hash is a hash: 64 hex characters, which is what a SHA-256 looks like as text.
        var hash = minted.StoredValue.Split('|')[0];
        Assert.Equal(64, hash.Length);
        Assert.True(
            hash.All(Uri.IsHexDigit),
            $"The stored hash is not hexadecimal: {hash}");
    }

    /// <summary>
    /// A second mint is a different token, so "send me a new link" really does replace the old one.
    /// </summary>
    /// <remarks>
    /// <b>Two hundred mints, all distinct.</b> One comparison would pass against a generator that
    /// returned one of two values; the number is high enough to catch a seeded or
    /// time-derived generator and cheap enough to run in a unit suite.
    /// </remarks>
    [Fact]
    public void Every_mint_is_different()
    {
        var links = Enumerable.Range(0, 200)
            .Select(_ => CustomerAccountTokens.Mint(1, Expiry).Link)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(200, links.Count);
    }

    /// <summary>
    /// The verifier is URL-safe and unpadded, so it survives a query string and a mail client untouched.
    /// </summary>
    /// <remarks>
    /// <b>A <c>+</c> in a query string is a space.</b> Plain base64 would hand a shopper a link that
    /// worked for three tokens in four and silently failed for the fourth — the worst kind of defect,
    /// because it is intermittent and looks like the shop being flaky. Checked over 200 mints rather than
    /// one, because the alphabet only shows up when the random bytes happen to produce it.
    /// </remarks>
    [Fact]
    public void The_verifier_is_url_safe_and_unpadded()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var link = CustomerAccountTokens.Mint(1, Expiry).Link;
            var verifier = link[(link.IndexOf('.', StringComparison.Ordinal) + 1)..];

            Assert.DoesNotContain('+', verifier);
            Assert.DoesNotContain('/', verifier);
            Assert.DoesNotContain('=', verifier);
            Assert.Equal(verifier, Uri.EscapeDataString(verifier));
        }
    }

    /// <summary>Changing one character of the verifier makes it fail.</summary>
    /// <remarks>
    /// <b>Tested at both ends and in the middle.</b> A comparison that only looked at a prefix — or a
    /// hash that only covered part of the value — would pass a test that tampered with one position.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void A_tampered_verifier_does_not_verify(int offset)
    {
        var minted = CustomerAccountTokens.Mint(42, Expiry);
        var separator = minted.Link.IndexOf('.', StringComparison.Ordinal);
        var verifier = minted.Link[(separator + 1)..].ToCharArray();

        var index = offset >= 0 ? offset : verifier.Length + offset;
        verifier[index] = verifier[index] == 'A' ? 'B' : 'A';

        var tampered = minted.Link[..(separator + 1)] + new string(verifier);

        Assert.Null(CustomerAccountTokens.Verify(tampered, minted.StoredValue));
    }

    /// <summary>
    /// A token minted for one account does not verify against another's row.
    /// </summary>
    /// <remarks>
    /// <b>The selector is only a lookup key and the verifier is the credential</b>, so this is really a
    /// test that the two are checked together: swapping the selector finds the wrong row, whose hash does
    /// not match. Without it, a holder of any valid link could edit the number in front of the dot and
    /// try it against every account in the shop.
    /// </remarks>
    [Fact]
    public void A_token_for_one_account_does_not_verify_against_anothers_row()
    {
        var mine = CustomerAccountTokens.Mint(1, Expiry);
        var theirs = CustomerAccountTokens.Mint(2, Expiry);

        Assert.Null(CustomerAccountTokens.Verify(mine.Link, theirs.StoredValue));
        Assert.Null(CustomerAccountTokens.Verify(theirs.Link, mine.StoredValue));
    }

    /// <summary>The selector is read back, so the redemption can find the row by its primary key.</summary>
    [Fact]
    public void The_user_id_is_readable_from_the_link()
    {
        var minted = CustomerAccountTokens.Mint(123_456, Expiry);

        Assert.Equal(123_456, CustomerAccountTokens.ReadUserId(minted.Link));
    }

    /// <summary>
    /// A string that is not one of ours yields no user id, so nothing is looked up.
    /// </summary>
    /// <remarks>
    /// <b><c>0</c> and a negative number are refused as well as rubbish</b>, because <c>0</c> is what a
    /// careless parse produces and because a query for user <c>0</c> is a query that could in principle
    /// match a row written with a default.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rubbish")]
    [InlineData(".verifier")]
    [InlineData("0.verifier")]
    [InlineData("-1.verifier")]
    [InlineData("1x.verifier")]
    [InlineData(" 1.verifier")]
    [InlineData("1")]
    public void A_string_that_is_not_one_of_ours_has_no_user_id(string? token)
    {
        Assert.Null(CustomerAccountTokens.ReadUserId(token));
    }

    /// <summary>
    /// A confirmation token remembers the address it was minted for.
    /// </summary>
    /// <remarks>
    /// <b>This is what stops a link emailed to one address confirming another.</b> Doc 11 P11 step 10's
    /// email change is not built, and this is the half of it that was cheap to get right in advance
    /// rather than retrofit — the redemption compares the stored subject against the account's current
    /// address and refuses a mismatch.
    /// </remarks>
    [Fact]
    public void A_confirmation_token_carries_the_address_it_was_minted_for()
    {
        var minted = CustomerAccountTokens.Mint(9, Expiry, "NIMAL@EXAMPLE.COM");

        var redeemed = CustomerAccountTokens.Verify(minted.Link, minted.StoredValue);

        Assert.NotNull(redeemed);
        Assert.Equal("NIMAL@EXAMPLE.COM", redeemed.Value.Subject);
    }

    /// <summary>
    /// The expiry survives the round trip to the second, which is what the redemption compares.
    /// </summary>
    /// <remarks>
    /// <b>Stored as Unix seconds, so sub-second precision is deliberately lost.</b> Asserted to the
    /// second rather than exactly, because a test demanding exact equality would be asserting a
    /// representation rather than a behaviour — and would fail the first time somebody minted a token at
    /// a non-integral second.
    /// </remarks>
    [Fact]
    public void The_expiry_round_trips_to_the_second()
    {
        var odd = new DateTime(2026, 10, 14, 12, 0, 30, 456, DateTimeKind.Utc);

        var minted = CustomerAccountTokens.Mint(3, odd);
        var redeemed = CustomerAccountTokens.Verify(minted.Link, minted.StoredValue);

        Assert.NotNull(redeemed);
        Assert.Equal(DateTimeKind.Utc, redeemed.Value.ExpiresAtUtc.Kind);
        Assert.True(
            Math.Abs((redeemed.Value.ExpiresAtUtc - odd).TotalSeconds) < 1,
            $"The expiry came back as {redeemed.Value.ExpiresAtUtc:O} for {odd:O}.");
    }

    /// <summary>A stored value that is not one of ours never verifies.</summary>
    /// <remarks>
    /// <b>The missing-row case is the important one.</b> A redemption reads
    /// <c>identity.UserTokens</c> and gets null when there is no pending link; if that verified against
    /// anything, every spent link would start working again.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rubbish")]
    [InlineData("hash-with-no-expiry")]
    [InlineData("hash|not-a-number")]
    public void A_stored_value_that_is_not_ours_never_verifies(string? stored)
    {
        var minted = CustomerAccountTokens.Mint(1, Expiry);

        Assert.Null(CustomerAccountTokens.Verify(minted.Link, stored));
    }

    /// <summary>
    /// Expiry is not checked here, and that is the contract.
    /// </summary>
    /// <remarks>
    /// <b>Separating "is this the right secret" from "is it still in date" is what lets the redemption
    /// delete an expired row instead of leaving it to rot</b> — and it is why the caller holds the clock,
    /// as every use case in this codebase does rather than reading <c>DateTime.UtcNow</c> inside a helper.
    /// Pinned, because a well-meaning change that moved the check in here would make the redemption's
    /// clean-up unreachable and nothing else would notice.
    /// </remarks>
    [Fact]
    public void Verification_does_not_judge_the_expiry()
    {
        var expired = CustomerAccountTokens.Mint(1, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var redeemed = CustomerAccountTokens.Verify(expired.Link, expired.StoredValue);

        Assert.NotNull(redeemed);
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), redeemed.Value.ExpiresAtUtc);
    }
}
