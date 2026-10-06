using System.Text.RegularExpressions;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// "Whether a shopper can reach this address" is decided in one place: <c>CatalogVisibility</c> (GLB-11).
/// <para>
/// <b>Why this test exists.</b> The class said it was the only definition and there were four — three
/// inline <c>IsActive</c> predicates remained in <c>ProductCatalogReader</c>, in the slug resolutions that
/// decide whether a listing URL resolves at all, which are precisely the ones a sitemap <c>&lt;loc&gt;</c>
/// has to agree with. Absorbing them made the sentence true, and then the round-2 tester reverted all
/// three to inline predicates and **887 unit plus 511 integration tests passed**. Nothing would have
/// noticed a fourth definition appearing.
/// </para>
/// <para>
/// The precedent is this branch's own: <c>UNBUILT_PATHS</c> has a test for the same reason, because prose
/// is not a gate. The drift this guards against has a direction — <i>the sitemap offers a URL whose page
/// answers 404</i> — which is #30's expensive kind of wrong, and the sort of thing that is discovered from
/// a crawler's error report months later.
/// </para>
/// <para>
/// <b>What it sweeps, and what it deliberately does not.</b> Only reachability: a query over
/// <c>Departments</c>, <c>Categories</c> or <c>Products</c> that spells out <c>IsActive</c> or
/// <c>IsPublished</c> for itself. <c>IsActive</c> on variants, age groups and attribute options is left
/// alone and named below — those rows are not addresses, and widening this to "what is enabled anywhere"
/// would make it a rule nobody could satisfy.
/// </para>
/// </summary>
public sealed class CatalogVisibilityConventionTests
{
    /// <summary>The one file that may define reachability.</summary>
    private const string TheDefinition = "src/backend/SpencerDenim.Infrastructure/Catalog/CatalogVisibility.cs";

    /// <summary>
    /// The entities whose <c>IsActive</c> / <c>IsPublished</c> decides whether an <b>address</b> resolves.
    /// A deactivated row here makes a URL stop existing; that is what makes it the sitemap's business.
    /// </summary>
    private static readonly string[] AddressEntities = ["Departments", "Categories", "Products"];

    /// <summary>
    /// Every place outside <see cref="TheDefinition"/> that may still decide reachability for itself, with
    /// the reason it is not composable. Repository-relative, forward slashes.
    /// <para>
    /// These are named here rather than only in a comment so that adding a fourth is a deliberate edit to
    /// this list with a sentence attached, which is the whole point of the test.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> MayDecideForThemselves = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/backend/SpencerDenim.Infrastructure/Content/HomeContentReader.cs"] =
            "decides whether a home tile links to a department or a product from predicates inside a "
            + "`Select` projection, which cannot take an Expression<Func<T,bool>>, so it is not composable "
            + "with CatalogVisibility. Pre-existing, and its product branch omits the department and "
            + "primary-category checks VisibleProduct has — a known fourth place, recorded in "
            + "CatalogVisibility's own docblock rather than left to be discovered.",
        ["src/backend/SpencerDenim.Infrastructure/Catalog/AdminCatalogReader.cs"] =
            "the admin read model, which exists precisely to show the rows the storefront drops so that a "
            + "client can switch them back on (see IAdminCatalogReader). The storefront's definition of "
            + "visible is the wrong one here by design.",
        ["src/backend/SpencerDenim.Infrastructure/Catalog/CatalogStructureStore.cs"] =
            "the admin write path: it loads rows in order to change their IsActive, so it must see them "
            + "whatever their current value is.",
        ["src/backend/SpencerDenim.Infrastructure/Catalog/ProductWriteStore.cs"] =
            "the admin product write path (ADM-10, W1-03), for the same reason as CatalogStructureStore "
            + "above: it loads a product in order to change its IsPublished, so it must see it whatever "
            + "the current value is. What this rule trips on is narrower than that even so — two "
            + "*projections*, not predicates: a category reference that carries IsActive so the product "
            + "form can show which category a product is filed under, and a related-product reference "
            + "that carries IsPublished so the picker can mark a pick that is not live. Neither decides "
            + "whether an address is reachable, and nothing here can put a URL in the sitemap.",
        ["src/backend/SpencerDenim.Infrastructure/Catalog/AdminProductReader.cs"] =
            "the admin product read model (ADM-10, W1-03), which exists precisely to show the products "
            + "the storefront drops — an unpublished one is the only kind there is before it is finished, "
            + "so the storefront's definition of visible is the wrong one here by design, exactly as for "
            + "AdminCatalogReader above. **Listed although the rule's heuristic does not currently flag "
            + "it:** its `published` filter sits in a statement of its own, two statements after the "
            + "`dbContext.Products` that starts the query, so the per-statement scan misses it. That is "
            + "luck, not compliance, and an entry here is better than a rule that passes for a reason "
            + "nobody wrote down.",
        ["src/backend/SpencerDenim.Infrastructure/Sales/CartReader.cs"] =
            "the bag (W2-09, CART-01). Two halves, and only one of them could be composed. Its "
            + "*filtering* half, FindSellableVariantAsync, does compose VisibleProduct — it starts the "
            + "query at Products for exactly that reason, since an Expression<Func<Product,bool>> cannot "
            + "be invoked against variant.Product inside an EF predicate. What this rule then trips on "
            + "in that same statement is the **variant's** own IsActive, which CatalogVisibility "
            + "deliberately leaves local (a deactivated variant changes a size list, not whether a URL "
            + "exists). Its reading half, ReadAsync, genuinely decides for itself and cannot be "
            + "composed, for the opposite of the usual reason: it must not filter. A bag line whose "
            + "product has been withdrawn has to stay on the page, named, unpriced and removable — an "
            + "item that silently vanishes from a shopper's bag is the worst available behaviour — so it "
            + "projects IsPublished, IsDeleted and both IsActive columns and reports a per-line status "
            + "in C#. A bag is not an address, and nothing here can put a URL in the sitemap. **This "
            + "entry is keyed on the file, so it would excuse the filtering half too — The_readers_"
            + "compose_it lists CartReader.cs for exactly that reason. Do not remove one without the "
            + "other.**",
    };

    /// <summary>
    /// A LINQ predicate over one of the address entities that mentions the switch itself.
    /// <para>
    /// Matched per line, which is enough because the reader's own style keeps a <c>Where</c> on one line,
    /// and because a false negative here is a missed fourth definition while a false positive is a
    /// one-line entry in the list above. The direction of the error matters more than the cleverness of
    /// the pattern.
    /// </para>
    /// </summary>
    private static readonly Regex DecidesForItself = new(
        @"\b(IsActive|IsPublished)\b(?!\s*=[^=])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A LINQ operator that takes a predicate. Both conditions have to hold, and this one was added
    /// because the rule's first run flagged the reference-data seeder: it writes
    /// <c>dbContext.Departments.Add(new Department { … IsActive = true … })</c>, which mentions the switch
    /// inside an object initialiser and decides nothing about reachability. A single <c>=</c> is excluded
    /// by the lookahead above, and a statement with no predicate operator in it is excluded here — so
    /// writing a row, or projecting the column into a DTO, is not mistaken for a visibility rule and does
    /// not need an exclusion entry that would only clutter the list.
    /// <para>
    /// The <c>Async</c> suffix is not optional decoration: every reader in this codebase awaits
    /// <c>FirstOrDefaultAsync</c>, <c>AnyAsync</c> and <c>CountAsync</c>, so without it this rule was
    /// blind to the dominant idiom. The tester proved that by moving a real visibility predicate out of
    /// a <c>.Where(</c> and into the <c>FirstOrDefaultAsync(</c> that followed it: the drift was caught
    /// only by <see cref="The_readers_compose_it"/>'s count, whose own failure message invites the
    /// reader to lower the number — which would have hidden it.
    /// </para>
    /// </summary>
    private static readonly Regex TakesAPredicate = new(
        @"\.(Where|Any|All|Count|First|FirstOrDefault|Single|SingleOrDefault)(?:Async)?\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Which of the address entities a line is querying, if any.</summary>
    private static readonly Regex QueriesAnAddressEntity = new(
        @"dbContext\.(Departments|Categories|Products)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void Only_CatalogVisibility_decides_whether_an_address_is_reachable()
    {
        var root = FindRepositoryRoot();
        var definition = Path.Combine(root, TheDefinition.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(definition), $"{TheDefinition} is the rule's one home and it is missing.");

        var offenders = new List<string>();
        var infrastructure = Path.Combine(root, "src", "backend", "SpencerDenim.Infrastructure");

        foreach (var file in Directory.EnumerateFiles(infrastructure, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Equals(TheDefinition, StringComparison.OrdinalIgnoreCase)
                || MayDecideForThemselves.ContainsKey(relative))
            {
                continue;
            }

            var lines = File.ReadAllText(file).Split('\n');

            // A file that never queries one of the address entities cannot be deciding reachability for
            // one, whatever else it says about IsActive.
            if (!lines.Any(line => QueriesAnAddressEntity.IsMatch(line)))
            {
                continue;
            }

            var block = string.Join('\n', lines);
            foreach (var match in QueriesAnAddressEntity.Matches(block).Cast<Match>())
            {
                // The statement the query starts: up to the terminating semicolon, so a Where on a
                // following line is seen. Bounded so a runaway match cannot read the rest of the file.
                var tail = block[match.Index..];
                var end = tail.IndexOf(';', StringComparison.Ordinal);
                var statement = end < 0 ? tail[..Math.Min(tail.Length, 2_000)] : tail[..end];

                if (DecidesForItself.IsMatch(statement) && TakesAPredicate.IsMatch(statement))
                {
                    var line = block[..match.Index].Count(character => character == '\n') + 1;
                    offenders.Add($"{relative}:{line} queries {match.Groups[1].Value} and decides visibility itself");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Reachability is defined in CatalogVisibility and read everywhere else, so that the sitemap "
            + "cannot offer a URL whose page answers 404.\n\nCompose CatalogVisibility.VisibleDepartment, "
            + ".VisibleCategory or .VisibleProduct — or, if this one genuinely cannot, add it to "
            + "MayDecideForThemselves with the reason.\n\n"
            + string.Join('\n', offenders));
    }

    /// <summary>
    /// The exclusions are real files. An entry that no longer matches anything is a rule that has quietly
    /// stopped covering something, which is how an allow-list rots into a no-op.
    /// </summary>
    [Fact]
    public void Every_named_exclusion_still_exists()
    {
        var root = FindRepositoryRoot();

        foreach (var (relative, reason) in MayDecideForThemselves)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(
                File.Exists(path),
                $"{relative} is excused from the visibility rule because \"{reason}\" — and it no longer "
                + "exists, so the exclusion should go.");
        }
    }

    /// <summary>
    /// The predicates are actually composed, so the rule above cannot pass by there being nothing to
    /// compose. The storefront's product reader, the navigation tree, the sitemap and the bag all use it.
    /// <para>
    /// <b><c>CartReader</c> is in both lists, and that is the point.</b> It is in
    /// <see cref="MayDecideForThemselves"/> because its <i>reading</i> half must not filter — a bag line
    /// whose product has been withdrawn has to stay on the page — and that entry is keyed on the whole
    /// file, so without a line here its <i>filtering</i> half would be guarded by nothing at all. The
    /// tester proved exactly that: replacing <c>CatalogVisibility.VisibleProduct</c> in
    /// <c>FindSellableVariantAsync</c> with a fourth inline copy left all 921 unit tests passing. An
    /// exclusion that was added to record one half must not end up excusing the other.
    /// </para>
    /// </summary>
    [Fact]
    public void The_readers_compose_it()
    {
        var root = FindRepositoryRoot();

        // The folder travels with the file: the bag's reader does not live under Catalog, and a relative
        // "../Sales/" in a path string would be the kind of thing nobody reads twice.
        foreach (var (folder, file, expected) in new[]
        {
            ("Catalog", "ProductCatalogReader.cs", 4),
            ("Catalog", "DepartmentCatalogReader.cs", 2),
            ("Catalog", "SitemapCatalogReader.cs", 3),
            ("Sales", "CartReader.cs", 1),
        })
        {
            var path = Path.Combine(root, "src", "backend", "SpencerDenim.Infrastructure", folder, file);
            var uses = Regex.Count(WithoutComments(File.ReadAllText(path)), @"CatalogVisibility\.Visible");

            Assert.True(
                uses >= expected,
                $"{file} composes CatalogVisibility {uses} times; it was {expected}. If a query was removed "
                + "that is fine — lower this number. If one was replaced by an inline predicate, that is the "
                + "drift this file exists to catch.");
        }
    }

    /// <summary>
    /// The file with its comments taken out, because the count below is about <b>code</b>.
    /// <para>
    /// Without this the check is vacuous, and measurably so: the bag's reader was given its entry here,
    /// the composition in <c>FindSellableVariantAsync</c> was then replaced with a fourth inline copy to
    /// see the guard bite — and all 921 tests passed, because the comment above that query names
    /// <c>CatalogVisibility.VisibleProduct</c> and the regex was counting prose. A rule that a docblock
    /// can satisfy is not a rule. The other three were safe only because their docblocks happen never to name
    /// the rule — which is luck and not design; see <see cref="WithoutComments"/>, where that is measured.
    /// </para>
    /// <para>
    /// <b>Why only the bag's reader went vacuous.</b> Measured, because the first explanation written here
    /// was wrong: the other three files contain <c>CatalogVisibility.Visible</c> <b>four, two and three
    /// times and in a comment not once</b>. They were not safe because their counts were too high for
    /// prose to reach — they were safe because their docblocks never name the rule. <c>CartReader</c> was
    /// the only file that names it, which is exactly why it was the only one that could satisfy its own
    /// count from a comment. That makes the conclusion stronger rather than weaker: the other three are
    /// safe only until somebody writes a docblock that mentions the rule, which is luck and not design,
    /// and stripping comments is what turns it into design.
    /// </para>
    /// <para>
    /// Block comments first, then line comments (<c>///</c> is covered by <c>//</c>). A <c>//</c> inside a
    /// string literal would be mangled; none of these four files has one, and the direction of that error
    /// is a false positive — a missing use — which fails loudly rather than passing quietly.
    /// </para>
    /// </summary>
    private static string WithoutComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//.*$", string.Empty, RegexOptions.Multiline);
    }

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
