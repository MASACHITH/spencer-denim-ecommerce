using System.Security.Cryptography;
using System.Text;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Settings;

/// <summary>
/// The concurrency token for a <i>group</i> of settings, derived from its rows' own
/// <c>rowversion</c> values.
/// <para>
/// <b>Why a group token rather than one per key.</b> A settings screen saves a group — the contact
/// number and three social addresses are one form with one Save button — so "has anything in this group
/// changed since you loaded it" is the question the screen can actually ask, and per-key tokens would
/// make the client carry four of them and the server decide what to do when two are current and two are
/// not. Doc 08 marks the table <c>[RV]</c>; this is that token, lifted to the unit a person edits.
/// </para>
/// <para>
/// <b>It does not replace the row-level check, it precedes it.</b> EF still puts each row's
/// <c>RowVersion</c> in the UPDATE's WHERE clause, so a change that lands between this comparison and the
/// save is still refused — exactly the two-layer arrangement <c>CatalogWriteSupport</c> documents. This
/// layer is what turns the common case ("somebody else saved while your form was open") into a sentence
/// instead of a <c>DbUpdateConcurrencyException</c>.
/// </para>
/// <para>
/// <b>A hash rather than the bytes concatenated</b>, so the token is 32 bytes whatever the group's size:
/// the <c>Store</c> group has one key today and doc 08's <c>Payments.PayPal</c> group has eleven, and a
/// token whose length tracked the group would overflow the fixed buffer
/// <see cref="SettingFieldRules.ReadRowVersion"/> reads into. It is not a secret and is not meant to be
/// one — it is an ETag, and an attacker who can guess it can at best replay a save they could already
/// make.
/// </para>
/// </summary>
public static class SettingsGroupVersion
{
    /// <summary>
    /// The token for <paramref name="rows"/>, which must be the group's rows. Order is normalised here
    /// rather than assumed, because a reader that changed its <c>ORDER BY</c> would otherwise invalidate
    /// every open form with nothing to say why.
    /// </summary>
    public static string For(IEnumerable<Setting> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var builder = new StringBuilder();

        // Ordered case-insensitively so the token does not depend on how a row's key happens to be cased in
        // the database — both sides read the same rows, but an Ordinal sort would put `social.x` and
        // `Social.Y` in a different order than a reader that had normalised them.
        foreach (var row in rows.OrderBy(row => row.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(row.Key).Append('\u0000');

            // A row with no token yet — inserted by a script rather than by SQL Server — contributes a
            // marker rather than nothing, so "no version" and "empty version" are different tokens.
            builder
                .Append(row.RowVersion is { Length: > 0 } version ? Convert.ToBase64String(version) : "-")
                .Append('\u001e');
        }

        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// Whether <paramref name="supplied"/> is the group's current token.
    /// <para>
    /// A missing or malformed token is <b>not</b> treated as "no opinion", for the reason
    /// <c>CatalogWriteSupport</c> gives: a client that did not send one cannot have seen the current rows,
    /// and allowing the write anyway is how optimistic concurrency becomes decorative. The comparison is
    /// in fixed time — not because the token is secret, but because a timing-variable comparison in a
    /// security-adjacent path is the kind of thing that gets copied into one where it matters.
    /// </para>
    /// </summary>
    public static bool Matches(IEnumerable<Setting> rows, string? supplied)
    {
        if (SettingFieldRules.ReadRowVersion(supplied) is not { } expected)
        {
            return false;
        }

        var current = Convert.FromBase64String(For(rows));

        return CryptographicOperations.FixedTimeEquals(expected, current);
    }
}
