using System.Net;
using SpencerDenim.Api.Http;

namespace SpencerDenim.UnitTests.Api;

/// <summary>
/// Parsing of <c>ForwardedHeaders__KnownNetworks</c> / <c>ForwardedHeaders__KnownProxies</c> (W0-15). What the
/// middleware then does with the result is proven end to end in the integration tests
/// (<c>ForwardedHeadersTests</c>); this pins the fail-fast rules, because a typo in an environment variable must
/// stop the API rather than silently trust nobody — or everybody.
/// </summary>
public sealed class TrustedProxyParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ; ")]
    public void Nothing_configured_is_no_networks(string? value)
    {
        Assert.Empty(TrustedProxyParser.ParseNetworks(value));
        Assert.Empty(TrustedProxyParser.ParseAddresses(value));
    }

    [Fact]
    public void A_list_of_cidr_blocks_is_parsed_whatever_the_separator()
    {
        var networks = TrustedProxyParser.ParseNetworks(" 10.251.0.16/28, 10.251.1.16/28;fd00:251::/64 ");

        Assert.Equal(3, networks.Count);
        Assert.True(networks[0].Contains(IPAddress.Parse("10.251.0.18")));
        Assert.False(networks[0].Contains(IPAddress.Parse("10.251.0.40")));
        Assert.True(networks[2].Contains(IPAddress.Parse("fd00:251::5")));
    }

    [Theory]
    [InlineData("10.251.0.16")]          // an address, not a block
    [InlineData("10.251.0.16/33")]
    [InlineData("gateway")]
    [InlineData("10.251.0.16/28,nope")]
    public void A_malformed_network_fails_loudly_and_names_itself(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => TrustedProxyParser.ParseNetworks(value));

        Assert.Contains("ForwardedHeaders:KnownNetworks", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A /0 is "trust the whole internet": the W1-01 audit-log spoofing hole, re-opened.</summary>
    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void A_network_that_trusts_everybody_is_refused(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => TrustedProxyParser.ParseNetworks(value));

        Assert.Contains("every address", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Proxy_addresses_are_parsed()
    {
        var proxies = TrustedProxyParser.ParseAddresses("10.251.0.20, fd00::20");

        Assert.Equal([IPAddress.Parse("10.251.0.20"), IPAddress.Parse("fd00::20")], proxies);
    }

    [Theory]
    [InlineData("10.251.0.0/28")]
    [InlineData("not-an-ip")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void A_malformed_or_unspecified_proxy_fails_loudly(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => TrustedProxyParser.ParseAddresses(value));

        Assert.Contains("ForwardedHeaders:KnownProxies", exception.Message, StringComparison.Ordinal);
    }
}
