using System.Text.RegularExpressions;
using SpencerDenim.Application.Privacy;

namespace SpencerDenim.UnitTests.Privacy;

/// <summary>
/// Issue <b>#201</b>, point 5: the gateway's access log and the API's request log follow one rule, and this keeps
/// them from drifting apart. It reads <c>deploy/gateway/nginx.conf</c> (#179), takes the <c>$sd_log_path</c> map
/// and checks that its credential routes are <i>exactly</i> <see cref="RequestPathRedaction.CredentialRoutes"/> (same
/// patterns, same output, same order) and that its generic rule uses the same numbers: a segment of at most 100
/// characters, a run of seven digits is a number, a word of sixteen is a token, and seven segments are kept.
/// <para>
/// The behavioural side of the same promise is <c>tests/Shared/log-path-vectors.json</c>, which every row of was
/// run through the map itself when it was written. This test is the structural half: a route added to the map and
/// not to the code, or the reverse, fails here with the line that differs.
/// </para>
/// <para>
/// <b>It skips while the gateway has no such map</b> (the gateway change, #179, is its own pull request) and runs
/// by itself the day it has.
/// </para>
/// </summary>
public sealed class GatewayLogMapParityTests
{
    private const string GatewayConfig = "deploy/gateway/nginx.conf";

    [Fact]
    public void The_gateway_names_the_same_credential_routes_as_the_api()
    {
        var map = ReadMap();

        // `"~*^/api/orders(?:[;/]|$)"   "/api/orders/*";`  — the case-insensitive rules, in file order.
        var named = Regex.Matches(map, "^\\s*\"~\\*(?<pattern>[^\"]+)\"\\s+\"(?<output>[^\"]+)\";", RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(match => (match.Groups["pattern"].Value, Output: match.Groups["output"].Value.Replace("$sd_a", "${sd_a}", StringComparison.Ordinal)))
            .ToList();

        var code = RequestPathRedaction.CredentialRoutes.Select(route => (route.Pattern, Output: route.Redacted)).ToList();

        Assert.True(
            code.SequenceEqual(named),
            "The gateway's $sd_log_path map and RequestPathRedaction.CredentialRoutes disagree." + Environment.NewLine
            + "gateway: " + string.Join(" | ", named.Select(rule => rule.Item1 + " => " + rule.Output)) + Environment.NewLine
            + "api:     " + string.Join(" | ", code.Select(rule => rule.Pattern + " => " + rule.Output)));
    }

    [Fact]
    public void The_gateways_generic_rule_uses_the_same_numbers_as_the_api()
    {
        var map = ReadMap();

        // The first case-sensitive rule is the all-slugs one (a path with nothing to star).
        var generic = Regex.Match(map, "^\\s*\"~\\^\\(\\?<sd_p>(?<body>[^\"]+)\"", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.True(generic.Success, "The gateway map has no case-sensitive all-slugs rule.");
        var body = generic.Groups["body"].Value;

        Assert.Contains("(?=[^/]{1,100}(?:/|$))", body, StringComparison.Ordinal);                  // 100 characters at most
        Assert.Contains("(?![^/]*(?:[0-9]-?){6}[0-9])", body, StringComparison.Ordinal);            // seven digits are a number
        Assert.Contains("(?![^/]*[a-z0-9]{" + RequestPathRedaction.LongWordLength + "})", body, StringComparison.Ordinal);
        Assert.Contains("[a-z0-9]+(?:-[a-z0-9]+)*", body, StringComparison.Ordinal);                // the slug
        Assert.Contains("[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}", body, StringComparison.Ordinal);
        Assert.Contains("){0," + RequestPathRedaction.MaxSegments + "})", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gateway_tries_the_credential_routes_before_the_generic_rule()
    {
        var map = ReadMap();

        var lastNamed = map.LastIndexOf("\"~*^/api/admin/orders/", StringComparison.Ordinal);
        var firstGeneric = map.IndexOf("\"~^(?<sd_p>", StringComparison.Ordinal);

        Assert.True(lastNamed >= 0 && firstGeneric > lastNamed, "A rule that stars a token has to come first, or the generic rule decides.");
    }

    private static string ReadMap()
    {
        var path = Path.Combine(FindRepositoryRoot(), GatewayConfig);
        Assert.SkipUnless(File.Exists(path), $"{GatewayConfig} is not in this branch.");

        var text = File.ReadAllText(path);
        var start = text.IndexOf("map $uri $sd_log_path {", StringComparison.Ordinal);
        Assert.SkipUnless(
            start >= 0,
            $"{GatewayConfig} has no `map $uri $sd_log_path` block yet (the gateway logging change, #179), so there is no rule to compare. The parity check runs when it has.");

        var end = text.IndexOf("\n    }", start, StringComparison.Ordinal);

        return text[start..end];
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
}
