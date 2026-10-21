using System.Reflection;
using System.Text.RegularExpressions;
using SpencerDenim.Api.Endpoints.Analytics;
using SpencerDenim.Application.Analytics.Search;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// Pins the popular-searches payloads (#78) against their hand-written TypeScript copies, in both directions — the
/// admin screen's and the storefront's — so a renamed field cannot leave a screen reading nothing.
/// </summary>
public sealed class PopularSearchShapeTests
{
    private const string AdminModel = "src/frontend/projects/admin/src/app/core/content/admin-popular-searches-api.ts";

    private static readonly Regex Property = new(@"readonly\s+(?<name>\w+)\??:", RegexOptions.CultureInvariant);

    [Theory]
    [InlineData(typeof(PopularSearchRow), "AdminPopularSearch")]
    [InlineData(typeof(HiddenSearchTermRow), "AdminHiddenSearchTerm")]
    [InlineData(typeof(AdminPopularSearchesDto), "AdminPopularSearches")]
    [InlineData(typeof(SearchTermChangedDto), "SearchTermChanged")]
    public void An_admin_payload_has_exactly_the_properties_the_screen_reads(Type dto, string tsInterface) =>
        Assert.Equal(Names(dto), PropertiesOf(AdminModel, tsInterface));

    [Fact]
    public void The_storefront_reads_the_public_list_by_the_name_the_api_sends()
    {
        Assert.Equal(["terms"], Names(typeof(PopularSearchesDto)));
        Assert.Contains(
            "as { terms?: unknown }",
            ReadRepositoryFile("src/frontend/projects/storefront/src/app/core/search/popular-searches.ts"),
            StringComparison.Ordinal);
    }

    private static List<string> Names(Type dto) =>
        dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => char.ToLowerInvariant(property.Name[0]) + property.Name[1..])
            .Order(StringComparer.Ordinal)
            .ToList();

    private static List<string> PropertiesOf(string file, string interfaceName)
    {
        var text = ReadRepositoryFile(file);
        var start = text.IndexOf($"interface {interfaceName} ", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find interface {interfaceName}.");
        var open = text.IndexOf('{', start);
        var close = text.IndexOf('}', open);

        return Property.Matches(text[(open + 1)..close])
            .Select(match => match.Groups["name"].Value)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new FileNotFoundException(relativePath);
    }
}
