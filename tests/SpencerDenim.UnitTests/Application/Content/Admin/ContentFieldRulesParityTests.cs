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
