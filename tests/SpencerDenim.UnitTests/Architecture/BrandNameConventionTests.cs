using SpencerDenim.Domain.Branding;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// The shop's name is written out in one place per application and read everywhere else (W1-15).
/// <para>
/// <b>Why this test exists.</b> The rename from "Spencer Denim" to "Colombo Wardrobe" touched 87 strings
/// across the back end, both front-end applications and their tests. Introducing
/// <see cref="StoreBrand"/> and <c>BRAND</c> only helps if the next person reads from them, and nothing
/// about a hard-coded <c>"Colombo Wardrobe"</c> looks wrong in review — it is the right name. So the sweep
/// is the test: a new literal anywhere in production source is a red build naming the file and line.
/// </para>
/// <para>
/// <b>Why it is here rather than in the front-end suites.</b> The rule spans three source trees, and this
/// is the only suite in the repository with unrestricted filesystem access that runs on every
/// <c>dotnet test</c> — the Angular specs are bundled for a jsdom environment with no <c>node:fs</c> in
/// their type set. A convention that holds in two places and not the third would be the usual half-rule,
/// so one test covers all three. <c>projects/shared/src/lib/branding/brand.spec.ts</c> pins the front-end
/// constant's value; this pins where it may appear.
/// </para>
/// <para>
/// <b>Why tests are exempt.</b> A spec that builds its expected title out of the same constant the subject
/// reads cannot fail. <c>"New Arrivals | Colombo Wardrobe"</c> written out in a spec is the assertion doing
/// its job, so <c>*.spec.ts</c>, the Playwright suite and <c>tests/</c> are deliberately not swept — the
/// literal is pinned there on purpose, and <see cref="The_shop_name_is_the_one_the_owner_decided"/> is the
/// anchor that makes those literals meaningful.
/// </para>
/// </summary>
public sealed class BrandNameConventionTests
{
    /// <summary>
    /// The files that may spell the name out, each with the reason it cannot read the constant instead.
    /// Repository-relative, forward slashes.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedToSpellItOut = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/backend/SpencerDenim.Domain/Branding/StoreBrand.cs"] =
            "the back end's definition of the name",
        ["src/frontend/projects/shared/src/lib/branding/brand.ts"] =
            "the front end's definition of the name, shared by the storefront and the admin app",
        ["src/frontend/projects/storefront/src/index.html"] =
            "the storefront's static shell: a document the build does not run TypeScript through, so it "
            + "cannot import BRAND. Every route replaces its title anyway",
        ["src/frontend/projects/admin/src/index.html"] =
            "the admin application's static shell, for the same reason",
        ["Directory.Build.props"] =
            "the assemblies' Product metadata: MSBuild cannot read a C# constant",
    };

    /// <summary>Source extensions worth sweeping: anything a visible string can be written in.</summary>
    private static readonly string[] SourceExtensions = [".cs", ".ts", ".html", ".css", ".mjs", ".json", ".props"];

    /// <summary>
    /// Every spelling of the name worth sweeping for, derived from the constant so a rename moves them all.
    /// <para>
    /// The percent-encoded form is here because of a real miss: the first sweep of this rename searched for
    /// <c>"Spencer Denim"</c> and found 87 places, and the one it could not see was an authenticator URI
    /// asserted as <c>otpauth://totp/Spencer%20Denim%20Admin:</c> — the same string with the space escaped.
    /// The slug form is here for the same reason one step on: the recovery-code download derives its
    /// filename from the name, and a hand-written <c>colombo-wardrobe-...</c> would read as correct.
    /// </para>
    /// </summary>
    private static IEnumerable<string> Spellings()
    {
        yield return StoreBrand.Name;
        yield return Uri.EscapeDataString(StoreBrand.Name);
        yield return StoreBrand.Name.Replace(' ', '-');
    }

    /// <summary>
    /// The anchor. Without this, every other assertion about the name is relative to the constant and a
    /// typo in the constant would be invisible everywhere at once.
    /// </summary>
    [Fact]
    public void The_shop_name_is_the_one_the_owner_decided()
    {
        Assert.Equal("Colombo Wardrobe", StoreBrand.Name);
        Assert.Equal("Colombo Wardrobe Admin", StoreBrand.AdminName);
        Assert.Equal("Women's clothing | Colombo Wardrobe", StoreBrand.PageTitle("Women's clothing"));
    }

    [Fact]
    public void The_shop_name_is_never_written_out_in_production_source()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();
        var allowedThatWereFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in SourceFiles(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            var lines = text.Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                if (!Spellings().Any(spelling => lines[i].Contains(spelling, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (AllowedToSpellItOut.ContainsKey(relative))
                {
                    allowedThatWereFound.Add(relative);
                    continue;
                }

                offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"The shop's name must be read from StoreBrand.Name (C#) or BRAND.name (@spencer-denim/shared), "
            + $"not written out — in any spelling ({string.Join(", ", Spellings())}). "
            + $"{offenders.Count} place(s) write it out:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));

        // The exemption list has to rot loudly rather than quietly: if one of these files stops containing
        // the name — renamed, deleted, or finally able to read the constant — the entry is dead and the
        // sweep is weaker than it reads.
        var deadEntries = AllowedToSpellItOut.Keys.Except(allowedThatWereFound, StringComparer.OrdinalIgnoreCase);
        Assert.True(
            !deadEntries.Any(),
            "These files are exempted from the sweep but no longer spell the name out, so the exemption is "
            + "stale and should be removed: " + string.Join(", ", deadEntries));
    }

    /// <summary>
    /// Directories the walk never enters: build output, dependencies and tool-generated reports.
    /// <para>
    /// <b><c>test-results</c> and <c>playwright-report</c> are on this list because of a real, transient
    /// failure.</b> A full <c>dotnet test</c> run started while a Playwright run was in flight swept
    /// <c>src/frontend/test-results</c>, whose artefacts carry the test titles — so the guard reported a
    /// hard-coded brand name in a file no human wrote, and passed on the next run when the directory was
    /// empty again. A convention test that fails depending on what another tool left on disk is worse than
    /// no test, because the next red build gets ignored. Everything here is git-ignored: the rule is that
    /// the sweep reads source, never anything generated.
    /// </para>
    /// </summary>
    private static readonly string[] GeneratedDirectories =
    [
        "bin", "obj", "node_modules", "dist", ".angular", "out-tsc",
        "test-results", "playwright-report", "coverage", ".vite", ".nx", ".git",
    ];

    private static IEnumerable<string> SourceFiles(string root)
    {
        // The build properties carry the name in assembly metadata, so they are swept too.
        yield return Path.Combine(root, "Directory.Build.props");

        // Only the two source trees. `tests/` and `.github/` are excluded wholesale, for the reason in the
        // class summary: a CI smoke check that greps the served HTML for the wordmark is an assertion, and
        // writing the expected text out is what makes it one.
        string[] trees = [Path.Combine(root, "src", "backend"), Path.Combine(root, "src", "frontend")];

        foreach (var tree in trees)
        {
            Assert.True(Directory.Exists(tree), $"Source tree not found: {tree}");

            foreach (var file in WalkSource(tree))
            {
                if (!SourceExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsTest(Path.GetRelativePath(root, file).Replace('\\', '/')))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    /// <summary>
    /// Every file below <paramref name="directory"/>, refusing to descend into a generated directory.
    /// Pruning the walk rather than filtering its results is the point: it never reads a file another tool
    /// is in the middle of writing.
    /// </summary>
    private static IEnumerable<string> WalkSource(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (GeneratedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in WalkSource(child))
            {
                yield return file;
            }
        }
    }

    private static bool IsTest(string relative) =>
        relative.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("src/frontend/e2e/", StringComparison.OrdinalIgnoreCase);

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
