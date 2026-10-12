using SpencerDenim.Api.Http;

namespace SpencerDenim.UnitTests.Api;

/// <summary>
/// W0-15 tester finding: the newsletter limit partitioned IPv6 by full address, so 25 signups from one /64
/// (<c>2001:db8:1:2::1</c> … <c>::19</c>) were all accepted.
/// </summary>
public sealed class ClientAddressPartitionTests
{
    [Theory]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::19", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    public void A_public_ipv6_address_is_keyed_by_its_64(string address, string expected) =>
        Assert.Equal(expected, ClientAddressPartition.For(address));

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    public void Ipv4_and_ipv4_mapped_ipv6_are_one_partition(string address, string expected) =>
        Assert.Equal(expected, ClientAddressPartition.For(address));

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::5")]
    public void Loopback_link_local_and_private_ipv6_are_left_whole(string address) =>
        Assert.Equal(System.Net.IPAddress.Parse(address).ToString(), ClientAddressPartition.For(address));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    public void Anything_else_is_returned_unchanged(string? value) =>
        Assert.Equal(value, ClientAddressPartition.For(value));
}
