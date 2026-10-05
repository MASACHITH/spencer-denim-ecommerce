using System.Text.RegularExpressions;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// The two rules that make an index declared in an EF configuration provably reach the database.
/// <para>
/// <b>Why this test exists.</b> W3-03's first generated migration was missing
/// <c>UX_Payments_OneSettlement</c> and <c>IX_Payments_Exceptions</c>. Two
/// <c>HasIndex(p =&gt; p.OrderId)</c> calls, and two <c>HasIndex(p =&gt; p.CreatedAtUtc)</c> calls, had each
/// collapsed into a single index: EF treats a repeated <c>HasIndex</c> over the same property set as
/// <i>reconfiguring the same index</i>, so the second call renamed the first one instead of declaring a
/// second. It built clean, <c>has-pending-model-changes</c> reported nothing, and every test of the
/// open-payment rule still passed — while the unique index that stops an order being settled twice simply
/// did not exist.
/// </para>
/// <para>
/// <b>Why it is here and not in the schema tests.</b> <c>SalesSchemaTests</c> pins the 31 indexes doc 08
/// §3.5 names, row by row, which is the right thing for those 31 and protects nothing else: an index added
/// by W3-05, ADM-13, ADM-14 or <c>ops.*</c> without its row is unprotected against exactly the collapse
/// above. These two rules are shaped the other way round — they enumerate nothing and apply to every index
/// in the repository, now and later — so they live with the other convention tests, need no database, and
/// cost a file read.
/// </para>
/// </summary>
public sealed class IndexDeclarationConventionTests
{
    /// <summary>Where entity configurations live. Every <c>HasIndex</c> in the codebase is under here.</summary>
    private const string ConfigurationsFolder =
        "src/backend/SpencerDenim.Infrastructure/Persistence/Configurations";

    /// <summary>Where migrations live. Index creation is the <c>Up</c>/<c>Down</c> bodies, not the snapshot.</summary>
    private const string MigrationsFolder =
        "src/backend/SpencerDenim.Infrastructure/Persistence/Migrations";

    /// <summary>
    /// A guard on the sweep itself. If a refactor moves configurations elsewhere, these rules would pass by
    /// finding nothing to check, which is the failure mode of every test that scans a folder.
    /// </summary>
    private const int FewestIndexDeclarationsExpected = 120;

    /// <summary>The database name an index declaration asks for.</summary>
    private static readonly Regex DatabaseName = new(
        @"HasDatabaseName\(""(?<name>[^""]+)""\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The name a migration creates an index under. Only <c>CreateIndex</c>: an index name that appears
    /// nowhere but the model snapshot has not been written to any database.
    /// </summary>
    private static readonly Regex CreatedIndexName = new(
        @"CreateIndex\(\s*name:\s*""(?<name>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The start of an index declaration. Its argument list is then read by balancing brackets.</summary>
    private const string IndexDeclaration = ".HasIndex(";

    /// <summary>A C# class declaration, so a file holding several configurations is read as several.</summary>
    private static readonly Regex ClassDeclaration = new(
        @"\b(?:internal|public|private)\s+(?:sealed\s+)?class\s+(?<name>\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A property read inside an index's lambda: <c>payment.OrderId</c> or the <c>new { … }</c> members.</summary>
    private static readonly Regex LambdaProperty = new(
        @"\w+\.(?<property>\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every <c>HasDatabaseName</c> in a configuration is an index some migration actually creates.
    /// <para>
    /// This is the rule that catches a collapse after the fact, and it catches it <b>whatever</b> swallowed
    /// the declaration. When two <c>HasIndex</c> calls merge, the surviving index keeps one of the two
    /// names and the other is left in the source asking for an index that no migration writes — which is
    /// precisely what <c>UX_Payments_OneSettlement</c> did, silently, past a clean build and a quiet
    /// <c>has-pending-model-changes</c>. It also fails, usefully, when a declaration is added and the
    /// migration is never regenerated.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_index_name_in_source_is_created_by_a_migration()
    {
        var root = FindRepositoryRoot();

        var declared = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in ConfigurationFiles(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var text = File.ReadAllText(file);

            foreach (var match in DatabaseName.Matches(text).Cast<Match>())
            {
                var name = match.Groups["name"].Value;
                var line = text[..match.Index].Count(character => character == '\n') + 1;
                declared.TryAdd(name, $"{relative}:{line}");
            }
        }

        Assert.True(
            declared.Count >= FewestIndexDeclarationsExpected,
            $"Only {declared.Count} index names were found under {ConfigurationsFolder}, which is fewer than "
            + "this rule has ever seen. Either the configurations moved, or this sweep has stopped sweeping. "
            + "Point it at the new location rather than lowering the number.");

        var created = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in MigrationFiles(root))
        {
            foreach (var match in CreatedIndexName.Matches(File.ReadAllText(file)).Cast<Match>())
            {
                created.Add(match.Groups["name"].Value);
            }
        }

        var missing = declared
            .Where(entry => !created.Contains(entry.Key))
            .Select(entry => $"{entry.Key}  (asked for at {entry.Value})")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Each index below is named by a HasDatabaseName in an entity configuration and is created by no "
            + "migration, so it does not exist in any database.\n\nThe usual cause is two HasIndex calls "
            + "over the same property set collapsing into one — EF reconfigures the first index instead of "
            + "declaring a second, and the losing name survives only in the source. The other cause is a "
            + "migration that was never regenerated: run `dotnet ef migrations add`.\n\n"
            + string.Join('\n', missing));
    }

    /// <summary>
    /// A property set used by more than one index on the same entity is declared with the <b>named</b>
    /// <c>HasIndex(expression, name)</c> overload every time.
    /// <para>
    /// This is the same rule one step earlier: it fails at the declaration rather than at the missing
    /// migration, and it does not need the index to have a <c>HasDatabaseName</c> to be seen. The name
    /// argument is not cosmetic — it is the model-level identity EF groups by, and it is the only thing
    /// that makes a second <c>HasIndex</c> over the same properties a second index rather than an edit to
    /// the first.
    /// </para>
    /// <para>
    /// Grouped per class, not per file: <c>LookupConfigurations.cs</c> and
    /// <c>ProductDetailConfigurations.cs</c> each hold several configurations, and two different entities
    /// indexing a property of the same name is not a collision.
    /// </para>
    /// </summary>
    [Fact]
    public void Two_indexes_over_one_property_set_are_both_named()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();
        var total = 0;

        foreach (var file in ConfigurationFiles(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            var classes = ClassDeclaration.Matches(text).Cast<Match>().ToList();

            var byPropertySet = new Dictionary<string, List<(string Where, bool Named)>>(StringComparer.Ordinal);

            foreach (var (at, arguments) in IndexDeclarations(text))
            {
                total++;

                var line = text[..at].Count(character => character == '\n') + 1;

                // The class this call is in: the last class declared above it.
                var owner = classes.LastOrDefault(candidate => candidate.Index < at)?
                    .Groups["name"].Value ?? Path.GetFileNameWithoutExtension(file);

                var named = NameArgument(arguments) is not null;
                var properties = PropertySet(arguments);
                var key = $"{owner}|{properties}";

                if (!byPropertySet.TryGetValue(key, out var declarations))
                {
                    declarations = [];
                    byPropertySet[key] = declarations;
                }

                declarations.Add(($"{relative}:{line}", named));
            }

            foreach (var (key, declarations) in byPropertySet)
            {
                if (declarations.Count < 2 || declarations.All(declaration => declaration.Named))
                {
                    continue;
                }

                var parts = key.Split('|');
                var unnamed = declarations.Where(declaration => !declaration.Named).Select(d => d.Where);

                offenders.Add(
                    $"{parts[0]} declares {declarations.Count} indexes over ({parts[1]}); "
                    + $"unnamed at {string.Join(", ", unnamed)}");
            }
        }

        Assert.True(
            total >= FewestIndexDeclarationsExpected,
            $"Only {total} HasIndex calls were found under {ConfigurationsFolder}, which is fewer than this "
            + "rule has ever seen. Point it at the new location rather than lowering the number.");

        Assert.True(
            offenders.Count == 0,
            "EF treats a second HasIndex over the same property set as further configuration of the first "
            + "index, not as a second index — so the declarations below do not all survive into the "
            + "migration, and the one that loses is gone without a build warning or a pending-model-change."
            + "\n\nUse the named overload, HasIndex(expression, \"UX_Table_Thing\"), on every declaration "
            + "over a shared property set.\n\n"
            + string.Join('\n', offenders));
    }

    /// <summary>
    /// Every <c>.HasIndex(</c> call in a file, as (offset of the call, its argument list). The argument list
    /// is read by balancing brackets rather than by a regex, so a <c>new { … }</c> property set and a
    /// declaration split over lines are both read whole.
    /// </summary>
    private static IEnumerable<(int At, string Arguments)> IndexDeclarations(string text)
    {
        for (var at = text.IndexOf(IndexDeclaration, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(IndexDeclaration, at + 1, StringComparison.Ordinal))
        {
            var open = at + IndexDeclaration.Length;
            var depth = 1;
            var position = open;

            while (position < text.Length && depth > 0)
            {
                depth += text[position] switch
                {
                    '(' => 1,
                    ')' => -1,
                    _ => 0,
                };

                position++;
            }

            // An unbalanced call means the file does not compile; the build is the right place to say so.
            if (depth == 0)
            {
                yield return (at, text[open..(position - 1)]);
            }
        }
    }

    /// <summary>The second argument of a <c>HasIndex</c> call, when it was given one.</summary>
    private static string? NameArgument(string arguments)
    {
        var depth = 0;

        for (var position = 0; position < arguments.Length; position++)
        {
            var character = arguments[position];

            if (character is '(' or '{' or '[')
            {
                depth++;
            }
            else if (character is ')' or '}' or ']')
            {
                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                var rest = arguments[(position + 1)..].Trim();
                return rest.StartsWith('"') ? rest : null;
            }
        }

        return null;
    }

    /// <summary>
    /// The properties an index is keyed on, in order, from its lambda. Order is part of the identity of an
    /// index, so this does not sort: <c>(A, B)</c> and <c>(B, A)</c> are different indexes and EF treats
    /// them as such.
    /// </summary>
    private static string PropertySet(string arguments)
    {
        var depth = 0;
        var end = arguments.Length;

        for (var position = 0; position < arguments.Length; position++)
        {
            var character = arguments[position];

            if (character is '(' or '{' or '[')
            {
                depth++;
            }
            else if (character is ')' or '}' or ']')
            {
                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                end = position;
                break;
            }
        }

        var expression = arguments[..end];
        var arrow = expression.IndexOf("=>", StringComparison.Ordinal);
        var body = arrow < 0 ? expression : expression[(arrow + 2)..];

        return string.Join(
            ", ",
            LambdaProperty.Matches(body).Cast<Match>().Select(match => match.Groups["property"].Value));
    }

    private static IEnumerable<string> ConfigurationFiles(string root) =>
        Directory
            .EnumerateFiles(
                Path.Combine(root, ConfigurationsFolder.Replace('/', Path.DirectorySeparatorChar)),
                "*.cs",
                SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// The migration bodies. <c>*.Designer.cs</c> and the model snapshot are excluded deliberately: they
    /// describe the model, not what was written to a database, and an index that exists only there is the
    /// very thing this rule is looking for.
    /// </summary>
    private static IEnumerable<string> MigrationFiles(string root) =>
        Directory
            .EnumerateFiles(
                Path.Combine(root, MigrationsFolder.Replace('/', Path.DirectorySeparatorChar)),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(file => !file.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .Where(file => !Path.GetFileName(file).Equals("AppDbContextModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SpencerDenim.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate SpencerDenim.sln above the test output directory.");
    }
}
