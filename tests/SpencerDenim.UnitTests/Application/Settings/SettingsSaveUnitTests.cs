using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Application.Settings;

/// <summary>
/// Which groups are saved in one request, and the invariant that keeps the concurrency check working.
/// <para>
/// <b>Why this file exists.</b> The first version of ADM-22 published a concurrency token <i>per group</i>
/// and then compared it, on the contact and social save, against the rows of <i>both</i> groups — because
/// one form saves both. Every valid save therefore came back <b>409</b>. The integration suite caught it
/// (seven failures, "Expected: NoContent / Actual: Conflict"), which is the right outcome and the expensive
/// one: the two sides of the comparison were written in two files and nothing made them agree.
/// </para>
/// <para>
/// <see cref="SettingCatalog.SaveUnits"/> is now the one declaration both sides read, and this is the test
/// that pins it. The assertion is deliberately phrased as "the rows a group's token covers are exactly the
/// rows its save compares", so a third group added to a form fails here rather than in a 409 on a client's
/// screen.
/// </para>
/// </summary>
public sealed class SettingsSaveUnitTests
{
    /// <summary>
    /// The two forms ADM-22's first slice ships: the logo on its own, and the contact number with the three
    /// social addresses.
    /// </summary>
    [Fact]
    public void The_save_units_are_the_forms_the_screen_shows()
    {
        Assert.Equal(
            [
                new[] { SettingKeys.Store.Group },
                new[] { SettingKeys.Contact.Group, SettingKeys.Social.Group },
            ],
            SettingCatalog.SaveUnits);
    }

    /// <summary>
    /// <b>The invariant.</b> A group's published token covers its whole save unit, which is the set of rows
    /// the matching write compares — so a token a screen read and sent straight back always matches, and a
    /// change to <i>any</i> row in the form is still a conflict.
    /// </summary>
    [Fact]
    public void Every_group_belongs_to_exactly_one_save_unit()
    {
        foreach (var groupKey in SettingCatalog.Groups)
        {
            var units = SettingCatalog.SaveUnits
                .Where(unit => unit.Contains(groupKey, StringComparer.Ordinal))
                .ToList();

            Assert.Single(units);
        }
    }

    [Fact]
    public void Every_group_in_a_save_unit_is_a_group_the_catalogue_knows()
    {
        foreach (var groupKey in SettingCatalog.SaveUnits.SelectMany(unit => unit))
        {
            Assert.Contains(groupKey, SettingCatalog.Groups);
        }
    }

    /// <summary>
    /// The keys a save unit covers, which is what both the read model's token and the handler's own target
    /// list are built from.
    /// </summary>
    [Fact]
    public void A_save_units_keys_are_the_keys_of_its_groups()
    {
        Assert.Equal(
            [SettingKeys.Store.LogoAssetId],
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Store.Group));

        string[] contactAndSocial =
        [
            SettingKeys.Contact.WhatsApp,
            SettingKeys.Social.FacebookUrl,
            SettingKeys.Social.InstagramUrl,
            SettingKeys.Social.TikTokUrl,
        ];

        // Both group names answer with the same set — that is the whole point: the screen may have read the
        // token from either group entry.
        Assert.Equal(
            contactAndSocial.Order(StringComparer.Ordinal),
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Contact.Group).Order(StringComparer.Ordinal));
        Assert.Equal(
            contactAndSocial.Order(StringComparer.Ordinal),
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Social.Group).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A group nobody has declared a save unit for answers with its own keys, so a key added to the catalogue
    /// in a new group is readable and writable rather than silently unsaveable.
    /// </summary>
    [Fact]
    public void A_group_with_no_declared_save_unit_is_its_own()
    {
        Assert.Equal(
            SettingCatalog.KeysIn(SettingKeys.Store.Group),
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Store.Group));

        Assert.Empty(SettingCatalog.KeysInSaveUnitFor("Bank"));
    }
}
