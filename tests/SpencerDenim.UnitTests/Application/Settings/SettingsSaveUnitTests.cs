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
    /// The three forms the screen shows: the logo and the shop's own details, the contact number with the
    /// three social addresses, and the bank details on their own.
    /// </summary>
    /// <remarks>
    /// <b><c>Store</c> and <c>Orders</c> are one unit since W3-04</b>, and that is a decision rather than a
    /// convenience. The logo has its own command, because changing it changes every page instantly; the
    /// invoice's legal details and the auto-deliver window sit on a second form beside it. Both forms read
    /// the unit's token, which <see cref="SettingCatalog.KeysInSaveUnitFor"/> guarantees is the token their
    /// save compares — so the one thing that cannot happen is the 409-on-every-save this file is about. The
    /// cost is that saving one of the two forms makes the other's token stale until the screen re-reads,
    /// and the screen re-reads after every save.
    /// </remarks>
    /// <remarks>
    /// <b>The bank details are their own save unit because they are guarded by a different permission.</b>
    /// A unit spanning <c>Bank</c> and any <c>Settings.Edit</c> group would hand a Store Manager a
    /// concurrency token covering rows they may not write — and the token is published for every row in the
    /// unit on purpose (so two readers with different permissions get the same token), which is precisely
    /// what would then leak the existence of the bank rows to a caller the read model hides them from.
    /// </remarks>
    [Fact]
    public void The_save_units_are_the_forms_the_screen_shows()
    {
        Assert.Equal(
            [
                new[] { SettingKeys.Store.Group, SettingKeys.Orders.Group },
                new[] { SettingKeys.Contact.Group, SettingKeys.Social.Group },
                new[] { SettingKeys.Bank.Group },
                new[] { SettingKeys.PayPal.Group },
            ],
            SettingCatalog.SaveUnits);
    }

    /// <summary>
    /// No save unit mixes permissions, which is the rule the bank group makes real.
    /// </summary>
    /// <remarks>
    /// A unit is a unit of <i>concurrency</i> and a group is a unit of <i>permission</i>; a unit spanning
    /// two permissions would publish one token over rows two different callers may write, so neither could
    /// save without the other's rows. Asserted over the catalogue rather than over the three known units,
    /// so a fourth form cannot be added wrongly.
    /// </remarks>
    [Fact]
    public void No_save_unit_spans_two_permissions()
    {
        foreach (var unit in SettingCatalog.SaveUnits)
        {
            var permissions = SettingCatalog.All
                .Where(definition => unit.Contains(definition.GroupKey, StringComparer.OrdinalIgnoreCase))
                .Select(definition => definition.RequiredPermission)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.True(
                permissions.Count <= 1,
                $"The save unit [{string.Join(", ", unit)}] spans the permissions "
                + $"[{string.Join(", ", permissions)}]. A form's concurrency token covers every row in its "
                + "unit, so a caller who may write only some of them could never save.");
        }
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
        string[] storeAndOrders =
        [
            SettingKeys.Store.LogoAssetId,
            SettingKeys.Store.LegalName,
            SettingKeys.Store.Address,
            SettingKeys.Store.BRNumber,
            SettingKeys.Store.VatNumber,
            SettingKeys.Orders.AutoDeliverAfterDays,

            // W4-01: PayPal's payment window is doc 08's Orders.PayPalExpiryMinutes, so it rides in this unit
            // (and with Settings.Edit, the permission this unit shares) rather than in PayPal's own.
            SettingKeys.PayPal.ExpiryMinutes,
        ];

        // Both group names answer with the same set, as with Contact and Social below: the screen may have
        // read the token from either group entry.
        Assert.Equal(
            storeAndOrders.Order(StringComparer.Ordinal),
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Store.Group).Order(StringComparer.Ordinal));
        Assert.Equal(
            storeAndOrders.Order(StringComparer.Ordinal),
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Orders.Group).Order(StringComparer.Ordinal));

        string[] contactAndSocial =
        [
            SettingKeys.Contact.WhatsApp,

            // W2-11 (GLB-10): the Contact page's own details. They join this save unit rather than getting
            // one of their own because they are one strip of one page, filled in from one email from the
            // client — which is the argument SettingCatalog.SaveUnits already makes for pairing Contact
            // with Social.
            SettingKeys.Contact.Phone,
            SettingKeys.Contact.Email,
            SettingKeys.Contact.Address,
            SettingKeys.Contact.OpeningHours,
            SettingKeys.Contact.MapEmbedUrl,

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
            SettingCatalog.KeysIn(SettingKeys.Bank.Group),
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Bank.Group));

        // `Bank` used to be the undeclared example and now has a unit of its own; `Store` was the
        // fall-through example and W3-04 paired it with `Orders`; `Payments.PayPal` was the next and W4-01
        // built it. An unbuilt group takes their place (doc 08 §3.8's `Email.Smtp`).
        Assert.Empty(SettingCatalog.KeysInSaveUnitFor("Email.Smtp"));
    }
}
