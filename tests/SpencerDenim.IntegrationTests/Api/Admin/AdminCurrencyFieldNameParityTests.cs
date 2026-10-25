using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Settings;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// Issue #248, tester round: how the REAL route reads a field's name, held to the same table as the e2e stub
/// (<c>src/frontend/e2e/support/currency-rate-cases.json</c>, <c>fieldNameCases</c>; the stub's half is
/// <c>admin-currency-stub-parity.e2e.ts</c>). The route reads the five rates' names without regard to case, so
/// <c>{"Usd":"333"}</c> saves the dollar rate, and a name it does not know is never looked at (204, nothing written).
/// The stub once treated "Usd" as unknown, so a screen could pass against it with a body the API reads differently.
/// <para>
/// Every case puts the five rows back to empty in a <c>finally</c>: the assembly's database is shared.
/// </para>
/// </summary>
public sealed class AdminCurrencyFieldNameParityTests(SqlServerDatabaseFixture sqlServer)
{
    private const string RatesUrl = "/api/admin/settings/currency-rates";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_field_name_in_the_table_is_read_as_the_table_says()
    {
        var cases = ReadCases();
        Assert.True(cases.Count >= 8, "The table lost its field-name cases.");

        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        using var client = await MediaTestHost.SignInAsync(factory, Permissions.Settings.Edit);

        try
        {
            foreach (var (name, currency) in cases)
            {
                await ClearAsync(factory);

                using var response = await client.PutAsJsonAsync(
                    new Uri(RatesUrl, UriKind.Relative),
                    new Dictionary<string, string> { [name] = "333", ["rowVersion"] = await VersionAsync(factory) },
                    JsonOptions,
                    Token);

                Assert.True(
                    response.StatusCode == HttpStatusCode.NoContent,
                    $"'{name}' answered {(int)response.StatusCode}, the table says 204.");

                foreach (var candidate in DisplayCurrencies.All)
                {
                    var expected = candidate.Field == currency ? "333" : null;

                    Assert.True(
                        expected == await ValueAsync(factory, candidate.SettingKey),
                        $"'{name}' should leave {candidate.Code} at {expected ?? "nothing"}.");
                }
            }
        }
        finally
        {
            await ClearAsync(factory);
        }
    }

    private static List<(string Name, string? Currency)> ReadCases()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "frontend", "e2e", "support", "currency-rate-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return [.. document.RootElement.GetProperty("fieldNameCases").EnumerateArray()
            .Select(entry => (
                entry.GetProperty("name").GetString()!,
                entry.GetProperty("currency").GetString()))];
    }

    private static async Task<string> VersionAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keys = SettingCatalog.KeysInSaveUnitFor(SettingKeys.CurrencyDisplay.Group);

        return SettingsGroupVersion.For(await db.Settings.AsNoTracking().Where(row => keys.Contains(row.Key)).ToListAsync(Token));
    }

    private static async Task<string?> ValueAsync(ApiFactory factory, string key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return (await db.Settings.AsNoTracking().SingleAsync(row => row.Key == key, Token)).Value;
    }

    private static async Task ClearAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keys = DisplayCurrencies.All.Select(currency => currency.SettingKey).ToArray();

        await db.Settings
            .Where(row => keys.Contains(row.Key))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(row => row.Value, (string?)null)
                    .SetProperty(row => row.UpdatedAtUtc, (DateTime?)null)
                    .SetProperty(row => row.UpdatedByUserId, (int?)null),
                Token);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SpencerDenim.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
