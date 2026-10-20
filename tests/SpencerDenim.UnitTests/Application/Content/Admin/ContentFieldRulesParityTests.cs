using System.Text.Json;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Content.Admin;

namespace SpencerDenim.UnitTests.Application.Content.Admin;

/// <summary>
/// #192 tester D3: the API's half of the parity between <see cref="ContentFieldRules"/> and the admin e2e
/// stub. Both answer to <c>src/frontend/e2e/support/content-rule-cases.json</c>; the stub's half is
/// <c>admin-content-stub-parity.e2e.ts</c>. Changing a rule on one side without the table fails here or there.
/// </summary>
public sealed class ContentFieldRulesParityTests
{
    private static readonly JsonElement Cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(
        RepositoryRoot(), "src", "frontend", "e2e", "support", "content-rule-cases.json"))).RootElement;

    [Fact]
    public void Site_paths_agree_with_the_shared_table()
    {
        foreach (var entry in Cases.GetProperty("sitePath").EnumerateArray())
        {
            var value = entry.GetProperty("value").GetString()!;
            Assert.True(entry.GetProperty("ok").GetBoolean() == ContentFieldRules.IsSitePath(value), $"site path {value}");
        }
    }

    [Fact]
    public void Web_addresses_agree_with_the_shared_table()
    {
        foreach (var entry in Cases.GetProperty("webAddress").EnumerateArray())
        {
            var value = entry.GetProperty("value").GetString()!;
            Assert.True(entry.GetProperty("ok").GetBoolean() == ContentFieldRules.IsWebAddress(value), $"web address {value}");
        }
    }

    [Fact]
    public void Plain_text_agrees_with_the_shared_table()
    {
        foreach (var entry in Cases.GetProperty("text").EnumerateArray())
        {
            var errors = new List<ValidationError>();
            ContentFieldRules.OptionalText(errors, "text", entry.GetProperty("value").GetString(), entry.GetProperty("max").GetInt32());

            Assert.True(entry.GetProperty("ok").GetBoolean() == (errors.Count == 0), $"text {entry.GetProperty("value").GetString()}");
        }
    }

    /// <summary>
    /// #215: the mega-menu card's link and heading, asked through the card handler itself rather than the rule it
    /// calls, so a handler that stopped calling the shared rule would fail here as the stub would fail in
    /// <c>admin-content-stub-parity.e2e.ts</c>. A card link may leave the site, so it is good when either table
    /// says so.
    /// </summary>
    [Fact]
    public async Task The_mega_menu_card_link_and_heading_agree_with_the_shared_table()
    {
        var sitePaths = Cases.GetProperty("sitePath").EnumerateArray().ToList();
        var webAddresses = Cases.GetProperty("webAddress").EnumerateArray().ToList();

        foreach (var entry in sitePaths.Concat(webAddresses))
        {
            var value = entry.GetProperty("value").GetString()!;
            var ok = sitePaths.Concat(webAddresses).Any(other => other.GetProperty("value").GetString() == value && other.GetProperty("ok").GetBoolean());

            var fields = await CardErrorsAsync(CardFields() with { LinkUrl = value });
            Assert.True(ok == !fields.Contains("linkUrl"), $"card linkUrl {value}");
        }

        foreach (var entry in Cases.GetProperty("text").EnumerateArray().Where(entry => entry.GetProperty("max").GetInt32() >= 100))
        {
            var value = entry.GetProperty("value").GetString();
            var fields = await CardErrorsAsync(CardFields() with { Title = value });
            Assert.True(entry.GetProperty("ok").GetBoolean() == !fields.Contains("title"), $"card title {value}");
        }
    }

    private static async Task<List<string>> CardErrorsAsync(SpencerDenim.Application.Content.Admin.MegaMenu.MegaMenuCardFields fields)
    {
        var handlers = new SpencerDenim.Application.Content.Admin.MegaMenu.MegaMenuCardWriteHandlers(
            new FakeMegaMenuCardAdminStore(),
            new RecordingAuditLogWriter(),
            new RecordingHomePageCache(),
            new FixedClock(new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero)));

        var result = await handlers.HandleAsync(
            new SpencerDenim.Application.Content.Admin.MegaMenu.CreateMegaMenuCardCommand(fields, TestActors.Editor),
            TestContext.Current.CancellationToken);

        return result is ContentWriteResult.Invalid invalid ? invalid.Errors.Select(error => error.Field).ToList() : [];
    }

    private static SpencerDenim.Application.Content.Admin.MegaMenu.MegaMenuCardFields CardFields() =>
        new(1, "New in Women", null, "Shop now", "/new-arrivals", null, 0, false, null, null);

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
