using System.Text;

namespace SpencerDenim.UnitTests.Tools;

/// <summary>One row of <c>tools/kids-stand-in-photos.csv</c>.</summary>
internal sealed record StandInRow(
    string KidsSlug,
    string KidsProduct,
    string DonorSlug,
    string DonorProduct,
    string Quality,
    string Why);

/// <summary>
/// Reads <c>tools/kids-stand-in-photos.csv</c>, the authoritative kids stand-in mapping.
/// <para>
/// Shared by the tests that check the mapping against the catalogue and the tests that drive the
/// script, because both have to agree with <c>ConvertFrom-Csv</c> about the same file and a second
/// parser is a second answer.
/// </para>
/// </summary>
internal static class KidsStandInMappingFile
{
    public static string Path() =>
        System.IO.Path.Combine(FindRepositoryRoot(), "tools", "kids-stand-in-photos.csv");

    public static string ScriptPath() =>
        System.IO.Path.Combine(FindRepositoryRoot(), "tools", "kids-stand-in-photos.ps1");

    public static string Text() => File.ReadAllText(Path(), Encoding.UTF8);

    public static List<StandInRow> Rows()
    {
        var lines = File.ReadAllLines(Path(), Encoding.UTF8)
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

    public static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "SpencerDenim.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate SpencerDenim.sln above the test output directory.");
    }
}
