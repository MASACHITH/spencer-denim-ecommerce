using Microsoft.EntityFrameworkCore;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Identity;

/// <summary>
/// W3-01's migration against a real SQL Server: <c>identity.CustomerAddresses</c> exists with the columns
/// doc 08 §3.1 gives it, the filtered unique index it names, and the two foreign keys.
/// <para>
/// <b>The whole column set is asserted, not a sample</b> — <c>EmailOutboxSchemaTests</c>' argument, which
/// applies here for the same reason: doc 08 §3.1's table is the only description of this table anybody
/// will read, and an assertion per interesting column says nothing about a column that was forgotten.
/// </para>
/// </summary>
public sealed class CustomerAddressSchemaTests(SqlServerDatabaseFixture sqlServer)
    : SalesSchemaTestBase(sqlServer)
{
    /// <summary>Doc 08 §3.1's column list, plus the four audit columns its <b>[A]</b> tag adds.</summary>
    private static readonly string[] ExpectedColumns =
    [
        "City|nvarchar|NOT NULL",
        "CreatedAtUtc|datetime2(3)|NOT NULL",
        "CreatedByUserId|int|NULL",
        "DistrictId|int|NOT NULL",
        "Id|int|NOT NULL",
        "IsDefault|bit|NOT NULL",
        "Label|nvarchar|NULL",
        "Landmark|nvarchar|NULL",
        "Line1|nvarchar|NOT NULL",
        "Line2|nvarchar|NULL",
        "Phone|varchar|NOT NULL",
        "PostalCode|varchar|NULL",
        "RecipientName|nvarchar|NOT NULL",
        "UpdatedAtUtc|datetime2(3)|NULL",
        "UpdatedByUserId|int|NULL",
        "UserId|int|NOT NULL",
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "IX_CustomerAddresses_District (DistrictId)",
        "IX_CustomerAddresses_User (UserId, Id)",
        "PK_CustomerAddresses (Id) UNIQUE",
        "UX_CustomerAddresses_UserId_Default (UserId) UNIQUE FILTERED",
    ];

    [Fact]
    public async Task The_migration_created_the_table_in_the_identity_schema()
    {
        SqlServer.SkipIfUnavailable();

        var exists = await DbContext.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = 'identity' AND t.name = 'CustomerAddresses'
                """)
            .SingleAsync(Token);

        Assert.Equal(1, exists);
    }

    [Fact]
    public async Task The_columns_match_doc_08()
    {
        SqlServer.SkipIfUnavailable();

        var columns = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT c.name COLLATE DATABASE_DEFAULT + '|' + ty.name COLLATE DATABASE_DEFAULT
                    + CASE WHEN ty.name = 'datetime2' THEN '(' + CAST(c.scale AS varchar(10)) + ')' ELSE '' END
                    + '|' + CASE WHEN c.is_nullable = 1 THEN 'NULL' ELSE 'NOT NULL' END AS [Value]
                FROM sys.columns c
                JOIN sys.tables t ON t.object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                WHERE s.name = 'identity' AND t.name = 'CustomerAddresses'
                """)
            .ToListAsync(Token);

        Assert.Equal(ExpectedColumns, columns.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The widths doc 08 §3.1 gives. <c>max_length</c> is bytes, so an <c>nvarchar(n)</c> reads as
    /// <c>2n</c>.
    /// </summary>
    /// <remarks>
    /// <b>These are the numbers the form's <c>maxlength</c> and the validator both have to agree with.</b>
    /// #126's lesson was a <c>maxlength</c> of 120 against a 100-character column passing the form, the
    /// stub and sixteen browser tests, so the widths are pinned here against the document and the
    /// validator is pinned against the same constants in the unit suite.
    /// </remarks>
    [Fact]
    public async Task The_widths_match_doc_08()
    {
        SqlServer.SkipIfUnavailable();

        var widths = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT c.name COLLATE DATABASE_DEFAULT + '=' + CAST(c.max_length AS varchar(10)) AS [Value]
                FROM sys.columns c
                JOIN sys.tables t ON t.object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                WHERE s.name = 'identity' AND t.name = 'CustomerAddresses'
                  AND ty.name IN ('varchar', 'nvarchar')
                """)
            .ToListAsync(Token);

        Assert.Equal(
            [
                "City=160",
                "Label=80",
                "Landmark=300",
                "Line1=300",
                "Line2=300",
                "Phone=20",
                "PostalCode=10",
                "RecipientName=240",
            ],
            widths.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Doc 08 §3.1: <c>UX_CustomerAddresses_UserId_Default (UserId) WHERE IsDefault = 1</c>.
    /// </summary>
    /// <remarks>
    /// <b>The filter is asserted as text, not just the uniqueness.</b> The whole of "one default per
    /// customer" is that predicate: a filter that drifted to <c>IsDefault = 0</c> would still be a unique
    /// filtered index on <c>UserId</c> and would instead permit one default and refuse a second
    /// <i>non</i>-default address, which is a worse bug and reads the same in a model snapshot.
    /// <para>
    /// <c>IX_CustomerAddresses_User</c> is the unfiltered companion the address-book read needs; a
    /// filtered index cannot serve it. The two could have collapsed into one — same leading column, and
    /// EF treats a repeated <c>HasIndex</c> over the same property set as reconfiguring the first
    /// (<c>IndexDeclarationConventionTests</c>) — so both are named and both are asserted here.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task One_default_per_customer_is_the_databases_rule()
    {
        SqlServer.SkipIfUnavailable();

        var indexes = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT i.name COLLATE DATABASE_DEFAULT + ' ('
                    + STUFF((
                        SELECT ', ' + c.name COLLATE DATABASE_DEFAULT
                        FROM sys.index_columns ic
                        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                        ORDER BY ic.key_ordinal
                        FOR XML PATH('')), 1, 2, '')
                    + ')'
                    + CASE WHEN i.is_unique = 1 THEN ' UNIQUE' ELSE '' END
                    + CASE WHEN i.has_filter = 1 THEN ' FILTERED' ELSE '' END AS [Value]
                FROM sys.indexes i
                WHERE i.object_id = OBJECT_ID('identity.CustomerAddresses') AND i.name IS NOT NULL
                """)
            .ToListAsync(Token);

        Assert.Equal(ExpectedIndexes, indexes.Order(StringComparer.Ordinal));

        var filters = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT i.name COLLATE DATABASE_DEFAULT + ' ' + i.filter_definition COLLATE DATABASE_DEFAULT AS [Value]
                FROM sys.indexes i
                WHERE i.object_id = OBJECT_ID('identity.CustomerAddresses') AND i.has_filter = 1
                """)
            .ToListAsync(Token);

        Assert.Equal(
            ["UX_CustomerAddresses_UserId_Default ([IsDefault]=(1))"],
            filters.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Both foreign keys exist and neither cascades (doc 08 §1.6's default and its cascade allow-list,
    /// which names no table in the <c>identity</c> schema).
    /// </summary>
    /// <remarks>
    /// <b>A cascade from <c>Users</c> would be a PDPA hazard rather than a convenience.</b> §3.1's
    /// anonymisation deletes these rows itself with a set-based <c>ExecuteDelete</c>, and a user row is
    /// never hard-deleted; a cascade would mean the one path nobody expects — a hard delete of a user —
    /// silently taking addresses with it and bypassing the audited routine.
    /// </remarks>
    [Fact]
    public async Task Neither_foreign_key_cascades()
    {
        SqlServer.SkipIfUnavailable();

        var keys = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT fk.name COLLATE DATABASE_DEFAULT + ' -> '
                    + OBJECT_SCHEMA_NAME(fk.referenced_object_id) COLLATE DATABASE_DEFAULT + '.'
                    + OBJECT_NAME(fk.referenced_object_id) COLLATE DATABASE_DEFAULT
                    + ' ON DELETE ' + fk.delete_referential_action_desc COLLATE DATABASE_DEFAULT AS [Value]
                FROM sys.foreign_keys fk
                WHERE fk.parent_object_id = OBJECT_ID('identity.CustomerAddresses')
                """)
            .ToListAsync(Token);

        Assert.Equal(
            [
                "FK_CustomerAddresses_Districts_DistrictId -> ref.Districts ON DELETE NO_ACTION",
                "FK_CustomerAddresses_Users_UserId -> identity.Users ON DELETE NO_ACTION",
            ],
            keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// <c>identity.Users.DefaultDistrictId</c> finally has the foreign key doc 08 §3.1 gives it, and the
    /// index doc 08 §1.6's DB7 rule requires beside it.
    /// </summary>
    /// <remarks>
    /// <b>This column has been in the table since W1-01 with neither.</b> <c>AppUser</c>'s own remark
    /// names W3-01 as the change that owes both, "in the change that gives the column its first reader":
    /// the account profile (doc 11 P11 step 10) is that reader. Without the constraint a profile save
    /// could store a district id naming no district, and GLB-06's delivery estimate would then have no
    /// zone to read.
    /// </remarks>
    [Fact]
    public async Task The_users_default_district_is_a_real_foreign_key_with_its_index()
    {
        SqlServer.SkipIfUnavailable();

        var key = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT fk.name COLLATE DATABASE_DEFAULT + ' ON DELETE '
                    + fk.delete_referential_action_desc COLLATE DATABASE_DEFAULT AS [Value]
                FROM sys.foreign_keys fk
                JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
                JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
                WHERE fk.parent_object_id = OBJECT_ID('identity.Users') AND c.name = 'DefaultDistrictId'
                """)
            .ToListAsync(Token);

        Assert.Equal(["FK_Users_Districts_DefaultDistrictId ON DELETE NO_ACTION"], key);

        var leadsAnIndex = await DbContext.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID('identity.Users')
                  AND ic.key_ordinal = 1 AND ic.is_included_column = 0
                  AND c.name = 'DefaultDistrictId'
                """)
            .SingleAsync(Token);

        Assert.Equal(1, leadsAnIndex);
    }
}
