using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Replaces Identity's <see cref="AuthenticatorTokenProvider{TUser}"/> under the same provider name
/// (<see cref="TokenOptions.DefaultAuthenticatorProvider"/>), so every authenticator check — the second sign-in step
/// and confirming enrolment alike — goes through it (W1-10). It checks the same RFC 6238 codes (HMAC-SHA1,
/// 30-second steps, six digits, two steps either side for clock drift), and adds what Identity's provider lacks:
/// a code is accepted only for a time step later than the last one accepted for the account, so a code that has
/// been used — or seen over someone's shoulder — cannot be used again while it is still inside the window.
/// </summary>
public sealed class ReplayProtectedAuthenticatorTokenProvider : IUserTwoFactorTokenProvider<AppUser>
{
    private const int StepSeconds = 30;

    /// <summary>Steps accepted either side of the current one, the same drift allowance as Identity's provider.</summary>
    private const int DriftSteps = 2;

    public Task<string> GenerateAsync(string purpose, UserManager<AppUser> manager, AppUser user) =>
        Task.FromResult(string.Empty); // The authenticator app generates the codes, never the server.

    public async Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<AppUser> manager, AppUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return !string.IsNullOrWhiteSpace(await manager.GetAuthenticatorKeyAsync(user));
    }

    public async Task<bool> ValidateAsync(string purpose, string token, UserManager<AppUser> manager, AppUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);

        var key = await manager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(key) || !TryParseCode(token, out var code))
        {
            return false;
        }

        var keyBytes = Base32Decode(key);
        var keyFingerprint = Fingerprint(keyBytes);
        var lastAccepted = await ReadLastAcceptedStepAsync(manager, user, keyFingerprint);
        var currentStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;

        for (var step = currentStep - DriftSteps; step <= currentStep + DriftSteps; step++)
        {
            // A step at or before the last accepted one is never considered, so a used code simply does not match.
            if (step <= lastAccepted || ComputeCode(keyBytes, step) != code)
            {
                continue;
            }

            var result = await manager.SetAuthenticationTokenAsync(
                user,
                AdminTokenNames.LoginProvider,
                AdminTokenNames.LastAuthenticatorStep,
                string.Create(CultureInfo.InvariantCulture, $"{step}:{keyFingerprint}"));
            return result.Succeeded;
        }

        return false;
    }

    /// <summary>
    /// The stored step is tied to a fingerprint of the key it was accepted with, so a new key (re-enrolment after
    /// turning 2FA off, or any reset) starts afresh instead of refusing its first codes.
    /// </summary>
    private static async Task<long> ReadLastAcceptedStepAsync(UserManager<AppUser> manager, AppUser user, string keyFingerprint)
    {
        var stored = await manager.GetAuthenticationTokenAsync(user, AdminTokenNames.LoginProvider, AdminTokenNames.LastAuthenticatorStep);
        var parts = stored?.Split(':', 2);
        return parts is { Length: 2 }
            && string.Equals(parts[1], keyFingerprint, StringComparison.Ordinal)
            && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var step)
            ? step
            : long.MinValue;
    }

    private static bool TryParseCode(string token, out int code)
    {
        code = 0;
        return token is { Length: 6 }
            && token.All(char.IsAsciiDigit)
            && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out code);
    }

    private static int ComputeCode(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        // RFC 6238 and every authenticator app use HMAC-SHA1 here; it is the standard, not a choice.
#pragma warning disable CA5350
        var hash = HMACSHA1.HashData(key, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return binary % 1_000_000;
    }

    private static string Fingerprint(byte[] key) => Convert.ToHexString(SHA256.HashData(key).AsSpan(0, 8));

    /// <summary>RFC 4648 base32, the format Identity stores the authenticator key in.</summary>
    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in input.ToUpperInvariant())
        {
            if (c is '=' or ' ')
            {
                continue;
            }

            var value = alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException("The authenticator key is not valid base32.");
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

/// <summary>The single-use id of a pending 2FA sign-in (W1-10): random, handed out once, stored only as a hash.</summary>
internal static class PendingSignInIds
{
    public static string NewId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    public static bool HashesMatch(string expectedHash, string id) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedHash), Encoding.ASCII.GetBytes(Hash(id)));
}
