using System.Text.RegularExpressions;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// <see cref="ContentPageSlugRules.Reserved"/> against the storefront's own route table (GLB-10).
/// <para>
/// <b>Why this test exists.</b> A content page is served from a single URL segment, and so are the
/// search page, the bag, the wishlist, checkout and the account area. The storefront's route table is
/// ordered, and those routes are declared <i>above</i> the content-page route — they have to be, or a
/// department called "women" would be looked up as a page on every navigation. So a page saved on one of
/// those slugs exists in the database, appears in the admin list, appears in the footer, appears in
/// <c>sitemap.xml</c>, and <b>cannot be opened by anybody</b>: the address resolves to the bag.
/// </para>
/// <para>
/// That is a defect with no symptom on the screen that caused it, which is the kind this project has
/// shipped before, so the write path refuses the slug. This test is what stops the refusal list drifting
/// from the route table: it reads <c>app.routes.ts</c> and fails if a single-segment route has appeared
/// that the list does not hold. The same binding <see cref="AdminProductEnumContractTests"/> uses to
/// keep a TypeScript union and a C# enum in step, and it needs no database and no browser.
/// </para>
/// </summary>
public sealed class ContentPageSlugRulesTests
{
    private const string RoutesFile = "src/frontend/projects/storefront/src/app/app.routes.ts";

    /// <summary>A quoted single-segment route path: <c>{ path: 'bag', …</c>.</summary>
    private static readonly Regex RoutePath = new(
        @"path:\s*'(?<path>[^']*)'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>An entry in <c>UNBUILT_PATHS</c>, which claims an address the shell links and has no page.</summary>
    private static readonly Regex UnbuiltPaths = new(
        @"export const UNBUILT_PATHS\s*=\s*\[(?<body>[^\]]*)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>
    /// Paths in the route table that are not a reserved slug and do not need to be.
    /// <para>
    /// <c>:slug</c> is the content-page route itself; <c>:department</c> matches any single segment and
    /// is the route the content route is declared <i>after</i>, so the department slugs are checked
    /// against the database instead (see <see cref="ContentPageSlugRules"/>); <c>''</c> is the home page
    /// and <c>'**'</c> the catch-all, neither of which is a slug a page could take.
    /// </para>
    /// </summary>
    private static readonly string[] NotSlugs = [string.Empty, "**", ":slug", ":department"];

    /// <summary>
    /// Every single-segment route the storefront declares is a slug a content page may not take.
    /// <para>
    /// This is the direction that matters: a route added without its entry here is a page a client can
    /// create and nobody can open.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_single_segment_route_is_a_reserved_slug()
    {
        var missing = SingleSegmentRoutes()
            .Where(path => !ContentPageSlugRules.IsReserved(path))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "The storefront declares these single-segment routes, and ContentPageSlugRules.Reserved does "
            + "not hold them. A content page saved on one of these slugs would exist, be listed in the "
            + "admin, be linked from the footer and be in sitemap.xml, and would resolve to the other "
            + $"route instead — unopenable, with nothing on any screen to say why: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Every address <c>UNBUILT_PATHS</c> still claims is reserved too.
    /// <para>
    /// An unbuilt path has no page <i>yet</i>. A content page saved on one would work today and stop
    /// working the day its real route lands, which is worse than failing now: nothing would connect the
    /// two changes.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_unbuilt_path_is_a_reserved_slug()
    {
        var text = ReadRepositoryFile(RoutesFile);
        var body = UnbuiltPaths.Match(text);

        Assert.True(body.Success, $"Could not find UNBUILT_PATHS in {RoutesFile}.");

        var paths = Regex
            .Matches(body.Groups["body"].Value, @"'(?<path>[^']+)'")
            .Select(match => match.Groups["path"].Value)
            .ToList();

        Assert.All(paths, path => Assert.True(
            ContentPageSlugRules.IsReserved(path),
            $"UNBUILT_PATHS still claims /{path}, so a content page must not be able to take that slug."));
    }

    /// <summary>
    /// The six seeded slugs are <b>not</b> reserved, which is the other direction and just as easy to
    /// get wrong: a list that reserved <c>returns</c> would make the seeder fail on a fresh database.
    /// </summary>
    [Fact]
    public void None_of_the_six_seeded_slugs_is_reserved()
    {
        Assert.All(ContentPageKeys.Seeds, seed => Assert.False(
            ContentPageSlugRules.IsReserved(seed.Slug),
            $"The seeder creates /{seed.Slug}, so reserving it would make a fresh database fail to seed."));
    }

    /// <summary>
    /// Nor is anything a client is plausibly going to want, which is the cost side of the list being
    /// generous. If one of these ever has to be reserved, the client needs to be told before they write
    /// the page rather than after.
    /// </summary>
    [Theory]
    [InlineData("size-guide")]
    [InlineData("faq")]
    [InlineData("stores")]
    [InlineData("careers")]
    [InlineData("delivery-and-returns")]
    [InlineData("our-story")]
    public void A_slug_a_client_would_want_is_not_reserved(string slug) =>
        Assert.False(ContentPageSlugRules.IsReserved(slug));

    /// <summary>Case-insensitively, to match the database collation and the handler's lower-casing.</summary>
    [Fact]
    public void The_check_is_case_insensitive() => Assert.True(ContentPageSlugRules.IsReserved("BAG"));

    /// <summary>
    /// A guard on the sweep itself. If the route file moves or its shape changes, the rule above would
    /// pass by finding nothing to check — the failure mode of every test that reads a file.
    /// </summary>
    [Fact]
    public void The_sweep_found_the_routes_it_is_checking()
    {
        var routes = SingleSegmentRoutes();

        Assert.True(
            routes.Count >= 6,
            $"Only {routes.Count} single-segment routes were found in {RoutesFile}. The file has moved or "
            + "its shape has changed, and this rule would otherwise pass by checking nothing.");

        // The ones that certainly exist, named, so a regex that matched the wrong thing is visible.
        Assert.Contains("bag", routes);
        Assert.Contains("search", routes);
    }

    private static List<string> SingleSegmentRoutes()
    {
        var text = ReadRepositoryFile(RoutesFile);

        return RoutePath
            .Matches(text)
            .Select(match => match.Groups["path"].Value)
            .Where(path => !NotSlugs.Contains(path, StringComparer.Ordinal))
            .Where(path => !path.Contains('/', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
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
