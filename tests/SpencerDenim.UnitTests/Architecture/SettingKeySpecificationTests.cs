using System.Globalization;
using System.Text.RegularExpressions;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// <b>Every settings key doc 08 §3.8 specifies is either seeded or has a recorded reason not to be</b>
/// (issue #113).
/// <para>
/// <b>The document is the input, not a copy of it.</b> The expected key list is parsed out of
/// <c>docs/08-database-design.md</c>'s §3.8 "Groups" table at test time, so this is a diff against the
/// specification rather than against a list someone retyped here. A key added to doc 08 fails the build
/// until somebody decides whether to seed it; a key left in <see cref="DeferredSettingKeys"/> after its
/// feature ships fails it too; and a key invented in code that the document does not list fails it as
/// well.
/// </para>
/// <para>
/// <b>Why it is a diff and not a count.</b> §9's table says <c>ops.Settings ~50</c> and the groups of
/// §3.8 name 59; "5 of ~50" was the shape of the original report and the count was never the finding.
/// What matters is which keys a client cannot edit, so the failure messages name them.
/// </para>
/// </summary>
public sealed class SettingKeySpecificationTests
{
    /// <summary>
    /// Keys the parser must find for the parse itself to be trusted — one from the first group, one from
    /// the last, one from the group whose cell carries a trailing sentence, and one dotted-group key.
    /// <para>
    /// <b>Without these the test could pass against a parser that matched nothing.</b> A specification
    /// test whose expected set is silently empty asserts that the catalogue is a subset of the empty set
    /// in one direction and nothing at all in the other, which is this project's recurring
    /// cannot-fail shape (a plan test on an empty table, a search for <c>@p0</c> against a null column).
    /// </para>
    /// </summary>
    private static readonly string[] ParseSentinels =
    [
        "Store.Name",
        "Store.DefaultOgImageAssetId",
        "Bank.AccountNumber",
        "Payments.PayPal.ClientSecret",
        "Email.Smtp.UseStartTls",
        "Security.CustomerLockoutMinutes",
    ];

    [Fact]
    public void The_parse_of_doc_08_section_3_8_finds_the_keys_it_is_supposed_to()
    {
        var specified = SpecifiedKeys();

        foreach (var sentinel in ParseSentinels)
        {
            Assert.Contains(sentinel, specified);
        }

        // **Fifteen groups, counted from the table, not sixteen.** SettingKeys and SettingCatalog both
        // used to say "doc 08 lists sixteen groups"; §3.8's table has fifteen rows (Store, Contact,
        // Social, Content, Orders, Inventory, Catalog, Reviews, Delivery, Privacy, Traffic, Bank,
        // Payments.PayPal, Email.Smtp, Security). Sixteen is §3.1's module count, which is a different
        // list. Both comments were corrected with this test.
        var groups = specified
            .Select(key => key[..key.LastIndexOf('.')])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(15, groups.Count);
    }

    [Fact]
    public void Every_specified_key_is_either_seeded_or_recorded_as_deferred()
    {
        var specified = SpecifiedKeys();
        var seeded = SettingCatalog.All.Select(definition => definition.Key).ToList();

        var undecided = specified
            .Where(key => !seeded.Contains(key, StringComparer.OrdinalIgnoreCase))
            .Where(key => !DeferredSettingKeys.Keys.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            undecided.Count == 0,
            $"doc 08 §3.8 specifies {string.Join(", ", undecided)}, and this build neither seeds them nor "
            + "records a reason not to. An absent key is not editable at all — the admin screen can only "
            + "edit what SettingCatalog knows about — so the client cannot set it and nothing says why. "
            + "Either add it to SettingCatalog (with an empty seed value unless one of this project's "
            + "documents states the figure) or add it to DeferredSettingKeys with what reads it today.");
    }

    [Fact]
    public void No_key_is_both_seeded_and_recorded_as_deferred()
    {
        var both = SettingCatalog.All
            .Select(definition => definition.Key)
            .Where(DeferredSettingKeys.Keys.Contains)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            both.Count == 0,
            $"{string.Join(", ", both)} are seeded and also listed as deferred. The deferral has been "
            + "overtaken by the key being built; delete the DeferredSettingKeys entry.");
    }

    [Fact]
    public void Nothing_seeded_or_deferred_is_absent_from_doc_08()
    {
        var specified = SpecifiedKeys();

        var seededButUnspecified = SettingCatalog.All
            .Select(definition => definition.Key)
            .Where(key => !specified.Contains(key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            seededButUnspecified.Count == 0,
            $"The catalogue seeds {string.Join(", ", seededButUnspecified)}, which doc 08 §3.8 does not "
            + "list. A settings key is part of the database design; add it to the document or drop it.");

        var deferredButUnspecified = DeferredSettingKeys.All
            .Select(deferred => deferred.Key)
            .Where(key => !specified.Contains(key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            deferredButUnspecified.Count == 0,
            $"{string.Join(", ", deferredButUnspecified)} are recorded as deferred and doc 08 §3.8 does "
            + "not list them, so there is nothing to defer.");
    }

    /// <summary>
    /// Every deferral says something a reader can disagree with, and no key is listed twice.
    /// </summary>
    [Fact]
    public void Every_deferral_carries_a_reason_and_appears_once()
    {
        Assert.Equal(DeferredSettingKeys.All.Count, DeferredSettingKeys.Keys.Count);

        Assert.All(
            DeferredSettingKeys.All,
            deferred =>
            {
                Assert.False(string.IsNullOrWhiteSpace(deferred.Reason));

                // Long enough to name what reads the value and what has to exist first. "Not built yet"
                // is the answer this list was created to stop being the whole answer.
                Assert.True(
                    deferred.Reason.Length >= 60,
                    $"The reason for {deferred.Key} is too short to say what reads it today.");
            });
    }

    /// <summary>
    /// <c>Seed.Version</c> is a settings row and deliberately <b>not</b> a catalogue key: the catalogue is
    /// what the admin API reads and writes, so a key in it is a box on a screen. This one is the seeder's
    /// bookkeeping (doc 08 §9) and an operator editing it would silently re-offer or suppress a permission
    /// increment.
    /// </summary>
    [Fact]
    public void The_seed_version_marker_is_not_a_catalogue_key()
    {
        Assert.Null(SettingCatalog.Find(SettingKeys.Seed.Version));
        Assert.DoesNotContain(SettingKeys.Seed.Group, SettingCatalog.Groups);

        // Nor is it in doc 08 §3.8's groups, so the specification tests above must not expect a row for
        // it. §9's prose names it; §3.8's table does not list it as an editable key, which is correct.
        Assert.DoesNotContain(SettingKeys.Seed.Version, SpecifiedKeys());
    }

    /// <summary>
    /// <b>A seeded key carries a value only where doc 08 §3.8 states the default, and then that exact
    /// value.</b>
    /// </summary>
    /// <remarks>
    /// <b>This is the invented-data rule, asserted against the document rather than against itself.</b>
    /// The companion integration test compares the rows on a fresh deployment with
    /// <see cref="SettingDefinition.SeedValue"/>, which is the right check for the seeder and <i>cannot</i>
    /// catch a placeholder: a mutation that gave <c>Bank.BankName</c> a plausible bank name passed it,
    /// because the expected set was computed from the same catalogue that had just been mutated. Here the
    /// permitted set comes from §3.8's own parenthesised defaults, so a value this project's documents do
    /// not state is a red build.
    /// <para>
    /// The cost of getting this wrong is specific: an invented bank account number on a live shop is a
    /// customer transferring real money to a number nobody owns, and an invented registered address on an
    /// invoice is a false statement on a legal document. W3-03, W3-04 and the stripped footer contact
    /// details all paid for this rule.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_seeded_value_exists_only_where_doc_08_states_the_default()
    {
        var documented = SpecifiedDefaults();

        var seededWithAValue = SettingCatalog.All
            .Where(definition => definition.SeedValue is not null)
            .ToList();

        foreach (var definition in seededWithAValue)
        {
            Assert.True(
                documented.TryGetValue(definition.Key, out var expected),
                $"{definition.Key} is seeded with '{definition.SeedValue}' and doc 08 §3.8 states no "
                + "default for it. A seeded value the documents do not state is a value mistakable for "
                + "data: a bank account number nobody owns, a registered address on an invoice. Leave it "
                + "null and let the screen handle empty.");

            Assert.Equal(expected, definition.SeedValue);
        }

        // Eight today, and the test says which, so another one arriving is a decision somebody made rather
        // than a line that slipped in. The seven PayPal keys came with W4-01: each is a figure doc 08 §3.8
        // and §9 state outright (Mode = sandbox, 3.00, 7, 3, 24, 5 and 60), and the loop above has just
        // compared every one with the document. The credentials, the webhook id and the exchange rate are
        // deliberately not here - the document gives them no default, so they stay empty.
        string[] expectedSeeded =
        [
            SettingKeys.Orders.AutoDeliverAfterDays,
            SettingKeys.PayPal.Mode,
            SettingKeys.PayPal.FxBufferPercent,
            SettingKeys.PayPal.RateMaxAgeDays,
            SettingKeys.PayPal.CaptureLockHours,
            SettingKeys.PayPal.CaptureRecoveryMaxHours,
            SettingKeys.PayPal.PendingCaptureMaxDays,
            SettingKeys.PayPal.ExpiryMinutes,

            // Issue #193: doc 08 §3.8's Traffic cell, "RawRetentionDays (90), BotRetentionDays (7)".
            SettingKeys.Traffic.RawRetentionDays,
            SettingKeys.Traffic.BotRetentionDays,

            // Issue #189: doc 08 §3.8's Reviews cell, "RequireApproval (true)".
            SettingKeys.Reviews.RequireApproval,
        ];

        Assert.Equal(
            expectedSeeded.Order(StringComparer.Ordinal),
            seededWithAValue.Select(definition => definition.Key).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// <b>No deferred key is running on a figure that differs from doc 08 §3.8's own default.</b>
    /// <para>
    /// This is the half of #113 that would have been a defect rather than an omission: "anything reading a
    /// missing key falls back to whatever the code's default is, which may not be what §9 specifies". The
    /// expected numbers are parsed out of the document's own cells, so this is a doc-against-code check
    /// and not two copies of the same constant.
    /// </para>
    /// <para>
    /// Only the eight deferred keys that <i>have</i> a code-side fallback are listed. The two traffic retention
    /// periods W4-03 added here left with issue #193, which seeded them — their configuration fallback is pinned
    /// by <see cref="The_traffic_retention_fallback_is_the_seeded_value_and_the_documented_default"/> — and the
    /// two customer lockout figures took their place. The rest are read by
    /// nothing at all, which <see cref="DeferredSettingKeys"/> records per key, so there is no value to
    /// compare.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_deferred_key_with_a_code_fallback_falls_back_to_the_documented_default()
    {
        var documented = SpecifiedDefaults();

        var catalog = new CatalogOptions();
        var bankTransfer = new BankTransferOptions();
        var adminSecurity = new AdminSecurityOptions();
        var customerSecurity = new SpencerDenim.Infrastructure.Identity.Customers.CustomerSecurityOptions();

        var fallbacks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Inventory.DefaultLowStockThreshold"] =
                catalog.DefaultLowStockThreshold.ToString(CultureInfo.InvariantCulture),
            ["Catalog.NewArrivalDays"] = catalog.NewArrivalDays.ToString(CultureInfo.InvariantCulture),
            ["Catalog.BestSellerTopN"] = catalog.BestSellerTopN.ToString(CultureInfo.InvariantCulture),
            ["Orders.BankTransferExpiryHours"] =
                bankTransfer.BankTransferExpiryHours.ToString(CultureInfo.InvariantCulture),
            ["Security.StaffLockoutMaxFailures"] =
                adminSecurity.LockoutMaxFailedAttempts.ToString(CultureInfo.InvariantCulture),
            ["Security.StaffLockoutMinutes"] =
                adminSecurity.LockoutMinutes.ToString(CultureInfo.InvariantCulture),

            // CustomerSecurityOptions' docblock has claimed since W3-01 that this test pins these two; until
            // issue #193 it did not. It does now.
            ["Security.CustomerLockoutMaxFailures"] =
                customerSecurity.LockoutMaxFailedAttempts.ToString(CultureInfo.InvariantCulture),
            ["Security.CustomerLockoutMinutes"] =
                customerSecurity.LockoutMinutes.ToString(CultureInfo.InvariantCulture),
        };

        foreach (var (key, fallback) in fallbacks)
        {
            Assert.True(
                DeferredSettingKeys.Keys.Contains(key),
                $"{key} is no longer deferred, so this pin belongs with its seeded definition instead.");

            Assert.True(
                documented.TryGetValue(key, out var expected),
                $"doc 08 §3.8 states no default for {key}, so there is nothing to pin here.");

            Assert.Equal(expected, fallback);
        }
    }

    /// <summary>
    /// <b>The traffic purge's configuration fallback, its seed and doc 08 §3.8 say the same number</b> (issue #193).
    /// <para>
    /// The settings row wins, and configuration is used only while it is unset (<c>TrafficRetentionPeriods</c>).
    /// That is safe to ship to an existing database only if the two agree out of the box: a deployment that sets
    /// no <c>Traffic__*</c> variable then purges at the same age before the seeder runs, after it runs, and after
    /// somebody damages the row. Three copies of 90 and 7 — the document's cell, <c>SettingKeys.Traffic</c>'s
    /// defaults and <c>TrafficOptions</c>' — pinned against the first.
    /// </para>
    /// </summary>
    [Fact]
    public void The_traffic_retention_fallback_is_the_seeded_value_and_the_documented_default()
    {
        var documented = SpecifiedDefaults();
        var traffic = new SpencerDenim.Infrastructure.Analytics.TrafficOptions();

        foreach (var (key, configured) in new[]
        {
            (SettingKeys.Traffic.RawRetentionDays, traffic.RawRetentionDays),
            (SettingKeys.Traffic.BotRetentionDays, traffic.BotRetentionDays),
        })
        {
            Assert.True(documented.TryGetValue(key, out var expected), $"doc 08 §3.8 states no default for {key}.");
            Assert.Equal(expected, SettingCatalog.Find(key)!.SeedValue);
            Assert.Equal(expected, configured.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Doc 08 §3.8's "Groups" table, flattened to <c>Group.Key</c> strings.
    /// </summary>
    /// <remarks>
    /// The cells read like <c>BankTransferExpiryHours (48), PayPalExpiryMinutes (60)</c>, and the
    /// <c>Security</c> cell ends with a sentence after a semicolon ("2FA for GrantsAll is always on").
    /// Parenthesised text goes first — it holds the defaults, and the <c>Delivery</c> cell's own
    /// <c>(3 IslandWide; R7)</c> would otherwise make the semicolon split wrong — then everything after a
    /// semicolon, then the remainder is split on commas.
    /// </remarks>
    private static List<string> SpecifiedKeys()
    {
        var markdown = ReadRepositoryFile("docs/08-database-design.md");
        var lines = markdown.Split('\n');

        var start = Array.FindIndex(lines, line => line.StartsWith("**Groups:**", StringComparison.Ordinal));
        Assert.True(start >= 0, "Could not find the '**Groups:**' table in docs/08-database-design.md.");

        var keys = new List<string>();

        for (var index = start + 1; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (!line.StartsWith('|'))
            {
                break;
            }

            var cells = line.Trim('|').Split('|');
            if (cells.Length < 2)
            {
                continue;
            }

            var group = cells[0].Trim().Trim('`').Trim();

            // The header row and the separator row.
            if (group is "Group" or "" || group.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            var cell = Regex.Replace(cells[1], @"\([^)]*\)", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(5));
            cell = cell.Split(';')[0];

            foreach (var raw in cell.Split(','))
            {
                var name = raw.Trim().Trim('*').Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                keys.Add($"{group}.{name}");
            }
        }

        return keys;
    }

    /// <summary>
    /// The parenthesised seed defaults of doc 08 §3.8's "Groups" table, keyed by <c>Group.Key</c>. Only
    /// the cells that carry a bare number or <c>true</c>/<c>false</c>: <c>Mode (sandbox)</c> and
    /// <c>DefaultZone (3 IslandWide; R7)</c> are prose, and a parser that guessed at them would be
    /// asserting its own guess.
    /// </summary>
    private static Dictionary<string, string> SpecifiedDefaults()
    {
        var markdown = ReadRepositoryFile("docs/08-database-design.md");
        var lines = markdown.Split('\n');

        var start = Array.FindIndex(lines, line => line.StartsWith("**Groups:**", StringComparison.Ordinal));
        Assert.True(start >= 0, "Could not find the '**Groups:**' table in docs/08-database-design.md.");

        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = start + 1; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (!line.StartsWith('|'))
            {
                break;
            }

            var cells = line.Trim('|').Split('|');
            if (cells.Length < 2)
            {
                continue;
            }

            var group = cells[0].Trim().Trim('`').Trim();
            if (group is "Group" or "" || group.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var match in Regex
                .Matches(
                    cells[1],
                    // `sandbox` is spelled out rather than matching any word: the same cells carry
                    // annotations such as `ClientSecret (secret)`, which is a marker and not a default.
                    @"(?<name>[A-Za-z][A-Za-z0-9]*)\s*\((?<value>-?\d+(?:\.\d+)?|true|false|sandbox)\)",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5))
                .Cast<Match>())
            {
                defaults[$"{group}.{match.Groups["name"].Value}"] = match.Groups["value"].Value;
            }
        }

        // The cells this is read from do carry defaults; a regex that matched nothing would otherwise make
        // the pin above vacuous.
        Assert.True(defaults.Count >= 15, $"Only {defaults.Count} seed defaults were parsed from §3.8.");

        return defaults;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(path), $"Expected to find {relativePath} at {path}.");

        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "frontend")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root above the test output directory.");
    }
}
