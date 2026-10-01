namespace SpencerDenim.Infrastructure.Persistence.Configurations.Catalog;

/// <summary>
/// The SQL form of <see cref="SpencerDenim.Domain.Catalog.CatalogSlug"/>'s spelling rule, for the CHECK
/// constraints on the slug columns that an admin can now write.
/// <para>
/// <b>Why a CHECK as well as the domain guard.</b> W1-03 is already down to add <c>CK_Products_Slug</c>
/// for exactly this reason (see <c>ProductConfiguration</c>): the guard in the property setter protects
/// writes that go through the application, and nothing else — a support script, a data fix, a restore from
/// a spreadsheet can all put a value in the column that makes a live page answer 400. Until W1-12 only the
/// seeder could write these two columns, so there was no argument for the constraint. There is now.
/// </para>
/// <para>
/// <b>The collation is the whole trick.</b> The database collation is <c>Latin1_General_100_CI_AS_SC</c>,
/// so <c>LIKE '%[^a-z0-9-]%'</c> against it accepts <c>DENIM</c> — case-insensitively, <c>A</c> <i>is</i>
/// in <c>[a-z]</c>. A case-sensitive collation is not enough either: in <c>CS_AS</c> the sort order
/// interleaves the cases (a A b B …), so <c>A</c> through <c>Z</c> still fall inside the range
/// <c>a-z</c>. Only a <b>binary</b> collation gives the ranges their ASCII meaning, which is the rule the
/// regular expression actually states. Verified against SQL Server on 21 inputs, including
/// <c>Denim</c>, <c>deniM</c>, <c>Z</c> and a Latin-1 accented character.
/// </para>
/// </summary>
internal static class CatalogSlugConstraint
{
    /// <summary>
    /// Binary, so <c>[a-z]</c> and <c>[0-9]</c> mean the ASCII ranges and nothing else. <c>100</c> matches
    /// the version of the database's own collation.
    /// </summary>
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    /// <summary>
    /// The CHECK expression for a slug column: lower-case letters, digits and single hyphens between them,
    /// never leading, trailing or doubled, never empty.
    /// </summary>
    /// <param name="column">The column name, without brackets.</param>
    public static string For(string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        var target = "[" + column + "]";

        return string.Join(
            " AND ",
            target + " COLLATE " + BinaryCollation + " NOT LIKE '%[^a-z0-9-]%'",
            target + " NOT LIKE '-%'",
            target + " NOT LIKE '%-'",
            target + " NOT LIKE '%--%'",
            "DATALENGTH(" + target + ") > 0");
    }
}
