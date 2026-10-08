using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// <c>ops.Settings</c> as the migration actually creates it, and what the seeder does to it (ADM-22,
/// doc 08 §3.8).
/// <para>
/// <b>Every constraint is proved by trying to break it</b>, including the two nothing uses yet. The
/// <see cref="SettingValueType.Secret"/> and <see cref="SettingValueType.Decimal"/> machinery exists for
/// issue #53 and W3, and the point of building it now is that it is checked now — a CHECK constraint that
/// has never refused anything is a comment.
/// </para>
/// </summary>
public sealed class SettingsSchemaAndSeedTests(SqlServerDatabaseFixture sqlServer)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the columns ------------------------------------------------------------------------------

    /// <summary>
    /// The ten columns of doc 08 §3.8, with the types the document gives them. Read out of
    /// <c>INFORMATION_SCHEMA</c> rather than from the EF model, because the model is what produced the
    /// migration and asserting it against itself proves nothing.
    /// </summary>
    [Fact]
    public async Task The_table_has_the_columns_and_types_doc_08_specifies()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        var columns = await QueryAsync(
            """
            SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'ops' AND TABLE_NAME = 'Settings'
            """,
            reader => (
                Name: reader.GetString(0),
                Type: reader.GetString(1),
                Length: reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
                Nullable: reader.GetString(3) == "YES"));

        var byName = columns.ToDictionary(column => column.Name, StringComparer.Ordinal);

        Assert.Equal(
            [
                "Description", "GroupKey", "IsSecret", "Key", "RequiredPermission", "RowVersion",
                "UpdatedAtUtc", "UpdatedByUserId", "Value", "ValueType",
            ],
            byName.Keys.Order(StringComparer.Ordinal));

        Assert.Equal(("varchar", (int?)100, false), (byName["Key"].Type, byName["Key"].Length, byName["Key"].Nullable));
        Assert.Equal(("varchar", (int?)50, false), (byName["GroupKey"].Type, byName["GroupKey"].Length, byName["GroupKey"].Nullable));
        Assert.Equal(("tinyint", false), (byName["ValueType"].Type, byName["ValueType"].Nullable));

        // nvarchar(max) is reported as -1. Doc 08 §3.8: one column holds every value type, including a Json
        // blob, a sanitised HTML snippet and a Data Protection ciphertext.
        Assert.Equal(("nvarchar", (int?)(-1), true), (byName["Value"].Type, byName["Value"].Length, byName["Value"].Nullable));

        Assert.Equal(("bit", false), (byName["IsSecret"].Type, byName["IsSecret"].Nullable));
        Assert.Equal(("varchar", (int?)64, false), (byName["RequiredPermission"].Type, byName["RequiredPermission"].Length, byName["RequiredPermission"].Nullable));
        Assert.Equal(("nvarchar", (int?)300, true), (byName["Description"].Type, byName["Description"].Length, byName["Description"].Nullable));
        Assert.Equal(("datetime2", true), (byName["UpdatedAtUtc"].Type, byName["UpdatedAtUtc"].Nullable));
        Assert.Equal(("int", true), (byName["UpdatedByUserId"].Type, byName["UpdatedByUserId"].Nullable));

        // [RV], which doc 08 marks on this table and not on sales.Carts.
        Assert.Equal("timestamp", byName["RowVersion"].Type);
    }

    [Fact]
    public async Task The_dotted_key_is_the_primary_key()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        var keyColumns = await QueryAsync(
            """
            SELECT kcu.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
              ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
            WHERE tc.TABLE_SCHEMA = 'ops' AND tc.TABLE_NAME = 'Settings'
              AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            """,
            reader => reader.GetString(0));

        Assert.Equal(["Key"], keyColumns);
    }

    [Fact]
    public async Task The_group_key_is_indexed()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        var indexes = await QueryAsync(
            """
            SELECT name FROM sys.indexes
            WHERE object_id = OBJECT_ID('ops.Settings') AND name IS NOT NULL
            """,
            reader => reader.GetString(0));

        Assert.Contains("IX_Settings_GroupKey", indexes);
    }

    // ---- the CHECK constraints --------------------------------------------------------------------

    /// <summary>
    /// <c>CK_Settings_ValueType</c>: doc 08 §3.8's eight values and nothing else.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(255)]
    public async Task A_value_type_outside_doc_08s_eight_is_refused(byte valueType)
    {
        await AssertRefusedAsync(
            $"""
            INSERT INTO ops.Settings (
                [Key], GroupKey, ValueType, Value, IsSecret, RequiredPermission, Description)
            VALUES (N'Test.Bad{valueType}', 'Test', {valueType}, NULL, 0, 'Settings.Edit', NULL);
            """,
            "CK_Settings_ValueType");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public async Task Every_non_secret_value_type_doc_08_lists_is_accepted(byte valueType)
    {
        // Including 4 (Decimal), which nothing writes until PayPal's FX buffer arrives, and 2 and 6, which
        // need the HTML sanitiser and a Json consumer. The point of the constraint allowing them now is that
        // issue #53 adds rows rather than a migration.
        await InsertAndRemoveAsync(
            $"""
            INSERT INTO ops.Settings (
                [Key], GroupKey, ValueType, Value, IsSecret, RequiredPermission, Description)
            VALUES (N'Test.Type{valueType}', 'Test', {valueType}, NULL, 0, 'Settings.Edit', NULL);
            """,
            $"Test.Type{valueType}");
    }

    /// <summary>
    /// <c>CK_Settings_IsSecret</c>, both directions. A <c>Secret</c> row with <c>IsSecret = 0</c> would have
    /// its value logged in full by the audit trail; a <c>String</c> row with <c>IsSecret = 1</c> would hide a
    /// value that is not encrypted and cannot be decrypted. Neither is recoverable from a screen.
    /// </summary>
    [Fact]
    public async Task A_secret_row_must_say_it_is_secret()
    {
        await AssertRefusedAsync(
            """
            INSERT INTO ops.Settings (
                [Key], GroupKey, ValueType, Value, IsSecret, RequiredPermission, Description)
            VALUES (N'Test.SecretUnflagged', 'Test', 7, NULL, 0, 'Settings.Security', NULL);
            """,
            "CK_Settings_IsSecret");
    }

    [Fact]
    public async Task A_non_secret_row_must_not_say_it_is_secret()
    {
        await AssertRefusedAsync(
            """
            INSERT INTO ops.Settings (
                [Key], GroupKey, ValueType, Value, IsSecret, RequiredPermission, Description)
            VALUES (N'Test.StringFlagged', 'Test', 1, NULL, 1, 'Settings.Edit', NULL);
            """,
            "CK_Settings_IsSecret");
    }

    [Fact]
    public async Task A_correctly_flagged_secret_row_is_accepted()
    {
        await InsertAndRemoveAsync(
            """
            INSERT INTO ops.Settings (
                [Key], GroupKey, ValueType, Value, IsSecret, RequiredPermission, Description)
            VALUES (N'Test.Secret', 'Test', 7, NULL, 1, 'Settings.Security', NULL);
            """,
            "Test.Secret");
    }

    /// <summary>
    /// <c>CK_Settings_RequiredPermission</c>, which is not in doc 08 and belongs here: that column decides who
    /// may write the key, so a blank one is a row with no guard at all.
    /// </summary>
    [Fact]
    public async Task A_row_with_no_required_permission_is_refused()
    {
        await AssertRefusedAsync(
            """
            INSERT INTO ops.Settings (
                [Key], GroupKey, ValueType, Value, IsSecret, RequiredPermission, Description)
            VALUES (N'Test.Ungoverned', 'Test', 1, NULL, 0, '', NULL);
            """,
            "CK_Settings_RequiredPermission");
    }

    /// <summary>
    /// A <c>rowversion</c> moves itself on every update, which is what makes the concurrency check work at
    /// all. Proved against the database rather than inferred from the mapping.
    /// </summary>
    [Fact]
    public async Task The_row_version_moves_when_a_value_changes()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await dbContext.Settings.SingleAsync(setting => setting.Key == SettingKeys.Social.TikTokUrl, Token);
        var before = row.RowVersion!.ToArray();

        row.Value = "https://www.tiktok.com/@rowversion-probe";
        await dbContext.SaveChangesAsync(Token);

        try
        {
            Assert.NotEqual(before, row.RowVersion);
        }
        finally
        {
            row.Value = null;
            await dbContext.SaveChangesAsync(Token);
        }
    }

    // ---- the seeder -------------------------------------------------------------------------------

    /// <summary>
    /// The seeder creates every catalogued key, with the metadata doc 08 §3.8 gives it.
    /// </summary>
    /// <remarks>
    /// <b>Metadata only, and the value assertion is a separate, self-contained test — because this one was
    /// flaky and the flakiness was mine.</b> It used to assert that every row's <i>value</i> equalled its
    /// catalogued seed value, which is a global property of a shared, deliberately-mutated table. It failed
    /// about one run in three and passed twenty times alone.
    /// <para>
    /// <b>And its failure message was right all along.</b> "A test that left one behind is a neighbour's
    /// fixture" described the actual mechanism: <c>AdminSettingsEndpointTests.RestoreAsync</c> was setting
    /// every catalogued key to NULL, including the one key the seeder gives a value, and the NULL then
    /// persisted for the whole run. Classes in this assembly do <b>not</b> interleave — four timed probe
    /// classes, zero overlap — so the non-determinism was xUnit varying class <b>order</b>: this test fails
    /// if and only if it is ordered after that class. A W3-04 round-2 version of this remark called the
    /// message misleading and drew the lesson that a message should describe the weakest explanation. The
    /// lesson is a good one; this was the wrong example for it, and the original author had it right.
    /// </para>
    /// <para>
    /// <b>The leak also hid behind the assertion's own wording.</b> It reported the offending value with
    /// <c>holds ""</c>, and <c>$"{null}"</c> interpolates as the empty string — so a NULL row and an empty
    /// row read identically, and a leak that wrote NULL presented as the theory's <c>("")</c> case. An
    /// assertion message that cannot tell its two failure causes apart costs the next reader the diagnosis.
    /// </para>
    /// <para>
    /// <b>The split stands, for a plainer reason than the one first given.</b> The metadata half is global
    /// and safe <i>because</i> classes are serialised and only this class touches a settings row's
    /// metadata. The value half is a Theory inside a rolled-back transaction because a value is what the
    /// rest of the assembly legitimately changes, and a test should not depend on the restore discipline of
    /// six other classes even when that discipline is currently sound.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_seeder_creates_every_catalogued_key_with_the_metadata_doc_08_specifies()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await dbContext.Settings.AsNoTracking().ToDictionaryAsync(row => row.Key, Token);

        foreach (var definition in SettingCatalog.All)
        {
            var row = Assert.Contains(definition.Key, rows);

            Assert.Equal(definition.GroupKey, row.GroupKey);
            Assert.Equal(definition.ValueType, row.ValueType);
            Assert.Equal(definition.IsSecret, row.IsSecret);
            Assert.Equal(definition.RequiredPermission, row.RequiredPermission);
            Assert.Equal(definition.Description, row.Description);
        }
    }

    /// <summary>
    /// <b>The seeder writes a missing key's catalogued seed value and invents nothing else.</b>
    /// </summary>
    /// <remarks>
    /// <b>It owns the rows it touches, which is this class's own idiom and the fix for the race above.</b>
    /// Each key is deleted and re-seeded <i>inside a transaction that is always rolled back</i>, so no
    /// other connection ever observes the row missing or changed — a plain delete-and-restore would trade a
    /// wide race for a narrow one, and <c>AutoDeliverShippedTests.The_window_is_read_from_the_settings_row</c>
    /// is a real reader of one of these keys. A concurrent reader blocks for the few milliseconds the
    /// exclusive lock is held, which is correct behaviour rather than a wrong answer.
    /// <para>
    /// <b>What this test proves, exactly: the seeder <i>honours</i> the catalogue.</b> Both halves are
    /// driven — a key doc 08 §3.8 gives a default (<c>Orders.AutoDeliverAfterDays (7)</c>) comes back with
    /// it, and a key that holds a client fact comes back <see langword="null"/> — so a seeder that wrote
    /// nothing, or that wrote the wrong thing, is caught. Verified by mutation: making the seeder ignore
    /// <c>SeedValue</c> turns the <c>AutoDeliverAfterDays</c> case red.
    /// </para>
    /// <para>
    /// <b>What it does <i>not</i> prove, and said so because the first version of this remark implied
    /// otherwise: that the catalogue is right.</b> Giving <c>Store.LegalName</c> a seed value makes this
    /// test pass — it would faithfully assert the invented address — and the guard for that is the unit
    /// test <c>AdminOrderWorkflowRulesTests.Nothing_that_holds_a_client_fact_is_seeded_with_a_value</c>,
    /// which pins the whole set of keys allowed to carry one. Also verified by mutation. The two together
    /// are the complete rule; either alone has a hole, and the hole in this one is the dangerous
    /// direction.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Orders.AutoDeliverAfterDays")]
    [InlineData("Store.LegalName")]
    [InlineData("Store.Address")]
    [InlineData("Bank.AccountNumber")]
    [InlineData("Social.FacebookUrl")]
    public async Task The_seeder_writes_a_missing_keys_catalogued_seed_value(string key)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        var definition = SettingCatalog.Find(key);
        Assert.NotNull(definition);

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeder = scope.ServiceProvider.GetRequiredService<SettingsSeeder>();

        // `CreateExecutionStrategy`, because `EnableRetryOnFailure` refuses a hand-rolled transaction
        // outright — the same hoop every store in this solution jumps through.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(Token);

            var deleted = await dbContext.Settings
                .Where(setting => setting.Key == key)
                .ExecuteDeleteAsync(Token);

            Assert.Equal(1, deleted);

            var counts = await seeder.SeedAsync(Token);

            Assert.Equal(1, counts.Inserted);

            dbContext.ChangeTracker.Clear();
            var row = await dbContext.Settings.AsNoTracking()
                .SingleAsync(setting => setting.Key == key, Token);

            Assert.True(
                string.Equals(row.Value, definition.SeedValue, StringComparison.Ordinal),
                $"the seeder wrote \"{row.Value}\" into {key} and its catalogued seed value is "
                + $"\"{definition.SeedValue}\". A settings value belongs to the client, so only a figure "
                + "one of this project's own documents states may be seeded — doc 08 §3.8's "
                + "AutoDeliverAfterDays (7) is the only one today.");

            // Rolled back whatever happened, so this test leaves the shared table exactly as it found it
            // and cannot be the neighbour it used to accuse others of being.
            await transaction.RollbackAsync(Token);
        });
    }

    /// <summary>
    /// Idempotent, and asymmetric: metadata is realigned, the client's value is never touched. Running the
    /// seeder again after the client has filled the screen in has to be safe — the alternative is a
    /// deployment that silently clears their social links, which is W1-12's blocker wearing a different hat.
    /// </summary>
    [Fact]
    public async Task Running_the_seeder_again_leaves_the_clients_values_alone()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await dbContext.Settings.SingleAsync(setting => setting.Key == SettingKeys.Social.FacebookUrl, Token);
        row.Value = "https://www.facebook.com/the-clients-own-page";
        await dbContext.SaveChangesAsync(Token);

        try
        {
            var seeder = scope.ServiceProvider.GetRequiredService<SettingsSeeder>();
            var counts = await seeder.SeedAsync(Token);

            Assert.Equal(0, counts.Inserted);
            Assert.Equal(0, counts.Realigned);

            dbContext.ChangeTracker.Clear();
            var after = await dbContext.Settings.AsNoTracking()
                .SingleAsync(setting => setting.Key == SettingKeys.Social.FacebookUrl, Token);

            Assert.Equal("https://www.facebook.com/the-clients-own-page", after.Value);
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
            await dbContext.Settings
                .Where(setting => setting.Key == SettingKeys.Social.FacebookUrl)
                .ExecuteUpdateAsync(update => update.SetProperty(setting => setting.Value, (string?)null), Token);
        }
    }

    /// <summary>
    /// The repair path, which is the reason the metadata <i>is</i> realigned: a wrong
    /// <c>RequiredPermission</c> would leave a key readable or writable by the wrong people, and no screen
    /// exposes that column, so running the seeder is the only fix short of SQL.
    /// </summary>
    [Fact]
    public async Task The_seeder_repairs_a_wrong_required_permission_without_touching_the_value()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.Settings
            .Where(setting => setting.Key == SettingKeys.Social.TikTokUrl)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(setting => setting.RequiredPermission, Permissions.Settings.Payments)
                    .SetProperty(setting => setting.Value, "https://www.tiktok.com/@kept"),
                Token);

        try
        {
            var counts = await scope.ServiceProvider.GetRequiredService<SettingsSeeder>().SeedAsync(Token);

            Assert.Equal(1, counts.Realigned);

            dbContext.ChangeTracker.Clear();
            var row = await dbContext.Settings.AsNoTracking()
                .SingleAsync(setting => setting.Key == SettingKeys.Social.TikTokUrl, Token);

            Assert.Equal(Permissions.Settings.Edit, row.RequiredPermission);

            // The client's value survived the repair.
            Assert.Equal("https://www.tiktok.com/@kept", row.Value);
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
            await dbContext.Settings
                .Where(setting => setting.Key == SettingKeys.Social.TikTokUrl)
                .ExecuteUpdateAsync(update => update.SetProperty(setting => setting.Value, (string?)null), Token);
        }
    }

    /// <summary>
    /// A key the catalogue no longer lists keeps its row. A seeder that deleted it would be data loss
    /// triggered by a code change; cleaning one up is a migration's job, where it is deliberate.
    /// </summary>
    [Fact]
    public async Task The_seeder_leaves_a_row_for_a_key_it_does_not_know()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        dbContext.Settings.Add(new Setting
        {
            Key = "Withdrawn.Key",
            GroupKey = "Withdrawn",
            ValueType = SettingValueType.String,
            Value = "something a person typed",
            RequiredPermission = Permissions.Settings.Edit,
        });
        await dbContext.SaveChangesAsync(Token);

        try
        {
            await scope.ServiceProvider.GetRequiredService<SettingsSeeder>().SeedAsync(Token);

            dbContext.ChangeTracker.Clear();
            Assert.Equal(
                "something a person typed",
                await dbContext.Settings.AsNoTracking()
                    .Where(setting => setting.Key == "Withdrawn.Key")
                    .Select(setting => setting.Value)
                    .SingleAsync(Token));
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
            await dbContext.Settings.Where(setting => setting.Key == "Withdrawn.Key").ExecuteDeleteAsync(Token);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private async Task<List<T>> QueryAsync<T>(string sql, Func<SqlDataReader, T> read)
    {
        await using var connection = new SqlConnection(sqlServer.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Token);

        var rows = new List<T>();
        while (await reader.ReadAsync(Token))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private async Task AssertRefusedAsync(string sql, string constraint)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        await using var connection = new SqlConnection(sqlServer.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = new SqlCommand(sql, connection);
        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(Token));

        // The constraint's name, so the test cannot pass because some *other* constraint refused the row.
        Assert.Contains(constraint, exception.Message, StringComparison.Ordinal);
    }

    private async Task InsertAndRemoveAsync(string sql, string key)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        await using var connection = new SqlConnection(sqlServer.ConnectionString);
        await connection.OpenAsync(Token);

        try
        {
            await using var insert = new SqlCommand(sql, connection);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(Token));
        }
        finally
        {
            await using var delete = new SqlCommand("DELETE FROM ops.Settings WHERE [Key] = @key", connection);
            delete.Parameters.Add("@key", SqlDbType.VarChar, 100).Value = key;
            await delete.ExecuteNonQueryAsync(Token);
        }
    }
}
