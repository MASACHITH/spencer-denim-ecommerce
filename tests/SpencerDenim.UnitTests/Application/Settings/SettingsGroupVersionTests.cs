using SpencerDenim.Application.Settings;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Application.Settings;

/// <summary>
/// The group concurrency token (<see cref="SettingsGroupVersion"/>), doc 08 §1.5 <c>[RV]</c>.
/// <para>
/// Two staff users on one settings screen is the ordinary case, and without this the second save silently
/// overwrites the first while both audit rows claim to have changed the value from what the screen last
/// showed. The tests below are about the two ways that can quietly stop working: a token that does not move
/// when a row does, and a missing token being read as "no opinion".
/// </para>
/// </summary>
public sealed class SettingsGroupVersionTests
{
    [Fact]
    public void The_token_is_stable_for_unchanged_rows()
    {
        var rows = Rows(("Social.FacebookUrl", 1), ("Social.InstagramUrl", 2));

        Assert.Equal(SettingsGroupVersion.For(rows), SettingsGroupVersion.For(Rows(("Social.FacebookUrl", 1), ("Social.InstagramUrl", 2))));
    }

    /// <summary>
    /// Reading order must not matter: a reader that changed its <c>ORDER BY</c> would otherwise invalidate
    /// every open settings form with nothing on any screen to say why.
    /// </summary>
    [Fact]
    public void The_token_does_not_depend_on_the_order_the_rows_arrive_in()
    {
        var forwards = Rows(("Social.FacebookUrl", 1), ("Social.InstagramUrl", 2), ("Social.TikTokUrl", 3));
        var backwards = Rows(("Social.TikTokUrl", 3), ("Social.InstagramUrl", 2), ("Social.FacebookUrl", 1));

        Assert.Equal(SettingsGroupVersion.For(forwards), SettingsGroupVersion.For(backwards));
    }

    [Fact]
    public void The_token_moves_when_any_rows_version_moves()
    {
        var before = SettingsGroupVersion.For(Rows(("Social.FacebookUrl", 1), ("Social.InstagramUrl", 2)));
        var after = SettingsGroupVersion.For(Rows(("Social.FacebookUrl", 1), ("Social.InstagramUrl", 9)));

        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// The token covers <i>which</i> rows there are, not only their versions — otherwise a key added to or
    /// removed from the group would leave an open form's token still matching.
    /// </summary>
    [Fact]
    public void The_token_moves_when_the_set_of_keys_changes()
    {
        var two = SettingsGroupVersion.For(Rows(("Social.FacebookUrl", 1), ("Social.InstagramUrl", 2)));
        var three = SettingsGroupVersion.For(
            Rows(("Social.FacebookUrl", 1), ("Social.InstagramUrl", 2), ("Social.TikTokUrl", 3)));

        Assert.NotEqual(two, three);
    }

    /// <summary>
    /// Two rows swapping their versions must not produce the same token. A token built by concatenating the
    /// version bytes without the keys would — and that is the bug this pairs each version with its key to
    /// avoid.
    /// </summary>
    [Fact]
    public void Two_rows_swapping_versions_is_not_the_same_token()
    {
        var one = SettingsGroupVersion.For(Rows(("Social.FacebookUrl", 1), ("Social.InstagramUrl", 2)));
        var other = SettingsGroupVersion.For(Rows(("Social.FacebookUrl", 2), ("Social.InstagramUrl", 1)));

        Assert.NotEqual(one, other);
    }

    /// <summary>
    /// A row with no <c>rowversion</c> yet — inserted by a script rather than by SQL Server — contributes a
    /// marker rather than nothing, so "no version" and "empty version" are different tokens.
    /// </summary>
    [Fact]
    public void A_row_with_no_version_is_distinguishable_from_one_with_an_empty_version()
    {
        var missing = new[] { new Setting { Key = "Social.FacebookUrl", RowVersion = null } };
        var empty = new[] { new Setting { Key = "Social.FacebookUrl", RowVersion = [] } };

        // Both are "no token", so they agree with each other, and neither is confused with a real version.
        Assert.Equal(SettingsGroupVersion.For(missing), SettingsGroupVersion.For(empty));
        Assert.NotEqual(SettingsGroupVersion.For(missing), SettingsGroupVersion.For(Rows(("Social.FacebookUrl", 1))));
    }

    [Fact]
    public void The_token_is_a_fixed_length_whatever_the_groups_size()
    {
        var one = Convert.FromBase64String(SettingsGroupVersion.For(Rows(("A.B", 1))));
        var eleven = Convert.FromBase64String(SettingsGroupVersion.For(
            Enumerable.Range(1, 11).Select(i => new Setting { Key = $"Payments.PayPal.K{i}", RowVersion = [(byte)i] }).ToList()));

        // 32 bytes, so it always fits the fixed buffer SettingFieldRules.ReadRowVersion reads into — which a
        // token whose length tracked the group's size would overflow for doc 08's eleven-key PayPal group.
        Assert.Equal(32, one.Length);
        Assert.Equal(32, eleven.Length);
        Assert.NotNull(SettingFieldRules.ReadRowVersion(SettingsGroupVersion.For(Rows(("A.B", 1)))));
    }

    // ---- Matches ---------------------------------------------------------------------------------

    [Fact]
    public void The_current_token_matches()
    {
        var rows = Rows(("Social.FacebookUrl", 1), ("Social.InstagramUrl", 2));

        Assert.True(SettingsGroupVersion.Matches(rows, SettingsGroupVersion.For(rows)));
    }

    [Fact]
    public void A_token_from_before_a_change_does_not_match()
    {
        var stale = SettingsGroupVersion.For(Rows(("Social.FacebookUrl", 1)));

        Assert.False(SettingsGroupVersion.Matches(Rows(("Social.FacebookUrl", 2)), stale));
    }

    /// <summary>
    /// A missing or malformed token is refused rather than taken as "no opinion". A client that did not send
    /// one cannot have seen the current rows, and allowing the write anyway is how optimistic concurrency
    /// becomes decorative — the argument <c>CatalogWriteSupport</c> already makes for ADM-11.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!!")]
    [InlineData("AAAA")]
    public void A_missing_or_malformed_token_does_not_match(string? supplied)
    {
        Assert.False(SettingsGroupVersion.Matches(Rows(("Social.FacebookUrl", 1)), supplied));
    }

    /// <summary>
    /// A token of the right length but the wrong contents. Separate from the malformed cases because it is the
    /// one an attacker would send, and because a comparison that only checked the length would pass it.
    /// </summary>
    [Fact]
    public void A_token_of_the_right_shape_and_the_wrong_value_does_not_match()
    {
        Assert.False(SettingsGroupVersion.Matches(Rows(("Social.FacebookUrl", 1)), Convert.ToBase64String(new byte[32])));
    }

    /// <summary>
    /// A token for a <i>different</i> group. Reachable from a screen that posted the Store group's token to
    /// the contact endpoint, which is a client bug rather than an attack — and has to be a refusal either way.
    /// </summary>
    [Fact]
    public void Another_groups_token_does_not_match()
    {
        var storeToken = SettingsGroupVersion.For(Rows(("Store.LogoAssetId", 1)));

        Assert.False(SettingsGroupVersion.Matches(Rows(("Social.FacebookUrl", 1)), storeToken));
    }

    private static List<Setting> Rows(params (string Key, byte Version)[] rows) =>
        rows.Select(row => new Setting
        {
            Key = row.Key,
            GroupKey = row.Key.Split('.')[0],
            RowVersion = [0, 0, 0, 0, 0, 0, 0, row.Version],
        }).ToList();
}
