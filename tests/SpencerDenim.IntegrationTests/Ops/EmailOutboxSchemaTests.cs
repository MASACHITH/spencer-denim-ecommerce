using Microsoft.EntityFrameworkCore;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Ops;

/// <summary>
/// W3-06's migration against a real SQL Server: <c>ops.EmailOutbox</c> exists with the columns doc 08
/// §3.8 gives it, the index it names, and the two it implies.
/// <para>
/// <b>The whole column set is asserted, not a sample.</b> An assertion per interesting column proves
/// those columns are right and says nothing about one that was forgotten or one that was invented — the
/// argument <c>WishlistSchemaTests</c> makes, and the reason it matters more here is that doc 08 §3.8 is
/// the only description of this table anybody will read.
/// </para>
/// </summary>
public sealed class EmailOutboxSchemaTests(SqlServerDatabaseFixture sqlServer)
    : SalesSchemaTestBase(sqlServer)
{
    /// <summary>Doc 08 §3.8's column list, in full.</summary>
    private static readonly string[] ExpectedColumns =
    [
        "AttemptCount|smallint|NOT NULL",
        "BodyHtml|nvarchar|NULL",
        "BodyText|nvarchar|NULL",
        "CreatedAtUtc|datetime2(3)|NOT NULL",
        "DeduplicationKey|varchar|NULL",
        "Id|bigint|NOT NULL",
        "LastError|nvarchar|NULL",
        "LockedUntilUtc|datetime2(3)|NULL",
        "MaxAttempts|smallint|NOT NULL",
        "NextAttemptAtUtc|datetime2(3)|NOT NULL",
        "RelatedEntityId|varchar|NULL",
        "RelatedEntityType|varchar|NULL",
        "SentAtUtc|datetime2(3)|NULL",
        "Status|tinyint|NOT NULL",
        "Subject|nvarchar|NOT NULL",
        "TemplateCode|varchar|NOT NULL",
        "ToEmail|nvarchar|NOT NULL",
        "ToName|nvarchar|NULL",
        "TransportOverrideProtected|nvarchar|NULL",
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "IX_EmailOutbox_Due (NextAttemptAtUtc) FILTERED",
        "IX_EmailOutbox_Stuck (LockedUntilUtc) FILTERED",
        "PK_EmailOutbox (Id) UNIQUE",
        "UX_EmailOutbox_Dedup (DeduplicationKey) UNIQUE FILTERED",
    ];

    [Fact]
    public async Task The_migration_created_the_table_in_the_ops_schema()
    {
        SqlServer.SkipIfUnavailable();

        var exists = await DbContext.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = 'ops' AND t.name = 'EmailOutbox'
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
                WHERE s.name = 'ops' AND t.name = 'EmailOutbox'
                """)
            .ToListAsync(Token);

        Assert.Equal(ExpectedColumns, columns.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The widths doc 08 §3.8 gives, which the column-type query above cannot show.
    /// </summary>
    /// <remarks>
    /// <b><c>max_length</c> is bytes</b>, so an <c>nvarchar(n)</c> reads as <c>2n</c> and an
    /// <c>nvarchar(max)</c> as <c>-1</c>. Asserted because the widths are the document's: a
    /// <c>Subject</c> of <c>nvarchar(200)</c> is the reason the composer's subjects are checked against
    /// 200 in the unit suite, and the two numbers have to be the same one.
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
                WHERE s.name = 'ops' AND t.name = 'EmailOutbox'
                  AND ty.name IN ('varchar', 'nvarchar')
                """)
            .ToListAsync(Token);

        Assert.Equal(
            [
                "BodyHtml=-1",
                "BodyText=-1",
                "DeduplicationKey=150",
                "LastError=4000",
                "RelatedEntityId=100",
                "RelatedEntityType=40",
                "Subject=400",
                "TemplateCode=50",
                "ToEmail=512",
                "ToName=240",
                "TransportOverrideProtected=-1",
            ],
            widths.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// <c>IX_EmailOutbox_Due (NextAttemptAtUtc) WHERE Status IN (0,3)</c> is doc 08 §3.8's own index, and
    /// the other two are named explicitly beside it.
    /// </summary>
    /// <remarks>
    /// <b>The filters are asserted as text, and that is the point of this test rather than the names.</b>
    /// A filtered index is only used when the query's predicate provably implies the filter (DB28), so a
    /// filter that drifted by one status would leave the sweep scanning a table full of message bodies
    /// with nothing failing. <c>IX_EmailOutbox_Stuck</c> exists for the opposite reason — recovering a
    /// crashed sender's lease needs <c>Status = 1</c>, which the due index deliberately excludes.
    /// <para>
    /// The two cannot have collapsed into one: different key column, different filter. EF's index-collapsing
    /// convention is about foreign-key indexes and this table has no foreign key, but both are named
    /// explicitly anyway, which is what makes a collapse impossible to happen quietly.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_indexes_and_their_filters_are_the_ones_the_sweep_depends_on()
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
                WHERE i.object_id = OBJECT_ID('ops.EmailOutbox') AND i.name IS NOT NULL
                """)
            .ToListAsync(Token);

        Assert.Equal(ExpectedIndexes, indexes.Order(StringComparer.Ordinal));

        var filters = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT i.name COLLATE DATABASE_DEFAULT + ' ' + i.filter_definition COLLATE DATABASE_DEFAULT AS [Value]
                FROM sys.indexes i
                WHERE i.object_id = OBJECT_ID('ops.EmailOutbox') AND i.has_filter = 1
                """)
            .ToListAsync(Token);

        Assert.Equal(
            [
                // SQL Server stores the predicate as it was written, so the `IN` survives rather than
                // being expanded to two ORs. Asserted in the server's own spelling, because the thing
                // being checked is what the engine will compare the sweep's predicate against.
                "IX_EmailOutbox_Due ([Status] IN ((0), (3)))",
                "IX_EmailOutbox_Stuck ([Status]=(1))",
                "UX_EmailOutbox_Dedup ([DeduplicationKey] IS NOT NULL)",
            ],
            filters.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Doc 08 §3.8: the table is "related to Orders with no FK".
    /// </summary>
    /// <remarks>
    /// Asserted rather than assumed, because a key here would be invisible until the retention purge this
    /// table owes tried to delete a row and found an order holding it. The rows are kept one year and an
    /// order seven (§8.4), so the two cannot share a lifetime.
    /// </remarks>
    [Fact]
    public async Task It_has_no_foreign_key_to_anything()
    {
        SqlServer.SkipIfUnavailable();

        var keys = await DbContext.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                FROM sys.foreign_keys
                WHERE parent_object_id = OBJECT_ID('ops.EmailOutbox')
                """)
            .SingleAsync(Token);

        Assert.Equal(0, keys);
    }

    /// <summary>
    /// Doc 08 §3.8 lists <c>EmailOutbox</c> among the tables that are <b>never audited</b>, "because it
    /// may contain reset links" — so it carries no audit column pair, and no soft-delete flag either.
    /// </summary>
    /// <remarks>
    /// The same assertion <c>CartModelTests</c> makes for the two cart tables, and for the same reason:
    /// "never audited" is a sentence in a document until something checks that the columns the audit
    /// interceptor looks for are absent.
    /// </remarks>
    [Fact]
    public async Task It_carries_no_audit_columns_and_no_soft_delete_flag()
    {
        SqlServer.SkipIfUnavailable();

        var columns = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT c.name COLLATE DATABASE_DEFAULT AS [Value]
                FROM sys.columns c
                WHERE c.object_id = OBJECT_ID('ops.EmailOutbox')
                  AND c.name IN ('CreatedByUserId', 'UpdatedByUserId', 'UpdatedAtUtc', 'IsDeleted',
                                 'DeletedAtUtc', 'DeletedByUserId', 'RowVersion')
                """)
            .ToListAsync(Token);

        Assert.Empty(columns);
    }

    /// <summary>
    /// <c>CK_EmailOutbox_Status</c> has teeth: status 6 does not exist.
    /// </summary>
    /// <remarks>
    /// <b>A constraint is proved by violating it</b>, never by reading the migration. The value 6 is one
    /// past <c>Cancelled</c>, which is the boundary the CK is actually about — a test using 99 would pass
    /// against a CK that allowed 0 to 9.
    /// </remarks>
    [Fact]
    public async Task A_status_outside_doc_08s_list_is_refused_by_the_database()
    {
        SqlServer.SkipIfUnavailable();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            DbContext.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ops.EmailOutbox
                    (TemplateCode, ToEmail, Subject, Status, AttemptCount, MaxAttempts, NextAttemptAtUtc, CreatedAtUtc)
                VALUES ('OrderPlaced', 'ck@example.com', 'CK probe', 6, 0, 6, sysutcdatetime(), sysutcdatetime());
                """,
                Token));

        Assert.Contains("CK_EmailOutbox_Status", Flatten(exception), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>CK_EmailOutbox_Attempts</c> has teeth as well: a ceiling of zero would make a message dead on
    /// arrival, and a negative counter would strand it.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 6)]
    public async Task An_impossible_attempt_count_is_refused_by_the_database(int attempts, int max)
    {
        SqlServer.SkipIfUnavailable();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            DbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO ops.EmailOutbox
                    (TemplateCode, ToEmail, Subject, Status, AttemptCount, MaxAttempts, NextAttemptAtUtc, CreatedAtUtc)
                VALUES ('OrderPlaced', 'ck@example.com', 'CK probe', 0, {attempts}, {max}, sysutcdatetime(), sysutcdatetime());
                """,
                Token));

        Assert.Contains("CK_EmailOutbox_Attempts", Flatten(exception), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>UX_EmailOutbox_Dedup</c> has teeth, and its filter means any number of rows may carry no key —
    /// which is every row this build writes.
    /// </summary>
    /// <remarks>
    /// Both halves matter. A plain unique index would allow exactly one keyless row in the whole table,
    /// which would make the <i>second</i> order email the shop ever queued fail; and an index that did not
    /// enforce uniqueness at all would be no use to the account emails it exists for (W3-01).
    /// </remarks>
    [Fact]
    public async Task The_deduplication_key_is_unique_when_present_and_unconstrained_when_absent()
    {
        SqlServer.SkipIfUnavailable();

        var key = $"probe-{Guid.NewGuid():N}";

        await InsertAsync(key);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => InsertAsync(key));
        Assert.Contains("UX_EmailOutbox_Dedup", Flatten(exception), StringComparison.Ordinal);

        // Two keyless rows: allowed, because the index is filtered on IS NOT NULL.
        await InsertAsync(null);
        await InsertAsync(null);
    }

    private Task<int> InsertAsync(string? deduplicationKey) =>
        DbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO ops.EmailOutbox
                (TemplateCode, DeduplicationKey, ToEmail, Subject, Status, AttemptCount, MaxAttempts,
                 NextAttemptAtUtc, CreatedAtUtc)
            VALUES ('OrderPlaced', {deduplicationKey}, 'dedup@example.com', 'Dedup probe', 0, 0, 6,
                    sysutcdatetime(), sysutcdatetime());
            """,
            Token);

    private static string Flatten(Exception exception)
    {
        var text = string.Empty;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            text += current.Message;
        }

        return text;
    }
}
