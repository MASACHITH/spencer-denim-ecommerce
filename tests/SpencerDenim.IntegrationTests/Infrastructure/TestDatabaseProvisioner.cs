using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Creates a test database by running <c>tools/db/provision-database.sql</c> — the script the live,
/// staging and developer databases are created with — rather than a <c>CREATE DATABASE</c> of its own.
/// </summary>
/// <remarks>
/// <b>Why the real script and not a copy of what it does.</b> Before issue #163 the suite had three
/// copies of <c>CREATE DATABASE … COLLATE …</c> (<see cref="SqlServerDatabaseFixture"/>,
/// <see cref="FreshDeployment"/>, <c>ClientAssetWorld</c>) and the script had a fourth. They agreed on the
/// collation and on nothing else, and the database options are where they had to agree: doc 08 §1.1 says
/// the database runs with <c>READ_COMMITTED_SNAPSHOT ON</c>, which changes what every plain read sees,
/// and no copy set it. Running the script itself means a test database cannot drift from the live one
/// without the script changing too, and a change to the script is exercised by every SQL test.
/// <para>
/// The script is embedded in this assembly (see the project file), so the text run here is the text in
/// the repository at build time. It is written for <c>sqlcmd</c>: this class substitutes
/// <c>$(DbName)</c>, which is the script's only variable, and splits on <c>GO</c> lines, which is what
/// <c>sqlcmd</c> does with them. Anything else <c>sqlcmd</c>-specific added to the script later fails
/// here loudly rather than being sent to SQL Server as text.
/// </para>
/// </remarks>
internal static partial class TestDatabaseProvisioner
{
    private const string ResourceName = "provision-database.sql";

    /// <summary>The script's batches for <paramref name="databaseName"/>, ready to run on <c>master</c>.</summary>
    public static IReadOnlyList<string> Batches(string databaseName)
    {
        if (!SafeName().IsMatch(databaseName))
        {
            throw new ArgumentException(
                $"'{databaseName}' is not a database name the provisioning script accepts (letters, digits and _ only; db-provision.sh refuses anything else too).",
                nameof(databaseName));
        }

        var script = ReadScript().Replace("$(DbName)", databaseName, StringComparison.Ordinal);

        if (script.Contains("$(", StringComparison.Ordinal) || SqlcmdCommand().IsMatch(script))
        {
            throw new InvalidOperationException(
                "tools/db/provision-database.sql now uses a sqlcmd variable or command other than $(DbName); "
                + "teach TestDatabaseProvisioner about it so test databases keep being created the way the live one is.");
        }

        return [.. GoSeparator().Split(script).Where(batch => !string.IsNullOrWhiteSpace(batch))];
    }

    /// <summary>
    /// Runs the script against the server in <paramref name="serverConnectionString"/> (any database;
    /// the script names its own), creating <paramref name="databaseName"/> if it does not exist.
    /// </summary>
    public static async Task ProvisionAsync(
        string serverConnectionString,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(
            new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var batch in Batches(databaseName))
        {
            await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The repository's own provisioning script, with a validated database name.
            command.CommandText = batch;
#pragma warning restore CA2100
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string ReadScript()
    {
        using var stream = typeof(TestDatabaseProvisioner).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{ResourceName}' is missing; the test project file embeds tools/db/provision-database.sql.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();

    /// <summary>A sqlcmd command such as <c>:setvar</c> or <c>:r</c> at the start of a line.</summary>
    [GeneratedRegex(@"^\s*:[A-Za-z]+", RegexOptions.Multiline)]
    private static partial Regex SqlcmdCommand();
}
