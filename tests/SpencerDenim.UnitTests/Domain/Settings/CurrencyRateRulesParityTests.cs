using System.Globalization;
using System.Text.Json;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Domain.Settings;

/// <summary>
/// Issue #248: the API's half of the parity between <see cref="CurrencyRateRules"/> and its two copies on the
/// admin side, the screen's pre-check and the e2e stub. All three answer to
/// <c>src/frontend/e2e/support/currency-rate-cases.json</c>; the screen's half is
/// <c>currency-rate-rules.spec.ts</c>, the stub's is <c>admin-currency-stub-parity.e2e.ts</c>. Changing a rule in one
/// place without the table fails here or there, which is the whole reason the table exists: a stub or a form that is
/// looser than the API hides defects, and one that is stricter refuses rates the API would take.
/// </summary>
public sealed class CurrencyRateRulesParityTests
{
    private static readonly JsonElement Cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(
        RepositoryRoot(), "src", "frontend", "e2e", "support", "currency-rate-cases.json"))).RootElement.GetProperty("cases");

    [Fact]
    public void The_table_is_not_empty_and_covers_every_kind_of_answer()
    {
        var problems = Cases.EnumerateArray()
            .Select(entry => entry.GetProperty("problem").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["none", "notANumber", "outOfRange", "tooManyDecimals"], problems);
        Assert.True(Cases.GetArrayLength() >= 40);
    }

    [Fact]
    public void Every_case_gets_the_answer_the_table_names()
    {
        foreach (var entry in Cases.EnumerateArray())
        {
            var value = entry.GetProperty("value").GetString()!;
            var expected = Enum.Parse<CurrencyRateProblem>(entry.GetProperty("problem").GetString()!, ignoreCase: true);

            Assert.True(
                expected == CurrencyRateRules.Check(value, out _),
                $"'{value}' should be {expected} but the API's rule says {CurrencyRateRules.Check(value, out _)}");
        }
    }

    [Fact]
    public void Every_good_rate_is_stored_in_the_form_the_table_names()
    {
        var checkedAny = false;

        foreach (var entry in Cases.EnumerateArray().Where(entry => entry.GetProperty("problem").GetString() == "none"))
        {
            var value = entry.GetProperty("value").GetString()!;

            Assert.Equal(CurrencyRateProblem.None, CurrencyRateRules.Check(value, out var rate));
            Assert.Equal(entry.GetProperty("canonical").GetString(), CurrencyRateRules.Canonical(rate));
            checkedAny = true;
        }

        Assert.True(checkedAny);
    }

    /// <summary>
    /// The table is read the same way under any thread culture: the rule is invariant, and a Sinhala or Tamil
    /// machine must not change what a rate means.
    /// </summary>
    [Theory]
    [InlineData("si-LK")]
    [InlineData("ta-LK")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void The_answers_do_not_depend_on_the_thread_culture(string culture)
    {
        var before = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            foreach (var entry in Cases.EnumerateArray())
            {
                var value = entry.GetProperty("value").GetString()!;
                var expected = Enum.Parse<CurrencyRateProblem>(entry.GetProperty("problem").GetString()!, ignoreCase: true);

                Assert.True(expected == CurrencyRateRules.Check(value, out _), $"'{value}' under {culture}");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
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
