using Microsoft.Data.SqlClient;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// The one test in this assembly that is <b>always reported</b>, whether or not a SQL Server is
/// configured.
/// <para>
/// <b>Why it exists.</b> Every SQL-backed test calls <c>SkipIfUnavailable</c>, so on a machine with no
/// <c>TEST_SQL_CONNECTION</c> the suite answers "880 skipped" and a green-looking summary. At
/// <c>dotnet test</c>'s normal verbosity the skip reasons are not printed, so nothing on screen says a
/// database was missing — and the run that proves nothing looks exactly like the run that proves
/// everything. That is how a round of W1-03 was reported as green when the browser tests were
/// exercising a stub whose shapes had diverged from the real contracts.
/// </para>
/// <para>
/// So this test fails, by name, with the variable and an example value in the message. CI always sets it
/// (<c>.github/workflows/ci.yml</c>), so this is red only on a machine that has genuinely not been set
/// up — which is precisely when somebody needs to be told.
/// </para>
/// </summary>
public sealed class TestDatabaseRequirementTests(SqlServerDatabaseFixture sqlServer)
{
    [Fact]
    public void A_real_SQL_Server_is_configured_for_this_run()
    {
        Assert.True(
            sqlServer.IsAvailable,
            $"""
             This assembly tests against a real SQL Server and nothing in it ran.

             {SqlServerDatabaseFixture.ConnectionVariable} is not set, so every SQL-backed test skipped
             itself and the run proved nothing. Set it to a server (no database name — the fixture
             creates and drops its own {SqlServerDatabaseFixture.DatabaseNamePrefix}<guid>):

                 PowerShell: $env:{SqlServerDatabaseFixture.ConnectionVariable} = 'Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True'

             Doc 06 §4 and README "Running the tests" both say so. Never point it at an instance holding
             databases you did not create for testing.
             """);
    }

    /// <summary>
    /// Doc 06 §4: "Never point tests at the default SQL Server instance; it may hold office databases."
    /// <para>
    /// The fixture generates its own database name and ignores any <c>Initial Catalog</c> in the
    /// variable, which is what makes that rule hold however the variable is written. This asserts the
    /// property rather than trusting the comment: a run whose database is a name somebody else chose is
    /// a run that can drop somebody else's data when the fixture tears down.
    /// </para>
    /// </summary>
    [Fact]
    public void The_run_works_in_a_database_it_created_itself()
    {
        sqlServer.SkipIfUnavailable();

        Assert.StartsWith(
            SqlServerDatabaseFixture.DatabaseNamePrefix,
            sqlServer.DatabaseName,
            StringComparison.Ordinal);

        Assert.Equal(
            sqlServer.DatabaseName,
            new SqlConnectionStringBuilder(sqlServer.ConnectionString).InitialCatalog);
    }
}
