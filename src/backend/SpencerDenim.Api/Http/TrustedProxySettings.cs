using System.Net;
using System.Net.Sockets;

namespace SpencerDenim.Api.Http;

/// <summary>
/// Which peers may tell the API who the real client is (<c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c>),
/// bound from configuration section <c>ForwardedHeaders</c>:
/// <list type="bullet">
///   <item><c>ForwardedHeaders__KnownNetworks</c>: comma-separated CIDR blocks, e.g. <c>10.251.0.16/28</c>.</item>
///   <item><c>ForwardedHeaders__KnownProxies</c>: comma-separated single addresses.</item>
/// </list>
/// <para>
/// In the Docker deployment (W0-15, docs/15) this is <c>KnownProxies</c> = the web gateway container's fixed
/// address on the <c>edge</c> network. The storefront shares that network, so a single address is trusted rather
/// than the subnet.
/// </para>
/// <para>
/// When neither is set, the API trusts nobody's <c>X-Forwarded-For</c> at all and accepts
/// <c>X-Forwarded-Proto</c> from loopback only — the state W1-01 round 3 left it in, so a developer machine,
/// CI and every test keep the behaviour that stopped a forged address reaching <c>ops.AuditLogs</c>.
/// </para>
/// </summary>
internal sealed class TrustedProxySettings
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Comma-separated CIDR blocks whose peers are trusted proxies.</summary>
    public string? KnownNetworks { get; set; }

    /// <summary>Comma-separated addresses of individual trusted proxies.</summary>
    public string? KnownProxies { get; set; }
}

/// <summary>
/// Parses <see cref="TrustedProxySettings"/>. Kept free of <c>Microsoft.AspNetCore.HttpOverrides</c> types so it
/// can be unit tested from the plain-SDK test project (TASKS W1-11 note).
/// </summary>
internal static class TrustedProxyParser
{
    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    /// <summary>
    /// Parses a comma-separated list of CIDR blocks. Throws <see cref="InvalidOperationException"/> naming the bad
    /// entry: a typo here must stop the API from starting rather than silently trust nobody (or everybody).
    /// </summary>
    public static IReadOnlyList<IPNetwork> ParseNetworks(string? value)
    {
        var networks = new List<IPNetwork>();

        foreach (var entry in Split(value))
        {
            if (!IPNetwork.TryParse(entry, out var network))
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownNetworks entry '{entry}' is not a CIDR block (expected e.g. 10.251.0.16/28).");
            }

            if (network.PrefixLength == 0)
            {
                // 0.0.0.0/0 or ::/0 would trust every peer on the internet, i.e. re-open the W1-01 audit-log
                // spoofing hole with extra steps.
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownNetworks entry '{entry}' trusts every address. List the gateway's network instead.");
            }

            networks.Add(network);
        }

        return networks;
    }

    /// <summary>Parses a comma-separated list of single addresses, with the same fail-fast rule.</summary>
    public static IReadOnlyList<IPAddress> ParseAddresses(string? value)
    {
        var addresses = new List<IPAddress>();

        foreach (var entry in Split(value))
        {
            if (!IPAddress.TryParse(entry, out var address)
                || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownProxies entry '{entry}' is not an IP address.");
            }

            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownProxies entry '{entry}' is the unspecified address, which no proxy has.");
            }

            addresses.Add(address);
        }

        return addresses;
    }

    private static string[] Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
