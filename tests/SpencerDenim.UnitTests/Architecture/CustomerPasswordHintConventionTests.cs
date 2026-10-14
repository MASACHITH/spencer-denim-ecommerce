using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// The password rule is stated in one place and copied to exactly one other, and the copy is pinned to
/// the original (W3-01, ACC-01).
/// </summary>
/// <remarks>
/// <b>Why a copy exists at all.</b> <see cref="CustomerPasswordPolicy.Requirement"/> is what the server
/// enforces, and the storefront has to show it <i>before</i> a shopper presses anything — WCAG 2.2
/// <b>3.3.2 Labels or Instructions</b>, and on a password field doubly so, because they cannot see what
/// they typed. There is no endpoint that serves the sentence and inventing one would be a request on
/// every page load for a string that changes once a year. So the front end holds a copy.
/// <para>
/// <b>Why the copy is pinned here rather than trusted.</b> This is #126's defect exactly: a
/// <c>maxlength</c> of 120 against a 100-character column passed the form, the stub and sixteen browser
/// tests, because two places stated one rule and nothing compared them. A form that promises ten
/// characters while the server demands twelve is the same defect in copy — and worse, because the
/// shopper meets the stated rule and is refused anyway.
/// </para>
/// <para>
/// <b>Why it is a C# test reading a TypeScript file.</b> <c>BrandNameConventionTests</c> records the
/// reason at length: this is the only suite in the repository with unrestricted filesystem access that
/// runs on every <c>dotnet test</c>, because the Angular specs are bundled for a jsdom environment with
/// no <c>node:fs</c> in their type set. A convention that held on one side and not the other would be
/// the usual half-rule.
/// </para>
/// </remarks>
public sealed class CustomerPasswordHintConventionTests
{
    /// <summary>The one front-end file allowed to spell the rule out.</summary>
    private const string FrontEndModel =
        "src/frontend/projects/storefront/src/app/core/account/account.model.ts";

    /// <summary>
    /// The front end's copy is byte-identical to the server's constant.
    /// </summary>
    /// <remarks>
    /// <b>Asserted as a substring of the file, not parsed out of it.</b> A parser for a TypeScript string
    /// literal is a second thing to get wrong, and what matters is only that the exact sentence the
    /// server enforces appears in the file the forms read it from. If somebody rewords
    /// <see cref="CustomerPasswordPolicy.Requirement"/> — raises the minimum, drops the symbol rule —
    /// this fails and names the file to change.
    /// </remarks>
    [Fact]
    public void The_storefronts_password_hint_is_the_servers_own_sentence()
    {
        var model = ReadRepositoryFile(FrontEndModel);

        Assert.Contains(
            CustomerPasswordPolicy.Requirement,
            model,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule is written out in exactly one front-end file, and the forms read it from there.
    /// </summary>
    /// <remarks>
    /// <b>One copy, not two.</b> Two forms ask for a password — registration and the reset — and if each
    /// held its own sentence there would be three places to change and two chances to miss one. They
    /// import <c>PASSWORD_RULE</c>; this is what stops the next form from pasting the words instead.
    /// <para>
    /// Keyed on a distinctive fragment rather than the whole sentence, so that rewording the rule does
    /// not make this test pass vacuously by matching nothing: the fragment is taken from the constant
    /// itself, so there is nothing here to keep in step by hand.
    /// </para>
    /// </remarks>
    [Fact]
    public void Only_one_front_end_file_spells_the_rule_out()
    {
        // The tail of the sentence, which no other copy would have for an unrelated reason.
        var fragment = CustomerPasswordPolicy.Requirement[^30..];

        var offenders = EnumerateFrontEndSource()
            .Where(file => File.ReadAllText(file.FullName).Contains(fragment, StringComparison.Ordinal))
            .Select(file => Relative(file.FullName))
            .Where(path => !string.Equals(path, FrontEndModel, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Empty(offenders);

        // And the one file that may spell it out really does, so this test cannot pass by finding
        // nothing anywhere — the vacuous shape this project has shipped more than once.
        Assert.Contains(fragment, ReadRepositoryFile(FrontEndModel), StringComparison.Ordinal);
    }

    /// <summary>
    /// Both password forms read the constant rather than their own words, and both wire it to the input.
    /// </summary>
    /// <remarks>
    /// <b>The <c>aria-describedby</c> half is the point of showing it at all.</b> A sentence sitting
    /// after a field is loose text; a sentence referenced by the field is read as part of it. Checked by
    /// looking for the id, because that is the thing a reviewer would delete while tidying the markup.
    /// </remarks>
    [Theory]
    [InlineData("src/frontend/projects/storefront/src/app/pages/account/register-page.ts", "rg-password-rule")]
    [InlineData("src/frontend/projects/storefront/src/app/pages/account/reset-password-page.ts", "rp-password-rule")]
    public void Both_password_forms_show_the_rule_and_tie_it_to_the_field(string path, string hintId)
    {
        var source = ReadRepositoryFile(path);

        Assert.Contains("PASSWORD_RULE", source, StringComparison.Ordinal);
        Assert.Contains($"id=\"{hintId}\"", source, StringComparison.Ordinal);
        Assert.Contains($"'{hintId}'", source, StringComparison.Ordinal);
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
            CustomerPasswordPolicy.Requirement.SkipWhile(character => !char.IsAsciiDigit(character))
                .TakeWhile(char.IsAsciiDigit)
                .ToArray());

        Assert.False(
            string.IsNullOrEmpty(digits),
            "CustomerPasswordPolicy.Requirement states no number, so a shopper cannot know the minimum.");

        Assert.Equal(
            CustomerPasswordPolicy.MinimumLength,
            int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The storefront's own source, and <b>not</b> the admin application's.
    /// </summary>
    /// <remarks>
    /// <b>The admin app states a password rule too, and it is a different rule.</b>
    /// <c>projects/admin/src/app/pages/change-password</c> describes what
    /// <c>IdentityOptions.Password</c> enforces for <i>staff</i> — Identity's defaults, six characters
    /// with four character classes — which <see cref="CustomerPasswordPolicy"/> deliberately does not
    /// govern (see its remarks: raising a shared minimum belongs to the task that owns that startup
    /// path). Sweeping the admin app in would pin a staff sentence to a customer constant and make the
    /// two rules look like one.
    /// <para>
    /// <b>Found by this test failing on its first run</b>, which is the right way round: the admin's
    /// sentence ends in the same words, so the fragment matched. The exclusion is narrow and named
    /// rather than a loosened fragment, because a loosened one would stop catching a second
    /// <i>storefront</i> copy, which is the thing this is for.
    /// </para>
    /// </remarks>
    private static IEnumerable<FileInfo> EnumerateFrontEndSource()
    {
        var root = Path.Combine(RepositoryRoot(), "src", "frontend", "projects", "storefront");

        return new DirectoryInfo(root)
            .EnumerateFiles("*.ts", SearchOption.AllDirectories)
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))

            // Specs are exempt for BrandNameConventionTests' reason: a spec that builds its expectation
            // out of the same constant the subject reads cannot fail, so the literal is pinned there on
            // purpose.
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
