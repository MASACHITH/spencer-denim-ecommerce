using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Ops;

/// <summary>
/// W3-07's migration against a real SQL Server: <c>ops.AdminAlerts</c> exists with the columns doc 08
/// §3.8 gives it, the index it names, and the constraints it implies.
/// <para>
/// <b>The whole column set is asserted, not a sample.</b> An assertion per interesting column proves
/// those columns are right and says nothing about one that was forgotten or one that was invented — the
/// argument <c>EmailOutboxSchemaTests</c> and <c>WishlistSchemaTests</c> both make, and it matters here
/// because doc 08 §3.8 is the only description of this table anybody will read.
/// </para>
/// </summary>
public sealed class AdminAlertsSchemaTests(SqlServerDatabaseFixture sqlServer)
    : SalesSchemaTestBase(sqlServer)
{
    /// <summary>Doc 08 §3.8's column list, in full.</summary>
    private static readonly string[] ExpectedColumns =
    [
        "AcknowledgedAtUtc|datetime2(3)|NULL",
        "AcknowledgedByUserId|int|NULL",
        "AcknowledgementNote|nvarchar|NULL",
        "AlertType|varchar|NOT NULL",
        "AuditLogId|bigint|NULL",
        "CreatedAtUtc|datetime2(3)|NOT NULL",
        "DetailsJson|nvarchar|NULL",
        "Id|int|NOT NULL",
        "RelatedEntityId|varchar|NULL",
        "RelatedEntityType|varchar|NULL",
        "Severity|tinyint|NOT NULL",
        "Title|nvarchar|NOT NULL",
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "IX_AdminAlerts_AcknowledgedBy (AcknowledgedByUserId)",
        "IX_AdminAlerts_Open (CreatedAtUtc) FILTERED",
        "PK_AdminAlerts (Id) UNIQUE",
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
                WHERE s.name = 'ops' AND t.name = 'AdminAlerts'
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
                WHERE s.name = 'ops' AND t.name = 'AdminAlerts'
                """)
            .ToListAsync(Token);

        Assert.Equal(ExpectedColumns, columns.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The widths doc 08 §3.8 gives, which the column-type query above cannot show.
    /// </summary>
    /// <remarks>
    /// <b><c>max_length</c> is bytes</b>, so an <c>nvarchar(n)</c> reads as <c>2n</c> and an
    /// <c>nvarchar(max)</c> as <c>-1</c>. <c>Title</c> at 400 is the figure
    /// <c>DeadEmailAlert</c> clips to, and the two numbers have to be the same one.
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
                WHERE s.name = 'ops' AND t.name = 'AdminAlerts'
                  AND ty.name IN ('varchar', 'nvarchar')
                """)
            .ToListAsync(Token);

        Assert.Equal(
            [
                "AcknowledgementNote=1000",
                "AlertType=40",
                "DetailsJson=-1",
                "RelatedEntityId=100",
                "RelatedEntityType=40",
                "Title=400",
            ],
            widths.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// <c>IX_AdminAlerts_Open (CreatedAtUtc DESC) WHERE AcknowledgedAtUtc IS NULL</c> is doc 08 §3.8's
    /// own index, and the foreign-key index is named explicitly beside it.
    /// </summary>
    /// <remarks>
    /// <b>The filter is asserted as text, and the descending key order with it.</b> A filtered index is
    /// only used when the query's predicate provably implies the filter (DB28), and the key order is what
    /// lets the banner's <c>ORDER BY CreatedAtUtc DESC</c> come off the front of the index with no sort.
    /// <para>
    /// <b>The two indexes cannot have collapsed into one.</b> EF's index-collapsing convention is about
    /// foreign-key indexes and it ignores filters when it decides whether an index it needs already
    /// exists — so a convention index and a hand-written filtered index on the same column become one,
    /// silently. These are on different columns, and both are named explicitly anyway, which is what makes
    /// that true by inspection.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_banners_index_is_the_one_doc_08_names()
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
                WHERE i.object_id = OBJECT_ID('ops.AdminAlerts') AND i.name IS NOT NULL
                """)
            .ToListAsync(Token);

        Assert.Equal(ExpectedIndexes, indexes.Order(StringComparer.Ordinal));

        var filter = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT i.filter_definition COLLATE DATABASE_DEFAULT AS [Value]
                FROM sys.indexes i
                WHERE i.object_id = OBJECT_ID('ops.AdminAlerts') AND i.name = 'IX_AdminAlerts_Open'
                """)
            .SingleAsync(Token);

        Assert.Equal("([AcknowledgedAtUtc] IS NULL)", filter);

        var descending = await DbContext.Database
            .SqlQuery<bool>($"""
                SELECT CAST(ic.is_descending_key AS bit) AS [Value]
                FROM sys.index_columns ic
                JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                WHERE i.object_id = OBJECT_ID('ops.AdminAlerts')
                  AND i.name = 'IX_AdminAlerts_Open'
                  AND ic.is_included_column = 0
                """)
            .SingleAsync(Token);

        Assert.True(descending);
    }

    /// <summary>
    /// Doc 08 §3.8: <c>AcknowledgedByUserId</c> has a foreign key to <c>identity.Users</c>, and
    /// <c>AuditLogId</c> has none.
    /// </summary>
    /// <remarks>
    /// <b>Both halves, because each would be invisible if it were wrong.</b> A key on
    /// <c>AuditLogId</c> would eventually block the audit log's own three-year purge (doc 08 §8.4) against
    /// an alert kept for one; no key on <c>AcknowledgedByUserId</c> would let an alert name a user id that
    /// is not a user, which is what the doc 11 §6.1 escalation reads to find out who has seen what.
    /// </remarks>
    [Fact]
    public async Task Only_the_acknowledging_user_is_a_foreign_key()
    {
        SqlServer.SkipIfUnavailable();

        var keys = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT fk.name COLLATE DATABASE_DEFAULT + ' -> '
                     + OBJECT_SCHEMA_NAME(fk.referenced_object_id) COLLATE DATABASE_DEFAULT + '.'
                     + OBJECT_NAME(fk.referenced_object_id) COLLATE DATABASE_DEFAULT AS [Value]
                FROM sys.foreign_keys fk
                WHERE fk.parent_object_id = OBJECT_ID('ops.AdminAlerts')
                """)
            .ToListAsync(Token);

        Assert.Equal(["FK_AdminAlerts_AcknowledgedBy_Users -> identity.Users"], keys);
    }

    /// <summary>
    /// Doc 08 §3.8 marks a CK on <c>Severity</c>, and one on <c>DetailsJson</c> for <c>ISJSON</c>.
    /// <b>Both are proved by violating them</b>, not by reading <c>sys.check_constraints</c>: a
    /// constraint that exists and is not enforced looks the same from the catalogue views.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(255)]
    public async Task A_severity_outside_doc_08s_three_is_refused(byte severity)
    {
        SqlServer.SkipIfUnavailable();

        var violation = await Assert.ThrowsAsync<DbUpdateException>(() => InsertAsync(
            severity: severity,
            detailsJson: null));

        Assert.Contains("CK_AdminAlerts_Severity", violation.InnerException!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"unclosed\": ")]
    public async Task Details_that_are_not_json_are_refused(string detailsJson)
    {
        SqlServer.SkipIfUnavailable();

        var violation = await Assert.ThrowsAsync<DbUpdateException>(() => InsertAsync(detailsJson: detailsJson));

        Assert.Contains("CK_AdminAlerts_DetailsJson", violation.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_details_and_a_json_document_are_both_accepted()
    {
        SqlServer.SkipIfUnavailable();

        await InsertAsync(detailsJson: null);
        await InsertAsync(detailsJson: "{\"templateCode\":\"OrderPlaced\",\"outboxMessageId\":1}");
    }

    /// <summary>
    /// The acknowledgement columns move together, which is what makes
    /// "open" mean exactly "<c>AcknowledgedAtUtc IS NULL</c>".
    /// </summary>
    /// <remarks>
    /// <b>Not in doc 08, and it pins the invariant the whole read path rests on.</b>
    /// <c>IX_AdminAlerts_Open</c> is filtered on that one column, so a row with an acknowledging user and
    /// no time would be invisible to the banner and look acknowledged in a report — two readers
    /// disagreeing about one row, with nothing to say which is right.
    /// </remarks>
    [Fact]
    public async Task A_half_acknowledged_row_is_refused()
    {
        SqlServer.SkipIfUnavailable();

        var userId = await StaffUserIdAsync();

        var timeWithoutUser = await Assert.ThrowsAsync<DbUpdateException>(() => InsertAsync(
            acknowledgedAtUtc: DateTime.UtcNow));

        Assert.Contains(
            "CK_AdminAlerts_Acknowledged",
            timeWithoutUser.InnerException!.Message,
            StringComparison.Ordinal);

        var userWithoutTime = await Assert.ThrowsAsync<DbUpdateException>(() => InsertAsync(
            acknowledgedByUserId: userId));

        Assert.Contains(
            "CK_AdminAlerts_Acknowledged",
            userWithoutTime.InnerException!.Message,
            StringComparison.Ordinal);

        var noteWithoutAnything = await Assert.ThrowsAsync<DbUpdateException>(() => InsertAsync(
            acknowledgementNote: "I looked at it"));

        Assert.Contains(
            "CK_AdminAlerts_Acknowledged",
            noteWithoutAnything.InnerException!.Message,
            StringComparison.Ordinal);

        // And the acknowledged shape the application writes is accepted.
        await InsertAsync(
            acknowledgedAtUtc: DateTime.UtcNow,
            acknowledgedByUserId: userId,
            acknowledgementNote: "Rang the customer");
    }

    /// <summary>
    /// <c>CreatedAtUtc</c> defaults to <c>sysutcdatetime()</c>, so an alert raised by a path that forgot
    /// to stamp it is still ordered correctly in the banner.
    /// </summary>
    [Fact]
    public async Task Created_at_defaults_to_the_servers_clock()
    {
        SqlServer.SkipIfUnavailable();

        // `ToListAsync().Single()` rather than `SingleAsync()`: an INSERT with an OUTPUT clause is
        // non-composable SQL, and EF refuses to compose `SingleAsync` over it (`EmailOutboxSendingTests`
        // writes its inserts the same way).
        var before = (await DbContext.Database
                .SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]")
                .ToListAsync(Token))
            .Single();

        var id = (await DbContext.Database
                .SqlQuery<int>($"""
                    INSERT INTO ops.AdminAlerts (AlertType, Severity, Title)
                    OUTPUT inserted.Id AS [Value]
                    VALUES ('EmailDeliveryFailed', 2, 'A schema probe')
                    """)
                .ToListAsync(Token))
            .Single();

        var stored = await DbContext.AdminAlerts.AsNoTracking().SingleAsync(alert => alert.Id == id, Token);

        Assert.True(stored.CreatedAtUtc >= before.AddSeconds(-5));
    }

    /// <summary>
    /// The table is <b>never audited</b> by the interceptor: it has no audit column pair at all.
    /// </summary>
    /// <remarks>
    /// Doc 08 §3.8 gives this table no <c>CreatedByUserId</c> / <c>UpdatedByUserId</c>, and the reason is
    /// that every writer is a job with no acting user. What <i>is</i> audited is the acknowledgement,
    /// through an <c>ops.AuditLogs</c> row — see <c>AdminAlertsAcknowledgeTests</c>. Asserted against the
    /// database rather than against the entity, because an audit column added by convention would be
    /// invisible in the C#.
    /// </remarks>
    [Fact]
    public async Task The_table_carries_no_audit_column_pair()
    {
        SqlServer.SkipIfUnavailable();

        var auditColumns = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT c.name COLLATE DATABASE_DEFAULT AS [Value]
                FROM sys.columns c
                WHERE c.object_id = OBJECT_ID('ops.AdminAlerts')
                  AND c.name IN ('CreatedByUserId', 'UpdatedAtUtc', 'UpdatedByUserId', 'IsDeleted', 'RowVersion')
                """)
            .ToListAsync(Token);

        Assert.Empty(auditColumns);
    }

    /// <summary>
    /// A staff user to acknowledge with, created by this test if it does not exist.
    /// </summary>
    /// <remarks>
    /// <b>The SQL fixture seeds the catalogue and not identity</b> — <c>AdminSeedHostedService</c> runs
    /// in the API host, which this suite does not start — so <c>identity.Users</c> is empty here, and a
    /// test that read the first user id got "sequence contains no elements". Inserted with SQL rather
    /// than through <c>UserManager</c>, which this fixture does not register; only the non-nullable
    /// columns are set and the rest take their defaults.
    /// </remarks>
    private async Task<int> StaffUserIdAsync() => await AdminAlertsBehaviourTests
        .StaffUserIdAsync(DbContext, Token);

    /// <summary>One row, inserted through EF so a check constraint surfaces as a <c>DbUpdateException</c>.</summary>
    private async Task InsertAsync(
        byte severity = (byte)AdminAlertSeverity.Warning,
        string? detailsJson = "{\"probe\":true}",
        DateTime? acknowledgedAtUtc = null,
        int? acknowledgedByUserId = null,
        string? acknowledgementNote = null)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        dbContext.AdminAlerts.Add(new AdminAlert
        {
            AlertType = AdminAlertTypes.EmailDeliveryFailed,
            Severity = (AdminAlertSeverity)severity,
            Title = "A schema probe",
            DetailsJson = detailsJson,
            RelatedEntityType = AdminAlert.EmailOutboxEntityType,
            RelatedEntityId = "1",
            CreatedAtUtc = DateTime.UtcNow,
            AcknowledgedAtUtc = acknowledgedAtUtc,
            AcknowledgedByUserId = acknowledgedByUserId,
            AcknowledgementNote = acknowledgementNote,
        });

        await dbContext.SaveChangesAsync(Token);
    }
}
