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
    /// <b>No deferred key is running on a figure that differs from doc 08 §3.8's own default.</b>
    /// <para>
    /// This is the half of #113 that would have been a defect rather than an omission: "anything reading a
    /// missing key falls back to whatever the code's default is, which may not be what §9 specifies". The
    /// expected numbers are parsed out of the document's own cells, so this is a doc-against-code check
    /// and not two copies of the same constant.
    /// </para>
    /// <para>
    /// Only the six deferred keys that <i>have</i> a code-side fallback are listed. The rest are read by
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
                    @"(?<name>[A-Za-z][A-Za-z0-9]*)\s*\((?<value>-?\d+(?:\.\d+)?|true|false)\)",
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
