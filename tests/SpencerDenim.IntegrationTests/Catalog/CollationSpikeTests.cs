using Microsoft.Data.SqlClient;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Catalog;

/// <summary>
/// Spike B6 / decision D11 (doc 08 §1.1, Appendix B): how <c>Latin1_General_100_CI_AS_SC</c> handles
/// Sinhala and Tamil for equality, <c>LIKE</c>, sorting and unique indexes.
/// <para>
/// This is a report, not an assertion of a design choice: the findings are written to the test output
/// and the assertions only pin the behaviour the catalogue depends on (exact match and substring
/// search must work; two different strings must not collide in a unique index).
/// </para>
/// </summary>
public sealed class CollationSpikeTests(SqlServerDatabaseFixture sqlServer)
{
    private const string SinhalaSaree = "ඔසරිය";
    private const string SinhalaSarong = "සරම";
    private const string TamilKanchipuram = "காஞ்சிபுரம்";
    private const string TamilSilk = "பட்டு";

    private readonly ITestOutputHelper _output = TestContext.Current.TestOutputHelper!;

    [Fact]
    public async Task Sinhala_and_tamil_equality_like_sorting_and_unique_indexes_behave_predictably()
    {
        sqlServer.SkipIfUnavailable();

        await using var connection = new SqlConnection(sqlServer.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // A real table in the test database, not a temp table: a #table would inherit tempdb's
        // collation instead of the platform collation this spike is about.
        await ExecuteAsync(connection, """
            IF OBJECT_ID(N'dbo.CollationSpike', N'U') IS NOT NULL DROP TABLE dbo.CollationSpike;
            CREATE TABLE dbo.CollationSpike (
                Id int IDENTITY(1,1) PRIMARY KEY,
                Term nvarchar(100) NOT NULL
            );
            CREATE UNIQUE INDEX UX_CollationSpike_Term ON dbo.CollationSpike (Term);
            """);

        var columnCollation = await ReadAllAsync(
            connection,
            "SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.CollationSpike') AND name = 'Term';");
        Assert.Equal(SqlServerDatabaseFixture.Collation, columnCollation[0]);

        foreach (var term in new[] { SinhalaSaree, SinhalaSarong, TamilKanchipuram, TamilSilk, "Osariya" })
        {
            await ExecuteAsync(connection, "INSERT INTO dbo.CollationSpike (Term) VALUES (@term);", ("@term", term));
        }

        _output.WriteLine($"Collation under test: {SqlServerDatabaseFixture.Collation}");

        // 1. Exact equality on a Sinhala and a Tamil string.
        var sinhalaEquality = await CountAsync(connection, "SELECT COUNT(*) FROM dbo.CollationSpike WHERE Term = @term;", SinhalaSaree);
        var tamilEquality = await CountAsync(connection, "SELECT COUNT(*) FROM dbo.CollationSpike WHERE Term = @term;", TamilKanchipuram);
        _output.WriteLine($"Equality: Sinhala {sinhalaEquality} row(s), Tamil {tamilEquality} row(s).");
        Assert.Equal(1, sinhalaEquality);
        Assert.Equal(1, tamilEquality);

        // 2. Two different Sinhala strings are not treated as equal.
        var crossMatch = await CountAsync(connection, "SELECT COUNT(*) FROM dbo.CollationSpike WHERE Term = @term;", SinhalaSarong);
        _output.WriteLine($"Two distinct Sinhala strings stay distinct: {crossMatch == 1}.");
        Assert.Equal(1, crossMatch);

        // 3. LIKE '%…%' — the mechanism the catalogue search uses (doc 08 §7.6).
        var sinhalaLike = await CountAsync(
            connection,
            "SELECT COUNT(*) FROM dbo.CollationSpike WHERE Term LIKE N'%' + @term + N'%';",
            SinhalaSaree[..3]);
        var tamilLike = await CountAsync(
            connection,
            "SELECT COUNT(*) FROM dbo.CollationSpike WHERE Term LIKE N'%' + @term + N'%';",
            TamilKanchipuram[..4]);
        _output.WriteLine($"LIKE on a substring: Sinhala {sinhalaLike} row(s), Tamil {tamilLike} row(s).");
        Assert.True(sinhalaLike >= 1, "A Sinhala substring must match with LIKE.");
        Assert.True(tamilLike >= 1, "A Tamil substring must match with LIKE.");

        // 4. Case insensitivity applies to Latin text; Sinhala and Tamil have no case.
        var latinCaseInsensitive = await CountAsync(connection, "SELECT COUNT(*) FROM dbo.CollationSpike WHERE Term = @term;", "OSARIYA");
        _output.WriteLine($"Latin 'OSARIYA' matches {latinCaseInsensitive} row(s) stored as 'Osariya' (CI collation).");
        Assert.Equal(1, latinCaseInsensitive);

        // 4b. A CI collation makes a unique index case-insensitive too: 'osariya' collides with the
        //     stored 'Osariya'. This is the one behaviour to keep in mind for slugs, SKUs and codes.
        var duplicate = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
            ExecuteAsync(connection, "INSERT INTO dbo.CollationSpike (Term) VALUES (@term);", ("@term", "osariya")));
        _output.WriteLine("Unique index under CI: inserting 'osariya' next to 'Osariya' is rejected - " + duplicate.Message.Split('.')[0] + ".");
        Assert.Contains("duplicate key", duplicate.Message, StringComparison.OrdinalIgnoreCase);

        // 4c. The same is not true across scripts: two different Sinhala strings both survive.
        var sinhalaDuplicate = await CountAsync(
            connection,
            "SELECT COUNT(*) FROM dbo.CollationSpike WHERE Term IN (@term, N'සරම');",
            SinhalaSaree);
        Assert.Equal(2, sinhalaDuplicate);

        // 5. Sort order. Version-100 weights give a deterministic order; what matters is that it is
        //    stable and does not collapse distinct strings.
        var sorted = await ReadAllAsync(connection, "SELECT Term FROM dbo.CollationSpike ORDER BY Term;");
        _output.WriteLine("Sort order: " + string.Join(" | ", sorted));
        Assert.Equal(sorted.Count, sorted.Distinct(StringComparer.Ordinal).Count());

        // 6. Every distinct non-Latin string survived the unique index.
        var total = await CountAsync(connection, "SELECT COUNT(*) FROM dbo.CollationSpike;", null);
        _output.WriteLine($"Rows accepted by the unique index: {total} (5 distinct terms inserted, 1 CI duplicate rejected).");
        Assert.Equal(5, total);

        _output.WriteLine(
            "Conclusion (D11 / spike B6): under Latin1_General_100_CI_AS_SC, Sinhala and Tamil nvarchar " +
            "values compare with =, match with LIKE '%...%', sort deterministically and stay distinct in " +
            "a unique index. Sinhala and Tamil are caseless, so CI changes nothing for them. CI does " +
            "apply to Latin script, so a unique index on a slug, SKU or product code treats 'ABC' and " +
            "'abc' as the same value - which is what we want for those columns. No design change needed.");

        await ExecuteAsync(connection, "DROP TABLE dbo.CollationSpike;");
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed SQL written in this test; values are parameters.
        command.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> CountAsync(SqlConnection connection, string sql, string? term)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed SQL written in this test; the term is a parameter.
        command.CommandText = sql;
#pragma warning restore CA2100
        if (term is not null)
        {
            command.Parameters.AddWithValue("@term", term);
        }

        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<string>> ReadAllAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed SQL written in this test.
        command.CommandText = sql;
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
