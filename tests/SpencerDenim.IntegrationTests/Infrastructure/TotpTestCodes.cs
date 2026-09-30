using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Computes the codes an authenticator app would show for a key (RFC 6238: HMAC-SHA1, 30-second steps, 6 digits),
/// the same algorithm ASP.NET Core Identity's authenticator token provider checks against. Identity's own
/// implementation is internal, so tests carry this small copy instead of reaching into it.
/// </summary>
internal static class TotpTestCodes
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Current(string base32Key) => At(base32Key, DateTimeOffset.UtcNow);

    /// <summary>
    /// The code for a time step <paramref name="stepsAhead"/> steps from now: still inside the server's drift window,
    /// and later than a step already accepted. Each authenticator step is accepted once only, so a test that signs
    /// in again after enrolling (which used the current step) needs a later one, like a real user a few seconds on.
    /// </summary>
    public static string Next(string base32Key, int stepsAhead = 1) => At(base32Key, DateTimeOffset.UtcNow.AddSeconds(30 * stepsAhead));

    public static string At(string base32Key, DateTimeOffset instant)
    {
        var step = instant.ToUnixTimeSeconds() / 30;
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        // RFC 6238 and every authenticator app use HMAC-SHA1; this is the algorithm under test, not a choice.
#pragma warning disable CA5350
        var hash = HMACSHA1.HashData(FromBase32(base32Key), counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>A six-digit code that is certainly not valid for this key anywhere near now.</summary>
    public static string Wrong(string base32Key)
    {
        var now = DateTimeOffset.UtcNow;
        var valid = Enumerable.Range(-3, 7).Select(i => At(base32Key, now.AddSeconds(30 * i))).ToHashSet(StringComparer.Ordinal);
        for (var candidate = 0; ; candidate++)
        {
            var code = candidate.ToString("D6", CultureInfo.InvariantCulture);
            if (!valid.Contains(code))
            {
                return code;
            }
        }
    }

    /// <summary>The <c>secret</c> parameter of an <c>otpauth://</c> URI.</summary>
    public static string SecretFromUri(string authenticatorUri)
    {
        var query = new Uri(authenticatorUri).Query.TrimStart('?');
        var secret = query.Split('&').Select(p => p.Split('=', 2)).Single(p => p[0] == "secret")[1];
        return Uri.UnescapeDataString(secret);
    }

    private static byte[] FromBase32(string input)
    {
        var clean = input.Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Base32Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException($"'{c}' is not a base32 character.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return [.. output];
    }
}
