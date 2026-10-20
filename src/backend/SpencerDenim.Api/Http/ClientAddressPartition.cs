using System.Net;
using System.Net.Sockets;

namespace SpencerDenim.Api.Http;

/// <summary>
/// The rate-limit partition for a visitor's address: an IPv4 address as it is, an IPv6 address by its
/// <b>/64</b> network.
/// <para>
/// <b>Why /64.</b> An IPv6 subscriber is routinely given a whole /64 (and often more), so a per-address limit
/// is no limit at all: every request can come from a fresh address in the same network. The W0-15 tester
/// sent 25 newsletter signups from <c>2001:db8:1:2::1</c> … <c>::19</c> and every one was accepted. The /64 is
/// the smallest block one subscriber can be assumed to control. An IPv4-mapped IPv6 address
/// (<c>::ffff:203.0.113.9</c>) is treated as the IPv4 address it carries, so one visitor is one partition
/// whichever socket family the API happened to accept them on.
/// </para>
/// <para>
/// This is only the throttles' key. What is <i>stored</i> — the newsletter consent record, the contact
/// enquiry, the audit log — keeps the full address.
/// </para>
/// </summary>
internal static class ClientAddressPartition
{
    /// <summary>
    /// The partition for <paramref name="address"/>, or the value unchanged when it is empty or not an IP
    /// address (the throttles map those to their own shared "unknown" partition).
    /// </summary>
    public static string? For(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address, out var parsed))
        {
            return address;
        }

        if (parsed.IsIPv4MappedToIPv6)
        {
            parsed = parsed.MapToIPv4();
        }

        // Loopback, link-local and private (unique-local) peers are left whole: they are infrastructure, not
        // subscribers, and the throttles recognise them by parsing the key as an address (PaymentSlipUploadThrottle
        // gives a shared address its looser bucket), which a "/64" key would defeat.
        if (parsed.AddressFamily != AddressFamily.InterNetworkV6
            || IPAddress.IsLoopback(parsed)
            || parsed.IsIPv6LinkLocal
            || parsed.IsIPv6SiteLocal
            || parsed.IsIPv6UniqueLocal)
        {
            return parsed.ToString();
        }

        var bytes = parsed.GetAddressBytes();
        Array.Clear(bytes, 8, 8);

        return $"{new IPAddress(bytes)}/64";
    }

    /// <summary>
    /// Whether <paramref name="address"/> is the address of a piece of infrastructure rather than a
    /// visitor: loopback, link-local, or a private (RFC 1918 / unique-local) range.
    /// <para>
    /// A request arriving from one of these has come through something whose forwarded headers the API is
    /// not trusting — a reverse proxy, a container network — so every visitor behind it shares the one
    /// address (docs/15 §3). Harmless on a developer's machine; in a deployment it is the signal that
    /// <c>ForwardedHeaders__KnownProxies</c> is unset or does not match the gateway. Text that is not an
    /// IP address at all is not infrastructure: it answers <see langword="false"/>.
    /// </para>
    /// <para>
    /// The same test <c>PaymentSlipUploadThrottle</c>, <c>NewsletterSignupThrottle</c> and
    /// <c>ContactEnquiryThrottle</c> each carry a private copy of. New code calls this one.
    /// </para>
    /// </summary>
    public static bool IsInfrastructure(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address, out var parsed))
        {
            return false;
        }

        if (IPAddress.IsLoopback(parsed))
        {
            return true;
        }

        if (parsed.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7, the IPv6 unique local range, plus a mapped IPv4 address behind it.
            return parsed.IsIPv6LinkLocal
                || parsed.IsIPv6UniqueLocal
                || (parsed.IsIPv4MappedToIPv6 && IsPrivateV4(parsed.MapToIPv4()));
        }

        return IsPrivateV4(parsed);
    }

    /// <summary>RFC 1918 and the link-local range.</summary>
    private static bool IsPrivateV4(IPAddress address)
    {
        var octets = address.GetAddressBytes();

        return octets[0] switch
        {
            10 => true,
            172 => octets[1] >= 16 && octets[1] <= 31,
            192 => octets[1] == 168,
            169 => octets[1] == 254,
            _ => false,
        };
    }
}
