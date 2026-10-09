using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// <b>Every settings key this build claims to seed is a row on a genuinely fresh deployment, and every key
/// it records as deferred is not</b> (issue #113).
/// <para>
/// <b>It uses its own database, and that is not a convenience.</b> The shared fixture seeds, so a test
/// asserting that seeding happened against a database the fixture already seeded proves nothing about a
/// deployment — this project has shipped eleven tests that could not fail, and that is the shape of
/// several. So this creates the deployment doc 08 §10 describes (a database with §1.1's collation, the
/// migrations, then the API started with no <c>Seed__*</c> variables at all), asserts the table is empty
/// first, and then asserts what arrived.
/// </para>
/// <para>
/// The companion to this is <c>SettingKeySpecificationTests</c>, which diffs <see cref="SettingCatalog"/>
/// and <see cref="DeferredSettingKeys"/> against doc 08 §3.8's own table. Together they answer #113's
/// question: a key §3.8 requires is either a row here or has a recorded reason, and neither list can
/// drift from the document without a red build.
/// </para>
/// </summary>
public sealed class FreshDeploymentSettingKeysTests(SqlServerDatabaseFixture sqlServer)
{
    [Fact]
    public async Task A_fresh_deployment_seeds_every_catalogue_key_and_no_deferred_key()
    {
        sqlServer.SkipIfUnavailable();

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(
            sqlServer.ConnectionString,
            cancellationToken);

        // The migrations create the table and put nothing in it. Asserted, because everything below is
        // about what a startup adds, and a `HasData` seed would make that claim false silently.
        await using (var before = deployment.Services.CreateAsyncScope())
        {
            var db = before.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.Settings.CountAsync(cancellationToken));
        }

        await using var factory = new ApiFactory(deployment.ConnectionString);
        using var client = factory.CreateClient();

        await using var scope = deployment.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await dbContext.Settings.ToListAsync(cancellationToken);
        var keys = rows.Select(row => row.Key).ToList();

        var expected = SettingCatalog.All.Select(definition => definition.Key).ToList();

        var absent = expected
            .Where(key => !keys.Contains(key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            absent.Count == 0,
            $"A fresh deployment has no row for {string.Join(", ", absent)}. An absent key is not "
            + "editable at all — the admin screen can only edit what the catalogue knows about — so the "
            + "client cannot set it and nothing errors (issue #113).");

        // The other direction, less a count than a guard: anything in the table that the catalogue does
        // not list and that is not the seeder's own marker row would be a key no screen can reach.
        var unexpected = keys
            .Where(key => !expected.Contains(key, StringComparer.OrdinalIgnoreCase))
            .Where(key => !string.Equals(key, SettingKeys.Seed.Version, StringComparison.OrdinalIgnoreCase))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"A fresh deployment has rows the catalogue does not list: {string.Join(", ", unexpected)}.");

        // **And every deferred key really is absent.** A deferral that was quietly seeded anyway would
        // make `DeferredSettingKeys` a comment rather than a record.
        var deferredButSeeded = keys
            .Where(DeferredSettingKeys.Keys.Contains)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            deferredButSeeded.Count == 0,
            $"{string.Join(", ", deferredButSeeded)} are recorded as deferred and were seeded anyway.");
    }

    /// <summary>
    /// <b>Everything the client has to supply arrives empty, and only a figure one of this project's own
    /// documents states arrives filled in.</b>
    /// </summary>
    /// <remarks>
    /// This asserts the <i>seeder</i>: every row whose definition carries no seed value really is null in
    /// the database, so nothing between the catalogue and the table invents one. It is on the whole table
    /// at once rather than on the keys that happened to be remembered.
    /// <para>
    /// <b>It deliberately does not police the catalogue, and cannot.</b> The expected set is computed from
    /// <c>SettingDefinition.SeedValue</c>, so a plausible-looking default added to the catalogue would
    /// make both sides of the comparison agree — a mutation giving <c>Bank.BankName</c> a bank name passed
    /// this test unchanged. That half is
    /// <c>SettingKeySpecificationTests.A_seeded_value_exists_only_where_doc_08_states_the_default</c>,
    /// which takes the permitted values from doc 08 §3.8's own cells.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Only_the_keys_whose_default_a_document_states_are_seeded_with_a_value()
    {
        sqlServer.SkipIfUnavailable();

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var deployment = await FreshDeployment.CreateAsync(
            sqlServer.ConnectionString,
            cancellationToken);

        await using var factory = new ApiFactory(deployment.ConnectionString);
        using var client = factory.CreateClient();

        await using var scope = deployment.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var filled = await dbContext.Settings
            .Where(setting => setting.Value != null && setting.Value != string.Empty)
            .Select(setting => new { setting.Key, setting.Value })
            .OrderBy(row => row.Key)
            .ToListAsync(cancellationToken);

        var expected = SettingCatalog.All
            .Where(definition => definition.SeedValue is not null)
            .Select(definition => definition.Key)
            .Append(SettingKeys.Seed.Version)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            expected,
            filled.Select(row => row.Key).OrderBy(key => key, StringComparer.Ordinal).ToList());

        // Doc 08 §3.8's own `AutoDeliverAfterDays (7)`, which is the only figure in this build that comes
        // from a document rather than from the client.
        Assert.Equal(
            SettingKeys.Orders.AutoDeliverAfterDaysDefault.ToString(CultureInfo.InvariantCulture),
            filled.Single(row => row.Key == SettingKeys.Orders.AutoDeliverAfterDays).Value);
    }
}
