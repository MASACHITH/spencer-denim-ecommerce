using System.Text.RegularExpressions;
using SpencerDenim.Api.Http;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// The order-placement limit's three settings reach the deployed API, with the code's own defaults (issue #107).
/// <para>
/// <b>Why this file exists.</b> The API container in <c>deploy/app/compose.yml</c> has an explicit
/// <c>environment:</c> list and no <c>env_file</c>, so a variable that is not listed there never reaches it, whatever
/// <c>config/app.env</c> says. #107's own documentation told an operator to tune the limit with
/// <c>OrderPlacementThrottle__PermitsPerWindow</c> on a campaign day, and nothing carried that to the container:
/// found by #107's tester. The mapping is therefore pinned, and so are its default figures, which are a second copy
/// of the ones in <see cref="OrderPlacementThrottleOptions"/> and would otherwise drift without a sound.
/// </para>
/// <para>It needs no database and no container: it is a file read and a regular expression.</para>
/// </summary>
public sealed class OrderPlacementThrottleDeploymentTests
{
    private static readonly OrderPlacementThrottleOptions Defaults = new();

    /// <summary>The setting, the environment variable an operator sets, and the code's default.</summary>
    public static TheoryData<string, string, int> Settings => new()
    {
        { nameof(OrderPlacementThrottleOptions.PermitsPerWindow), "SD_ORDER_THROTTLE_PERMITS", Defaults.PermitsPerWindow },
        { nameof(OrderPlacementThrottleOptions.SharedAddressPermitsPerWindow), "SD_ORDER_THROTTLE_SHARED_PERMITS", Defaults.SharedAddressPermitsPerWindow },
        { nameof(OrderPlacementThrottleOptions.WindowSeconds), "SD_ORDER_THROTTLE_WINDOW_SECONDS", Defaults.WindowSeconds },
    };

    [Theory]
    [MemberData(nameof(Settings))]
    public void Compose_maps_the_operators_variable_onto_the_setting_with_the_codes_default(
        string setting, string variable, int codeDefault)
    {
        var compose = ReadRepositoryFile("deploy/app/compose.yml");

        // `      OrderPlacementThrottle__PermitsPerWindow: ${SD_ORDER_THROTTLE_PERMITS:-20}`, outside a comment.
        var line = new Regex(
            @"^\s*" + Regex.Escape($"{OrderPlacementThrottleOptions.SectionName}__{setting}") + @":\s*\$\{(?<variable>[A-Z_]+):-(?<default>[^}]*)\}\s*$",
            RegexOptions.Multiline);

        var match = line.Match(compose);

        Assert.True(match.Success, $"compose.yml does not pass {OrderPlacementThrottleOptions.SectionName}__{setting} to the API container.");
        Assert.Equal(variable, match.Groups["variable"].Value);
        Assert.Equal(codeDefault.ToString(System.Globalization.CultureInfo.InvariantCulture), match.Groups["default"].Value);
    }

    [Fact]
    public void The_env_template_offers_every_variable_so_an_operator_can_find_it()
    {
        var template = ReadRepositoryFile("deploy/app/.env.example");

        foreach (var row in Settings)
        {
            var (_, variable, codeDefault) = row.Data;

            Assert.Contains(
                $"# {variable}={codeDefault.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                template,
                StringComparison.Ordinal);
        }
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SpencerDenim.sln")))
            {
                var path = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

                Assert.True(File.Exists(path), $"{relativePath} was not found at {path}.");

                return File.ReadAllText(path);
            }
        }

        throw new DirectoryNotFoundException("Could not locate SpencerDenim.sln above the test output directory.");
    }
}
