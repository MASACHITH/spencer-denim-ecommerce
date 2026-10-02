using System.Text;
using SpencerDenim.Infrastructure.Persistence.Seeding;

namespace SpencerDenim.UnitTests.Tools;

/// <summary>
/// <c>tools/kids-stand-in-photos.csv</c> held against the real catalogue.
/// <para>
/// The ten kids products have no photographs, so each borrows the nearest delivered adult garment
/// until the real ones arrive (owner decision, 2026-10-02). That mapping is a list of slugs in a text
/// file, and a list of slugs in a text file rots: a product renamed in <see cref="DemoCatalogueData"/>
/// leaves a row pointing at nothing, and the only symptom is one kids product quietly keeping its
/// placeholder — which is exactly the state the mapping exists to end. The import would not fail,
/// either: <c>ClientAssetNaming</c> refuses an unknown slug, so a stale row stops that one file and
/// nothing says which product lost its picture.
/// </para>
/// <para>
/// So the build checks it. This is also the test that fails the day somebody adds an eleventh kids
/// product and forgets the photograph.
/// </para>
/// </summary>
public sealed class KidsStandInMappingTests
{
    private static readonly string[] Qualities = ["exact", "good", "fair", "poor", "none"];

    [Fact]
    public void Every_kids_product_has_exactly_one_stand_in()
    {
        var kids = DemoCatalogueData.Products
            .Where(product => product.DepartmentSlug == "kids")
            .Select(product => product.Slug)
            .ToList();

        var mapped = Mapping().Select(row => row.KidsSlug).ToList();

        Assert.Equal(kids.Count, mapped.Count);
        Assert.Equal(mapped.Count, mapped.Distinct(StringComparer.Ordinal).Count());

        // Named rather than counted: a message saying "10 != 9" sends the reader to the wrong file.
        Assert.Empty(kids.Except(mapped, StringComparer.Ordinal));
        Assert.Empty(mapped.Except(kids, StringComparer.Ordinal));
    }

    /// <summary>
    /// The donor is a real product that is <b>not</b> a kids product. Not kids, because a kids product
    /// has no photograph to lend — the whole point — so a kids donor is a row that can never produce an
    /// image, and it would fail silently.
    /// </summary>
    [Fact]
    public void Every_donor_is_a_real_product_from_another_department()
    {
        foreach (var row in Mapping())
        {
            var donor = DemoCatalogueData.Products.SingleOrDefault(
                product => string.Equals(product.Slug, row.DonorSlug, StringComparison.Ordinal));

            Assert.NotNull(donor);
            Assert.NotEqual("kids", donor.DepartmentSlug);
        }
    }

    /// <summary>
    /// The names in the file are the catalogue's names. They are there so a person reading the mapping
    /// does not have to translate slugs, and a name that has drifted from the catalogue is worse than
    /// no name at all.
    /// </summary>
    [Fact]
    public void The_product_names_in_the_mapping_are_the_catalogues_own()
    {
        var names = DemoCatalogueData.Products.ToDictionary(
            product => product.Slug,
            product => product.Name,
            StringComparer.Ordinal);

        foreach (var row in Mapping())
        {
            Assert.Equal(names[row.KidsSlug], row.KidsProduct);
            Assert.Equal(names[row.DonorSlug], row.DonorProduct);
        }
    }

    /// <summary>
    /// Every row grades its own match and says why, because a stand-in that nobody has called poor
    /// reads as a photograph somebody chose. <c>none</c> is a legal grade and is the honest answer for
    /// the muslin bib set: nothing in the delivery is close to it.
    /// </summary>
    [Fact]
    public void Every_row_grades_the_match_and_says_why()
    {
        foreach (var row in Mapping())
        {
            Assert.Contains(row.Quality, Qualities);
            Assert.False(string.IsNullOrWhiteSpace(row.Why), $"{row.KidsSlug} has no reason recorded.");

            // Long enough to be a reason rather than a label. The short ones are the ones that turn out
            // to be "closest" with nothing behind it.
            Assert.True(row.Why.Length > 60, $"{row.KidsSlug}: '{row.Why}' is too short to be a reason.");
        }
    }

    /// <summary>
    /// The file says, in its own text, that this is test data and that the real photographs are still
    /// outstanding. It is the first thing anybody opening it reads, and the one sentence that stops the
    /// mapping being mistaken for a delivery.
    /// </summary>
    [Fact]
    public void The_mapping_says_on_its_face_that_it_is_test_data()
    {
        var text = File.ReadAllText(MappingPath(), Encoding.UTF8);

        Assert.Contains("TEST DATA", text, StringComparison.Ordinal);
        Assert.Contains("outstanding", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string MappingPath() => Path.Combine(FindRepositoryRoot(), "tools", "kids-stand-in-photos.csv");

    private static List<StandInRow> Mapping()
    {
        var lines = File.ReadAllLines(MappingPath(), Encoding.UTF8)
            .Where(line => !line.TrimStart().StartsWith('#') && line.Trim().Length > 0)
            .ToList();

        Assert.True(lines.Count > 1, "The mapping has no rows.");

        var header = Split(lines[0]);
        Assert.Equal(["KidsSlug", "KidsProduct", "DonorSlug", "DonorProduct", "Quality", "Why"], header);

        return lines
            .Skip(1)
            .Select(Split)
            .Select(fields =>
            {
                Assert.Equal(header.Count, fields.Count);
                return new StandInRow(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5]);
            })
            .ToList();
    }

    /// <summary>
    /// One CSV line. Quoted fields only, no escaped quotes and no embedded newlines — which is all the
    /// file uses, and asserting it here is cheaper than depending on a CSV library to agree with
    /// PowerShell's <c>ConvertFrom-Csv</c> about a case neither file has.
    /// </summary>
    private static List<string> Split(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        foreach (var character in line)
        {
            switch (character)
            {
                case '"':
                    quoted = !quoted;
                    break;
                case ',' when !quoted:
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        Assert.False(quoted, $"Unclosed quote in the mapping: {line}");
        fields.Add(field.ToString());

        return fields;
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

    private sealed record StandInRow(
        string KidsSlug,
        string KidsProduct,
        string DonorSlug,
        string DonorProduct,
        string Quality,
        string Why);
}
