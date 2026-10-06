using System.Text.RegularExpressions;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// The admin product client and its browser-test stub speak the same enum vocabulary as the API.
/// <para>
/// <b>Why this test exists.</b> <c>Program.cs</c> registers a global <c>JsonStringEnumConverter</c> with its
/// reason written down — "Enums travel as their names … so a client never has to keep a copy of our numeric
/// values in sync" — and ADM-10 shipped with the client keeping exactly such a copy:
/// <c>export type ProductMediaRole = 1 | 2 | 3;</c> against an API that answers <c>"Gallery"</c>. Two of
/// ADM-10's own scope bullets were broken by it: setting a badge answered <b>500</b> and reordering,
/// re-captioning or removing a photograph on any saved product answered <b>400</b>.
/// </para>
/// <para>
/// <b>And why the suites did not catch it.</b> <c>e2e/support/admin-product-stub.mjs</c> answered
/// <c>mediaRole: 1</c> too. A stub is a second implementation of the API, so 43 Playwright and 348 Vitest
/// tests agreed with each other about a contract that did not exist. The tester's instruction was to make
/// the stub's shapes derive from the real DTOs so they cannot diverge again; a <c>.mjs</c> file cannot
/// import a C# enum, so the binding is this test instead — it reads both TypeScript files and checks their
/// vocabularies against <see cref="Enum.GetNames{TEnum}"/>. Add a badge, rename a role, and the build goes
/// red here rather than at a client six months later.
/// </para>
/// <para>
/// It needs no database and no browser: it is a file read, like
/// <see cref="IndexDeclarationConventionTests"/>.
/// </para>
/// </summary>
public sealed class AdminProductEnumContractTests
{
    private const string ModelFile = "src/frontend/projects/admin/src/app/core/products/admin-product.model.ts";

    private const string StubFile = "src/frontend/e2e/support/admin-product-stub.mjs";

    /// <summary>
    /// Doc 08 §3.2: <c>LowStock</c> is computed at read time and never stored, so it is the one
    /// <see cref="ProductBadge"/> a staff member cannot assign and the one the client must not offer.
    /// </summary>
    private static readonly string[] AssignableBadgeNames =
        [nameof(ProductBadge.New), nameof(ProductBadge.Sale), nameof(ProductBadge.OnlineExclusive), nameof(ProductBadge.BestSeller)];

    [Fact]
    public void The_client_declares_the_media_roles_the_enum_has()
    {
        var declared = UnionMembers(ReadRepositoryFile(ModelFile), "ProductMediaRole");

        Assert.Equal(Enum.GetNames<ProductMediaRole>().Order(StringComparer.Ordinal), declared);
    }

    [Fact]
    public void The_client_declares_the_media_asset_types_the_enum_has()
    {
        var declared = UnionMembers(ReadRepositoryFile(ModelFile), "MediaAssetType");

        Assert.Equal(Enum.GetNames<MediaAssetType>().Order(StringComparer.Ordinal), declared);
    }

    /// <summary>
    /// The badge union is the assignable subset, not every name on the enum — so this asserts the subset
    /// deliberately rather than asserting <see cref="Enum.GetNames{TEnum}"/> and quietly letting
    /// <c>LowStock</c> onto a form that cannot store it.
    /// </summary>
    [Fact]
    public void The_client_offers_the_four_assignable_badges_and_not_low_stock()
    {
        var model = ReadRepositoryFile(ModelFile);

        Assert.Equal(AssignableBadgeNames.Order(StringComparer.Ordinal), UnionMembers(model, "ProductBadge"));

        // The picker's own list, which is what actually reaches a <select>.
        var offered = Regex
            .Matches(model, @"\{\s*value:\s*'(?<name>\w+)',\s*label:")
            .Select(match => match.Groups["name"].Value)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(AssignableBadgeNames.Order(StringComparer.Ordinal), offered);
        Assert.DoesNotContain(nameof(ProductBadge.LowStock), offered);
    }

    /// <summary>
    /// Every enum name the client or the stub writes down is a real member of the enum it belongs to.
    /// <para>
    /// The unions above pin the sets; this pins the <i>literals</i> scattered through both files — the
    /// <c>MEDIA_ROLE</c> constants, the seeded stub rows, the role comparisons. A typo in any of them was
    /// previously a silent disagreement with the API.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(ModelFile)]
    [InlineData(StubFile)]
    public void Every_role_and_type_literal_in_the_client_is_a_real_enum_member(string relativePath)
    {
        var text = WithoutComments(ReadRepositoryFile(relativePath));

        var roleNames = Enum.GetNames<ProductMediaRole>().ToHashSet(StringComparer.Ordinal);
        var typeNames = Enum.GetNames<MediaAssetType>().ToHashSet(StringComparer.Ordinal);
        var badgeNames = Enum.GetNames<ProductBadge>().ToHashSet(StringComparer.Ordinal);

        AssertLiterals("mediaRole", roleNames);
        AssertLiterals("mediaType", typeNames);
        AssertLiterals("badgeOverride", badgeNames);

        void AssertLiterals(string field, IReadOnlySet<string> allowed)
        {
            // `field: 'Name'` and `field === 'Name'` / `!== 'Name'`, which is every shape either file uses.
            var found = Regex
                .Matches(text, $@"\b{field}\b\s*(?::|={{2,3}}|!={{1,2}})\s*'(?<name>[^']*)'")
                .Select(match => match.Groups["name"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var unknown = found.Where(name => !allowed.Contains(name)).Order(StringComparer.Ordinal).ToList();

            Assert.True(
                unknown.Count == 0,
                $"{relativePath} uses {field} value(s) that are not members of the matching C# enum: "
                + $"{string.Join(", ", unknown)}. The API serialises enums as their names "
                + $"(Program.cs's JsonStringEnumConverter), so the allowed set is: "
                + $"{string.Join(", ", allowed.Order(StringComparer.Ordinal))}.");
        }
    }

    /// <summary>
    /// Neither file may go back to numbers.
    /// <para>
    /// The guard is shaped as "no numeric literal is assigned to one of these fields" rather than as a
    /// search for <c>1 | 2 | 3</c>, because the union was only one of the places the numbers lived: the
    /// seeded stub rows, the <c>&lt;option [ngValue]&gt;</c> values and the comparisons each had their own
    /// copy, and fixing the union alone would have left the rest.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(ModelFile)]
    [InlineData(StubFile)]
    public void Neither_the_client_nor_the_stub_assigns_a_number_to_an_enum_field(string relativePath)
    {
        var text = WithoutComments(ReadRepositoryFile(relativePath));

        var offenders = new List<string>();

        foreach (var field in new[] { "mediaRole", "mediaType", "badgeOverride" })
        {
            foreach (var match in Regex
                .Matches(text, $@"\b{field}\b\s*(?::|={{2,3}}|!={{1,2}})\s*(?<value>-?\d+)")
                .Cast<Match>())
            {
                offenders.Add($"{field} = {match.Groups["value"].Value}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{relativePath} assigns a number to an enum field: {string.Join(", ", offenders)}. Enums cross "
            + "this API as their names (Program.cs), and a client that keeps a copy of the numeric values "
            + "is the defect this rule exists to prevent — it broke the badge field and the whole gallery "
            + "editor while every suite stayed green.");
    }

    /// <summary>The members of a TypeScript string-literal union, in order, with the quotes removed.</summary>
    private static List<string> UnionMembers(string text, string typeName)
    {
        var declaration = Regex.Match(text, $@"export type {typeName}\s*=\s*(?<body>[^;]+);");

        Assert.True(
            declaration.Success,
            $"no `export type {typeName}` in the client model. If it was renamed, rename it here too rather "
            + "than deleting the rule.");

        var members = Regex
            .Matches(declaration.Groups["body"].Value, @"'(?<name>[^']+)'")
            .Select(match => match.Groups["name"].Value)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            members.Count > 0,
            $"`{typeName}` is not a union of string literals. Enums cross this API as their names "
            + "(Program.cs's JsonStringEnumConverter), so a numeric union is the defect, not the fix.");

        return members;
    }

    /// <summary>
    /// Strips line and block comments, so the docblocks that <i>describe</i> the old numeric shapes do not
    /// trip the rules that forbid them.
    /// </summary>
    private static string WithoutComments(string text) =>
        Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"//[^\n]*", string.Empty);

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(path), $"{relativePath} is missing. If it moved, point this rule at it.");

        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SpencerDenim.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }
}
