using SpencerDenim.Domain.Branding;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Domain.Settings;

/// <summary>
/// The settings catalogue (<see cref="SettingCatalog"/>): which keys exist, and what guards each one.
/// <para>
/// Every assertion here is about a property the database cannot check on its own, or about a decision that
/// would be invisible in review. The CHECK constraints are proved against real SQL Server in
/// <c>SettingsSchemaTests</c>; this is the layer above them.
/// </para>
/// </summary>
public sealed class SettingCatalogTests
{
    /// <summary>
    /// <b>The trap, as a test.</b> ADM-22 says "store info" and doc 08 §3.8 does list <c>Name</c> in the
    /// <c>Store</c> group — but the owner's decision of 2026-10-02 (docs/02 §I2, W1-15) put the shop's name
    /// in exactly one constant per application with a convention test behind it. A settings row would give
    /// it two sources of truth, one of which the server renders the storefront from before any database is
    /// touched.
    /// <para>
    /// The convention test <c>BrandNameConventionTests</c> would <i>not</i> catch this: seeding
    /// <c>Store.Name</c> from <see cref="StoreBrand.Name"/> reads the constant rather than writing the
    /// literal, so the sweep stays green. This is the test that would go red, and it is deliberately
    /// phrased as "no settings key may be the shop's name" rather than "Store.Name is absent", so a key
    /// called <c>Branding.StoreName</c> or <c>Store.DisplayName</c> fails it too.
    /// </para>
    /// </summary>
    [Fact]
    public void The_shop_name_is_not_a_setting()
    {
        var nameLike = SettingCatalog.All
            .Where(definition =>
                definition.Key.EndsWith(".Name", StringComparison.OrdinalIgnoreCase)
                || definition.Key.Contains("StoreName", StringComparison.OrdinalIgnoreCase)
                || definition.Key.Contains("DisplayName", StringComparison.OrdinalIgnoreCase)
                || definition.Key.Contains("ShopName", StringComparison.OrdinalIgnoreCase)
                || definition.Key.Contains("BrandName", StringComparison.OrdinalIgnoreCase))
            .Select(definition => definition.Key)
            .ToList();

        Assert.True(
            nameLike.Count == 0,
            "The shop's name is a compile-time constant in one place per application (StoreBrand.Name and "
            + "BRAND in @spencer-denim/shared) by the owner's decision of 2026-10-02 (docs/02 §I2, W1-15). "
            + "It must not become an ops.Settings row: the server renders the storefront, so the name has to "
            + "resolve before the first byte of the shell, and two sources of truth for the shop's own name "
            + $"is the drift that decision exists to prevent. Offending key(s): {string.Join(", ", nameLike)}. "
            + "If the owner changes their mind, change docs/02 §I2 first.");

        Assert.Equal("Colombo Wardrobe", StoreBrand.Name);
    }

    /// <summary>
    /// <c>CK_Settings_IsSecret</c>, checked in the catalogue as well as in the database: a definition that
    /// got this wrong would make the seeder's insert fail at run time rather than at build time, and the
    /// failure would be a constraint name in a log.
    /// </summary>
    [Fact]
    public void A_key_is_secret_exactly_when_its_type_is_secret()
    {
        foreach (var definition in SettingCatalog.All)
        {
            Assert.Equal(definition.ValueType == SettingValueType.Secret, definition.IsSecret);
        }
    }

    /// <summary>
    /// Nothing this build seeds is a secret. Said as a test rather than left implicit, because the
    /// <see cref="SettingValueType.Secret"/> machinery is built and unused — so the day the first one is
    /// added, this test goes red and whoever adds it has to confirm the protector, the audit redaction and
    /// the read models are all in place rather than discovering them later.
    /// </summary>
    [Fact]
    public void No_key_this_build_seeds_is_a_secret()
    {
        Assert.DoesNotContain(SettingCatalog.All, definition => definition.IsSecret);
    }

    /// <summary>
    /// Every permission names a real one. A typo would produce a row nobody can read or write and no error
    /// anywhere — the screen would simply be missing a group.
    /// </summary>
    [Fact]
    public void Every_required_permission_is_in_the_permission_catalogue()
    {
        foreach (var definition in SettingCatalog.All)
        {
            Assert.Contains(definition.RequiredPermission, PermissionCatalog.All);
        }
    }

    /// <summary>
    /// Doc 08 §3.8 DB2: <c>Bank.*</c> and <c>Payments.*</c> need <c>Settings.Payments</c>;
    /// <c>Email.Smtp.*</c>, <c>Security.*</c> and <c>Privacy.RetainInvoiceBillTo</c> need
    /// <c>Settings.Security</c>. Nothing with those prefixes is seeded today, so the test is a tripwire for
    /// issue #53 and W3: add one of those keys with <c>Settings.Edit</c> on it and this goes red.
    /// </summary>
    [Theory]
    [InlineData("Bank.", "Settings.Payments")]
    [InlineData("Payments.", "Settings.Payments")]
    [InlineData("Email.Smtp.", "Settings.Security")]
    [InlineData("Security.", "Settings.Security")]
    public void A_sensitive_group_carries_the_permission_doc_08_gives_it(string prefix, string permission)
    {
        foreach (var definition in SettingCatalog.All.Where(candidate =>
            candidate.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            Assert.Equal(permission, definition.RequiredPermission);
        }
    }

    /// <inheritdoc cref="A_sensitive_group_carries_the_permission_doc_08_gives_it"/>
    [Fact]
    public void Invoice_bill_to_retention_would_carry_settings_security()
    {
        var retention = SettingCatalog.Find("Privacy.RetainInvoiceBillTo");

        // Not seeded by this build (doc 08 §3.8 R12 needs Settings.Security and no screen exists). The
        // assertion is conditional on purpose, so it becomes a real check the moment the key arrives.
        if (retention is not null)
        {
            Assert.Equal(Permissions.Settings.Security, retention.RequiredPermission);
        }
    }

    /// <summary>
    /// The keys this slice is meant to populate, named explicitly. A key quietly dropped from the catalogue
    /// would leave the settings screen with one box fewer and no test to notice.
    /// </summary>
    [Fact]
    public void The_five_keys_this_slice_populates_are_all_there_and_nothing_else_is()
    {
        string[] expected =
        [
            SettingKeys.Store.LogoAssetId,
            SettingKeys.Contact.WhatsApp,
            SettingKeys.Social.FacebookUrl,
            SettingKeys.Social.InstagramUrl,
            SettingKeys.Social.TikTokUrl,
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), SettingCatalog.All.Select(d => d.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_logo_is_a_media_asset_reference_rather_than_a_string()
    {
        var logo = SettingCatalog.Find(SettingKeys.Store.LogoAssetId);

        Assert.NotNull(logo);
        Assert.Equal(SettingValueType.MediaAssetRef, logo.ValueType);
        Assert.Equal(SettingKeys.Store.Group, logo.GroupKey);
        Assert.Equal(Permissions.Settings.Edit, logo.RequiredPermission);
    }

    /// <summary>
    /// Every key and group fits its column (doc 08 §3.8), so a truncation cannot reach SQL Server — where it
    /// would arrive as a <c>DbUpdateException</c> naming no field, at seeding time, on somebody's first run.
    /// </summary>
    [Fact]
    public void Every_definition_fits_the_columns_doc_08_specifies()
    {
        foreach (var definition in SettingCatalog.All)
        {
            Assert.InRange(definition.Key.Length, 1, SettingKeys.KeyMaxLength);
            Assert.InRange(definition.GroupKey.Length, 1, SettingKeys.GroupKeyMaxLength);
            Assert.InRange(definition.RequiredPermission.Length, 1, SettingKeys.RequiredPermissionMaxLength);
            Assert.InRange(definition.Description?.Length ?? 1, 1, SettingKeys.DescriptionMaxLength);
        }
    }

    /// <summary>
    /// A key is dotted and starts with its own group, which is what makes <c>IX_Settings_GroupKey</c> and the
    /// key itself agree about what a group is — and what lets doc 08's DB2 rule be read off a key's prefix.
    /// </summary>
    [Fact]
    public void Every_key_is_dotted_and_begins_with_its_group()
    {
        foreach (var definition in SettingCatalog.All)
        {
            Assert.StartsWith(definition.GroupKey + ".", definition.Key, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", definition.Key, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_key_has_a_description_a_staff_user_can_act_on()
    {
        foreach (var definition in SettingCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Description), definition.Key);
        }
    }

    [Fact]
    public void Keys_are_unique_so_the_seeder_cannot_insert_two_rows_for_one()
    {
        Assert.Equal(
            SettingCatalog.All.Count,
            SettingCatalog.All.Select(definition => definition.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Groups_are_listed_once_each_in_catalogue_order()
    {
        Assert.Equal(["Store", "Contact", "Social"], SettingCatalog.Groups);
    }

    [Fact]
    public void Keys_in_a_group_are_the_keys_whose_group_it_is()
    {
        Assert.Equal([SettingKeys.Store.LogoAssetId], SettingCatalog.KeysIn(SettingKeys.Store.Group));

        Assert.Equal(
            [SettingKeys.Social.FacebookUrl, SettingKeys.Social.InstagramUrl, SettingKeys.Social.TikTokUrl],
            SettingCatalog.KeysIn(SettingKeys.Social.Group));

        // A group nobody has defined is an empty list, not an exception: the read model iterates groups.
        Assert.Empty(SettingCatalog.KeysIn("Bank"));
    }

    [Fact]
    public void Find_answers_null_for_a_key_this_build_does_not_know()
    {
        Assert.Null(SettingCatalog.Find("Bank.AccountNumber"));
        Assert.Null(SettingCatalog.Find(null));
        Assert.Null(SettingCatalog.Find("Store.Logo"));
    }

    /// <summary>
    /// <b>And a key found case-insensitively, because the database is.</b>
    /// <para>
    /// The collation is <c>Latin1_General_100_CI_AS_SC</c>, so <c>social.facebookurl</c> and
    /// <c>Social.FacebookUrl</c> are one row to SQL Server. This test used to assert the opposite — that
    /// <c>store.logoassetid</c> was unknown — which pinned an <c>Ordinal</c> lookup whose consequence was
    /// permanent: a row scripted in with different casing is found by SQL and realigned by the seeder, which
    /// reports success, and then missed by every ordinal dictionary, so the box disappears from the screen and
    /// every save answers <c>NotSeeded</c> (a 500) for ever.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("store.logoassetid")]
    [InlineData("STORE.LOGOASSETID")]
    [InlineData("Store.LogoAssetId")]
    public void Find_matches_a_key_however_it_is_cased(string key)
    {
        Assert.Equal(SettingKeys.Store.LogoAssetId, SettingCatalog.Find(key)?.Key);
    }

    /// <summary>
    /// <see cref="SettingDefinition.Label"/> is "a name a person recognises" (doc 08 §3.3), which is what the
    /// media library's "used in…" panel shows. The first version used <c>Description</c> there, so a
    /// 185-character paragraph arrived beside entries like "Wide-Leg Rinse Jeans".
    /// </summary>
    [Fact]
    public void Every_key_has_a_short_label_that_reads_as_a_name()
    {
        foreach (var definition in SettingCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Label), definition.Key);
            Assert.InRange(definition.Label.Length, 1, SettingDefinition.LabelMaxLength);

            // A name, not a sentence: no full stop, and distinctly shorter than the description beside it.
            Assert.DoesNotContain(".", definition.Label, StringComparison.Ordinal);
            Assert.True(
                definition.Label.Length < (definition.Description?.Length ?? int.MaxValue),
                $"{definition.Key}'s label is not shorter than its description.");
        }
    }
}
