using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Domain.Settings;

/// <summary>
/// Issue #248: the API's half of the parity between <see cref="CurrencyRateRules"/> and its two copies on the
/// admin side, the screen's pre-check and the e2e stub. All three answer to
/// <c>src/frontend/e2e/support/currency-rate-cases.json</c>; the screen's half is
/// <c>currency-rate-rules.spec.ts</c>, the stub's is <c>admin-currency-stub-parity.e2e.ts</c>. Changing a rule in one
/// place without the table fails here or there, which is the whole reason the table exists: a stub or a form that is
/// looser than the API hides defects, and one that is stricter refuses rates the API would take.
/// <para>
/// <b>The table also holds the five currencies' names and the exact sentence for each kind of fault</b>, and the
/// REAL <see cref="UpdateCurrencyRatesCommandHandler"/> is asked for every one of them. The screen shows its own
/// sentence before the request is sent and the server's after it, and the two must never read differently: the
/// handler once lower-cased "US dollar" into "us dollar" in the middle of a sentence, and the screen's copy of that
/// wording went stale the day the server's was corrected.
/// </para>
/// </summary>
public sealed class CurrencyRateRulesParityTests
{
    private static readonly JsonElement Root = JsonDocument.Parse(File.ReadAllText(Path.Combine(
        RepositoryRoot(), "src", "frontend", "e2e", "support", "currency-rate-cases.json"))).RootElement;

    private static readonly JsonElement Cases = Root.GetProperty("cases");

    private static readonly AdminActor Actor = new(7, "staff@example.com", null, null, null);

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

    [Fact]
    public void The_five_currencies_are_the_tables_five_with_the_same_codes_fields_and_names_in_the_same_order()
    {
        var expected = Root.GetProperty("currencies").EnumerateArray()
            .Select(entry => (
                Field: entry.GetProperty("field").GetString()!,
                Code: entry.GetProperty("code").GetString()!,
                Name: entry.GetProperty("name").GetString()!))
            .ToList();

        var actual = DisplayCurrencies.All.Select(currency => (currency.Field, currency.Code, currency.Name)).ToList();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Every currency, every kind of fault, the real handler. A currency is asked about one field at a time so each
    /// sentence is the only error, and the store is one that throws if it is touched: validation happens first, and
    /// this is also the proof of it.
    /// </summary>
    [Fact]
    public async Task The_real_handler_gives_the_tables_exact_sentence_for_every_currency_and_every_kind_of_fault()
    {
        var templates = Root.GetProperty("messages");
        var faults = new[]
        {
            (Kind: "notANumber", Value: "1e3"),
            (Kind: "tooManyDecimals", Value: "1.00001"),
            (Kind: "outOfRange", Value: "0"),
        };

        var asked = 0;

        foreach (var currency in DisplayCurrencies.All)
        {
            foreach (var (kind, value) in faults)
            {
                var result = await Handler().HandleAsync(
                    new UpdateCurrencyRatesCommand(
                        new Dictionary<string, string?>(StringComparer.Ordinal) { [currency.Field] = value },
                        null,
                        Actor),
                    TestContext.Current.CancellationToken);

                var invalid = Assert.IsType<SettingsWriteResult.Invalid>(result);
                var error = Assert.Single(invalid.Errors);
                var expected = templates.GetProperty(kind).GetString()!
                    .Replace("{name}", currency.Name, StringComparison.Ordinal)
                    .Replace("{code}", currency.Code, StringComparison.Ordinal);

                Assert.Equal(currency.Field, error.Field);
                Assert.Equal(expected, error.Message);
                asked++;
            }
        }

        Assert.Equal(15, asked);
    }

    /// <summary>
    /// A name that begins with a capital in the middle of a sentence (US dollar, UAE dirham, Australian dollar) is a
    /// proper noun and must keep it; the handler once lower-cased every name at the point of use.
    /// </summary>
    [Fact]
    public void A_currency_named_after_a_place_keeps_its_capital_in_the_tables_names()
    {
        var names = Root.GetProperty("currencies").EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!).ToList();

        Assert.Contains("US dollar", names);
        Assert.Contains("UAE dirham", names);
        Assert.Contains("Australian dollar", names);
        Assert.Contains("pound sterling", names);
        Assert.Contains("euro", names);
    }

    private static UpdateCurrencyRatesCommandHandler Handler() =>
        new(new StoreThatMustNotBeTouched(), new SilentAuditLog(), new AllPermissions(), new FixedClock(), new NoCache());

    private sealed class StoreThatMustNotBeTouched : ISettingsStore
    {
        public Task<IReadOnlyList<Setting>> ListAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Validation must finish before the store is read.");

        public Task<SettingImageAsset?> FindUsableImageAsync(Guid publicId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by this route.");

        public Task<SettingImageAsset?> FindUsableImageByIdAsync(int assetId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by this route.");

        public Task ReplaceMediaUsageAsync(string settingKey, string usageRole, int? newAssetId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by this route.");

        public Task<SettingsSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Nothing may be saved when a field is refused.");
    }

    private sealed class SilentAuditLog : IAuditLogWriter
    {
        public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Enlist(AuditLog entry)
        {
        }
    }

    private sealed class AllPermissions : IEffectivePermissionsProvider
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken) =>
            Task.FromResult(new EffectivePermissions(true, []));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class NoCache : ICurrencyRatesCache
    {
        public IReadOnlyList<CurrencyRateDto>? GetRates() => null;

        public void SetRates(IReadOnlyList<CurrencyRateDto> rates)
        {
        }

        public void Evict()
        {
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
