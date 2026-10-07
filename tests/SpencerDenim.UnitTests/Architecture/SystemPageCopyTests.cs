using System.Text.RegularExpressions;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// <b>Nothing in this repository writes the copy of the six system pages</b>, and this is the test that
/// goes red when something does (GLB-10, doc 11 P13).
/// <para>
/// <b>Why it reads source rather than a database.</b> The guarantee already has an integration test —
/// <c>ContentPageEndpointTests.The_system_pages_are_seeded_with_no_copy_and_only_contact_on_the_shop</c>,
/// which now deletes the rows and runs the real seeder. But the W2-11 tester showed how little that was
/// worth on its own: it changed the seeder to write <c>&lt;p&gt;We offer a 14-day returns policy on all
/// items.&lt;/p&gt;</c> into all six pages and <b>the entire unit suite stayed green</b>, 1,643/1,643,
/// because the only assertion on a seeded body lived in a suite that needs SQL Server. A developer
/// running the fast suite — which is what a developer runs — got no signal at all about the one
/// constraint this task exists to protect.
/// </para>
/// <para>
/// So this one needs no database, no browser and no network, and it fails in under a second. A returns
/// policy, a privacy policy and terms of business are the client's own words; Privacy and Terms need a
/// PDPA review before they can be published at all. Inventing them would put words the client never
/// agreed to on the client's own shop, under the client's own company name, where a customer would rely
/// on them.
/// </para>
/// <para>
/// <b>What it allows.</b> The <i>titles</i> are seeded, because a title is a label rather than copy and
/// a page with no heading cannot be found on the admin list. So is the slug. What may not appear is a
/// body: the seeder may assign <c>string.Empty</c> and nothing else.
/// </para>
/// </summary>
public sealed class SystemPageCopyTests
{
    private const string SeederFile =
        "src/backend/SpencerDenim.Infrastructure/Persistence/Seeding/HomeContentSeeder.cs";

    private const string KeysFile = "src/backend/SpencerDenim.Domain/Content/ContentPage.cs";

    /// <summary>
    /// Every <c>BodyHtml = …</c> assignment anywhere in the seeder, with whatever it assigns.
    /// </summary>
    private static readonly Regex BodyAssignment = new(
        @"BodyHtml\s*=\s*(?<value>[^,;\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The seeder assigns an empty body to a system page, and assigns nothing else to a body anywhere.
    /// <para>
    /// Asserted as "every assignment is <c>string.Empty</c>" rather than "no assignment contains a
    /// <c>&lt;p&gt;</c>", because the second is a block-list and would miss plain text, a constant held
    /// elsewhere, a resource string, or a verbatim string. An allow-list of one permitted right-hand side
    /// is the only form of this check that cannot be worked around by accident.
    /// </para>
    /// </summary>
    [Fact]
    public void The_seeder_gives_a_system_page_an_empty_body_and_nothing_else()
    {
        var source = ReadRepositoryFile(SeederFile);

        var assignments = BodyAssignment.Matches(source)
            .Select(match => match.Groups["value"].Value.Trim())
            .ToList();

        Assert.NotEmpty(assignments);

        Assert.All(
            assignments,
            value => Assert.True(
                value is "string.Empty" or "\"\"",
                $"{SeederFile} assigns a content-page body: BodyHtml = {value}. The six system pages ship "
                + "with empty bodies because the copy is the client's — a returns policy, a privacy policy "
                + "and terms of business carry legal weight, and Privacy and Terms need a PDPA review. "
                + "Seed the title, never the body."));
    }

    /// <summary>
    /// And the seed definitions carry no body to assign. <see cref="ContentPageKeys.Seeds"/> is a system
    /// key, a slug, a title and a boolean; a field holding copy is the other way this could arrive, and
    /// it would read as data rather than as a decision.
    /// <para>
    /// The property list is asserted whole rather than "does not contain Body", because the name of the
    /// field is the part nobody can predict — <c>Body</c>, <c>Html</c>, <c>DefaultCopy</c>,
    /// <c>Placeholder</c>. What can be predicted is that **a boolean cannot carry a returns policy**, so
    /// the list is the check and <c>PublishedOnInstall</c> is on it by name.
    /// </para>
    /// </summary>
    [Fact]
    public void A_seed_definition_has_nowhere_to_put_copy()
    {
        var properties = typeof(ContentPageKeys).Assembly
            .GetType("SpencerDenim.Domain.Content.ContentPageSeed")
            ?.GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotNull(properties);
        Assert.Equal(["PublishedOnInstall", "Slug", "SystemKey", "Title"], properties);

        // Named, because the point is the *type*: nothing here can hold a sentence.
        Assert.All(
            typeof(ContentPageKeys).Assembly
                .GetType("SpencerDenim.Domain.Content.ContentPageSeed")!
                .GetProperties()
                .Where(property => property.Name == "PublishedOnInstall"),
            property => Assert.Equal(typeof(bool), property.PropertyType));
    }

    /// <summary>
    /// <b>Contact ships on the shop; the other five do not.</b> The owner's decision in W2-11, as a test,
    /// because it is a decision rather than a default and the next reader needs to see which way round it
    /// goes.
    /// <para>
    /// The reasoning, so this test is not just a restatement of the data: Contact's content is the enquiry
    /// form plus the <c>Contact.*</c> settings, so nothing about it is wrong for want of copy — while an
    /// unpublished Contact page would leave a brand-new shop with <b>no contact route at all</b>, now
    /// that the WhatsApp float correctly hides itself until a number is set. The other five are the
    /// client's words, two of them needing a PDPA review, so publishing them empty would put a blank
    /// returns policy on a live shop.
    /// </para>
    /// <para>
    /// Asserted as "exactly one, and it is Contact" rather than per page, so adding a seventh seed that is
    /// published by default fails here and has to argue for itself.
    /// </para>
    /// </summary>
    [Fact]
    public void Contact_is_the_only_page_that_ships_on_the_shop()
    {
        var published = ContentPageKeys.Seeds
            .Where(seed => seed.PublishedOnInstall)
            .Select(seed => seed.SystemKey)
            .ToList();

        Assert.Equal([ContentPageKeys.Contact], published);
    }

    /// <summary>
    /// The titles are the one seeded string a visitor sees, so they are checked for the <i>other</i>
    /// W2-11 constraint: the shop a customer sees is Colombo Wardrobe, and a seeded title must not read
    /// as a sentence of policy either.
    /// </summary>
    [Fact]
    public void A_seeded_title_is_a_label_and_not_a_sentence()
    {
        Assert.Equal(6, ContentPageKeys.Seeds.Count);

        Assert.All(
            ContentPageKeys.Seeds,
            seed =>
            {
                Assert.False(string.IsNullOrWhiteSpace(seed.Title));

                // Six words is twice the longest of them ("Returns and exchanges"); a full stop or any
                // length past that is prose, and prose in a title is copy by another route.
                Assert.DoesNotContain(".", seed.Title, StringComparison.Ordinal);
                Assert.True(
                    seed.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 6,
                    $"The seeded title \"{seed.Title}\" reads as a sentence rather than a label.");
            });
    }

    /// <summary>
    /// A last door, and the cheapest one to leave open: a seed <i>file</i> that holds policy prose in a
    /// comment or a constant, waiting for somebody to wire it up. The two files that define the system
    /// pages must not contain the phrases a drafted policy would.
    /// <para>
    /// This one <i>is</i> a block-list, and is honest about being weak: it catches the careless case, not
    /// a determined one. It is here because the careless case is the realistic one — an agent asked to
    /// "fill in the pages" writes exactly these phrases — and because it costs nothing.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("14-day")]
    [InlineData("14 day")]
    [InlineData("30-day")]
    [InlineData("days of receipt")]
    [InlineData("we collect")]
    [InlineData("personal data we")]
    [InlineData("these terms")]
    [InlineData("governed by the laws")]
    [InlineData("refund")]
    public void The_files_that_define_the_system_pages_hold_no_policy_prose(string phrase)
    {
        foreach (var file in new[] { SeederFile, KeysFile })
        {
            Assert.DoesNotContain(
                phrase,
                ReadRepositoryFile(file),
                StringComparison.OrdinalIgnoreCase);
        }
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
