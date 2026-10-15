using System.Reflection;
using System.Text.RegularExpressions;
using SpencerDenim.Application.Analytics.Reporting;
using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// Pins the traffic payloads (W4-03, ADM-06, ADM-20) against their TypeScript copies and the Playwright stub's row
/// builders, in both directions — the #126 lesson: a stub that answered a field the server does not would let a
/// dead screen look healthy.
/// </summary>
public sealed class TrafficShapeTests
{
    private const string Model = "src/frontend/projects/admin/src/app/core/traffic/traffic.model.ts";
    private const string Stub = "src/frontend/e2e/support/admin-reporting-stub.mjs";

    private static readonly Regex Property = new(@"readonly\s+(?<name>\w+)\??:", RegexOptions.CultureInvariant);

    [Theory]
    [InlineData(typeof(TrafficReportDto), "TrafficReport")]
    [InlineData(typeof(TrafficDayRow), "TrafficDay")]
    [InlineData(typeof(TrafficChannelRow), "TrafficChannelRow")]
    [InlineData(typeof(TrafficDeviceRow), "TrafficDeviceRow")]
    [InlineData(typeof(TrafficSourceRow), "TrafficSourceRow")]
    [InlineData(typeof(TrafficPageRow), "TrafficPageRow")]
    [InlineData(typeof(TrafficSourcesDto), "TrafficSources")]
    public void A_payload_has_exactly_the_properties_the_screen_reads(Type dto, string tsInterface) =>
        Assert.Equal(Names(dto), PropertiesOf(Model, tsInterface));

    [Fact]
    public void The_client_names_the_same_channels_and_devices_as_the_server()
    {
        var text = ReadRepositoryFile(Model);

        Assert.Equal(Enum.GetNames<TrafficChannel>().Order(StringComparer.Ordinal), Literals(text, "export type TrafficChannelName"));
        Assert.Equal(Enum.GetNames<DeviceType>().Order(StringComparer.Ordinal), Literals(text, "export type DeviceName"));
    }

    [Theory]
    [InlineData(typeof(TrafficDayRow), "function trafficDayRow(")]
    [InlineData(typeof(TrafficChannelRow), "function trafficChannelRow(")]
    [InlineData(typeof(TrafficDeviceRow), "function trafficDeviceRow(")]
    [InlineData(typeof(TrafficSourceRow), "function trafficSourceRow(")]
    [InlineData(typeof(TrafficPageRow), "function trafficPageRow(")]
    public void The_stub_s_rows_carry_exactly_the_contract_s_fields(Type dto, string builder)
    {
        var body = Block(ReadRepositoryFile(Stub), builder);
        var returned = body[body.LastIndexOf("return {", StringComparison.Ordinal)..];
        var inner = returned[(returned.IndexOf('{', StringComparison.Ordinal) + 1)..returned.LastIndexOf('}')];

        var keys = inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split(':')[0].Trim())
            .Where(key => Regex.IsMatch(key, @"^\w+$"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Names(dto), keys);
    }

    [Fact]
    public void The_stub_s_report_and_widget_bodies_carry_exactly_the_contract_s_fields()
    {
        var text = ReadRepositoryFile(Stub);

        Assert.Equal(Names(typeof(TrafficReportDto)), TopLevelKeys(Block(text, "function trafficBody(")));
        Assert.Equal(Names(typeof(TrafficSourcesDto)), TopLevelKeys(Block(text, "function trafficSourcesBody(")));
    }

    /// <summary>
    /// The keys of the object literal the function returns, at its top level: written one per line at four spaces'
    /// indent (the function body is at two), so a nested array's keys, deeper in, are not counted.
    /// </summary>
    private static List<string> TopLevelKeys(string body) =>
        Regex.Matches(body[body.LastIndexOf("return {", StringComparison.Ordinal)..], @"(?m)^    (\w+)\s*[:,]")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();


    private static List<string> Literals(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find `{marker}`.");

        return Regex.Matches(text[start..text.IndexOf(';', start)], "'(\\w+)'")
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> Names(Type dto) =>
        dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => char.ToLowerInvariant(property.Name[0]) + property.Name[1..])
            .Order(StringComparer.Ordinal)
            .ToList();

    private static List<string> PropertiesOf(string file, string interfaceName) =>
        Property.Matches(Block(ReadRepositoryFile(file), $"interface {interfaceName} "))
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string Block(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find `{marker}`.");

        var open = text.IndexOf('{', start);
        var depth = 0;

        for (var index = open; index < text.Length; index++)
        {
            depth += text[index] switch { '{' => 1, '}' => -1, _ => 0 };

            if (depth == 0)
            {
                return text[(open + 1)..index];
            }
        }

        throw new InvalidOperationException($"`{marker}` has no closing brace.");
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
