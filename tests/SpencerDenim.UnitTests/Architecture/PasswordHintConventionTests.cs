using System.Text.RegularExpressions;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// The password rule is stated in one place and copied to exactly three others, and every copy is pinned to
/// the original: the storefront's hint, the admin app's hint, and the deployment check that guards the
/// seeded first Super Admin's password (W3-01, ACC-01, issue #200).
/// </summary>
/// <remarks>
/// <b>Why copies exist at all.</b> <see cref="PasswordPolicy.Requirement"/> is what the server enforces, and
/// each front end has to show it <i>before</i> a person presses anything — WCAG 2.2
/// <b>3.3.2 Labels or Instructions</b>, and on a password field doubly so, because they cannot see what
/// they typed. There is no endpoint that serves the sentence and inventing one would be a request on
/// every page load for a string that changes once a year. So each front end holds a copy. The deployment
/// check is a shell script that cannot reference a C# constant at all.
/// <para>
/// <b>Why the copies are pinned here rather than trusted.</b> This is #126's defect exactly: a
/// <c>maxlength</c> of 120 against a 100-character column passed the form, the stub and sixteen browser
/// tests, because two places stated one rule and nothing compared them. A form that promises ten
/// characters while the server demands twelve is the same defect in copy — and worse, because the
/// person meets the stated rule and is refused anyway. Until issue #200 the admin app was a fourth
/// statement of a <i>different</i> rule (six characters), which is how staff stayed weaker than customers
/// without anything noticing.
/// </para>
/// <para>
/// <b>Why it is a C# test reading TypeScript and shell.</b> <c>BrandNameConventionTests</c> records the
/// reason at length: this is the only suite in the repository with unrestricted filesystem access that
/// runs on every <c>dotnet test</c>, because the Angular specs are bundled for a jsdom environment with
/// no <c>node:fs</c> in their type set. A convention that held on one side and not the other would be
/// the usual half-rule.
/// </para>
/// </remarks>
public sealed partial class PasswordHintConventionTests
{
    private const string StorefrontModel =
        "src/frontend/projects/storefront/src/app/core/account/account.model.ts";

    private const string AdminModel =
        "src/frontend/projects/admin/src/app/core/auth/password-rule.ts";

    private const string DeployLibrary = "deploy/scripts/lib.sh";

    /// <summary>
    /// Each front end's copy is byte-identical to the server's constant.
    /// </summary>
    /// <remarks>
    /// <b>Asserted as a substring of the file, not parsed out of it.</b> A parser for a TypeScript string
    /// literal is a second thing to get wrong, and what matters is only that the exact sentence the
    /// server enforces appears in the file the forms read it from. If somebody rewords
    /// <see cref="PasswordPolicy.Requirement"/> — raises the minimum, drops the symbol rule —
    /// this fails and names the file to change.
    /// </remarks>
    [Theory]
    [InlineData(StorefrontModel)]
    [InlineData(AdminModel)]
    public void A_front_ends_password_hint_is_the_servers_own_sentence(string model)
    {
        var source = ReadRepositoryFile(model);

        Assert.Contains(
            PasswordPolicy.Requirement,
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule is written out in exactly one file per front end, and the forms read it from there.
    /// </summary>
    /// <remarks>
    /// <b>One copy per app, not one per form.</b> Two storefront forms ask for a password — registration and
    /// the reset — and two admin screens do — change-password and the new-staff-user form. If each held its
    /// own sentence there would be four places to change and three chances to miss one. They import the
    /// constant; this is what stops the next form from pasting the words instead.
    /// <para>
    /// Keyed on a distinctive fragment rather than the whole sentence, so that rewording the rule does
    /// not make this test pass vacuously by matching nothing: the fragment is taken from the constant
    /// itself, so there is nothing here to keep in step by hand.
    /// </para>
    /// <para>
    /// <b>The admin app used to be an exclusion here</b>, because it stated a different rule (six
    /// characters) whose tail matched the fragment. Issue #200 made it the same rule, so it is swept
    /// like the storefront and the exclusion is gone.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("storefront", StorefrontModel)]
    [InlineData("admin", AdminModel)]
    public void Only_one_file_per_front_end_spells_the_rule_out(string project, string allowedFile)
    {
        // The tail of the sentence, which no other copy would have for an unrelated reason.
        var fragment = PasswordPolicy.Requirement[^30..];

        var offenders = EnumerateFrontEndSource(project)
            .Where(file => File.ReadAllText(file.FullName).Contains(fragment, StringComparison.Ordinal))
            .Select(file => Relative(file.FullName))
            .Where(path => !string.Equals(path, allowedFile, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Empty(offenders);

        // And the one file that may spell it out really does, so this test cannot pass by finding
        // nothing anywhere — the vacuous shape this project has shipped more than once.
        Assert.Contains(fragment, ReadRepositoryFile(allowedFile), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every password form reads the constant rather than its own words, and wires it to the input.
    /// </summary>
    /// <remarks>
    /// <b>The <c>aria-describedby</c> half is the point of showing it at all.</b> A sentence sitting
    /// after a field is loose text; a sentence referenced by the field is read as part of it. Checked by
    /// looking for the id, because that is the thing a reviewer would delete while tidying the markup.
    /// </remarks>
    [Theory]
    [InlineData("src/frontend/projects/storefront/src/app/pages/account/register-page.ts", "PASSWORD_RULE", "rg-password-rule")]
    [InlineData("src/frontend/projects/storefront/src/app/pages/account/reset-password-page.ts", "PASSWORD_RULE", "rp-password-rule")]
    [InlineData("src/frontend/projects/admin/src/app/pages/change-password/change-password-page.ts", "STAFF_PASSWORD_RULE", "change-rules")]
    [InlineData("src/frontend/projects/admin/src/app/pages/users/user-form-page.ts", "STAFF_PASSWORD_RULE", "initial-password-rule")]
    public void Every_password_form_shows_the_rule_and_ties_it_to_the_field(string path, string constant, string hintId)
    {
        var source = ReadRepositoryFile(path);

        Assert.Contains(constant, source, StringComparison.Ordinal);
        Assert.Contains($"id=\"{hintId}\"", source, StringComparison.Ordinal);
        Assert.Contains(hintId, source.Replace($"id=\"{hintId}\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>
    /// The minimum length the sentence states is the minimum the policy enforces.
    /// </summary>
    /// <remarks>
    /// <b>The sentence and the number are two fields of the same class</b>, and nothing otherwise stops
    /// one being changed without the other — which is the same defect as the front-end copy, one layer
    /// in. Read out of the sentence rather than written here, so there is no third copy.
    /// </remarks>
    [Fact]
    public void The_sentence_states_the_minimum_the_policy_enforces()
    {
        var digits = new string(
            PasswordPolicy.Requirement.SkipWhile(character => !char.IsAsciiDigit(character))
                .TakeWhile(char.IsAsciiDigit)
                .ToArray());

        Assert.False(
            string.IsNullOrEmpty(digits),
            "PasswordPolicy.Requirement states no number, so a person cannot know the minimum.");

        Assert.Equal(
            PasswordPolicy.MinimumLength,
            int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The deployment check refuses a seeded-admin password exactly when the server would: the number in
    /// <c>deploy/scripts/lib.sh</c> is the policy's.
    /// </summary>
    /// <remarks>
    /// <b>The shell cannot read a C# constant</b>, so <c>check_env_file</c> carries its own copy of the
    /// minimum, used to stop <c>SD_SEED_ADMIN_PASSWORD</c> being too short before the API starts and then
    /// quietly seeds nobody (issue #200). Left unpinned it would be the next place the rule drifts, so it is
    /// read here by name and compared.
    /// </remarks>
    [Fact]
    public void The_deployment_checks_minimum_is_the_policys()
    {
        var match = DeployMinimum().Match(ReadRepositoryFile(DeployLibrary));

        Assert.True(
            match.Success,
            $"{DeployLibrary} no longer defines SD_PASSWORD_MIN_LENGTH, so check-env.sh cannot refuse a seed "
            + "password the API would refuse.");

        Assert.Equal(
            PasswordPolicy.MinimumLength,
            int.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"^SD_PASSWORD_MIN_LENGTH=(?<n>\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex DeployMinimum();

    /// <summary>
    /// The source of one front-end project, specs excluded.
    /// </summary>
    /// <remarks>
    /// Specs are exempt for <c>BrandNameConventionTests</c>' reason: a spec that builds its expectation
    /// out of the same constant the subject reads cannot fail, so the literal is pinned there on purpose.
    /// </remarks>
    private static IEnumerable<FileInfo> EnumerateFrontEndSource(string project)
    {
        var root = Path.Combine(RepositoryRoot(), "src", "frontend", "projects", project);

        return new DirectoryInfo(root)
            .EnumerateFiles("*.ts", SearchOption.AllDirectories)
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Name.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string Relative(string fullPath) =>
        Path.GetRelativePath(RepositoryRoot(), fullPath).Replace('\\', '/');

    /// <summary>
    /// The repository root, found by walking up to the directory that holds <c>src/frontend</c>.
    /// </summary>
    /// <remarks>
    /// The same search <c>BrandNameConventionTests</c> and <c>LayerDependencyTests</c> make, for the same
    /// reason: the test binary's own directory is several levels under <c>tests/</c> and the depth
    /// changes with the configuration.
    /// </remarks>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "frontend")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root (a directory containing src/frontend) above "
            + AppContext.BaseDirectory);
    }
}
