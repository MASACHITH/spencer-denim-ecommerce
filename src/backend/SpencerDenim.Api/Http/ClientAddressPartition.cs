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
}
