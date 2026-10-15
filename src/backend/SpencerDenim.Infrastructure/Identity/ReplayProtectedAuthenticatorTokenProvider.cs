using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Replaces Identity's <see cref="AuthenticatorTokenProvider{TUser}"/> under the same provider name
/// (<see cref="TokenOptions.DefaultAuthenticatorProvider"/>), so every authenticator check — the second sign-in step
/// and confirming enrolment alike — goes through it (W1-10). It checks the same RFC 6238 codes (HMAC-SHA1,
/// 30-second steps, six digits, two steps either side for clock drift), and adds what Identity's provider lacks:
/// a code is accepted only for a time step later than the last one accepted for the account, so a code that has
/// been used — or seen over someone's shoulder — cannot be used again while it is still inside the window.
/// </summary>
/// <remarks>
/// <b>"Accepted once" has to hold for requests that arrive together, not only one after another (#48).</b> The
/// first version read the last accepted step through <see cref="UserManager{TUser}"/> and wrote the new one with
/// <see cref="UserManager{TUser}.SetAuthenticationTokenAsync"/>: two round trips, nothing spanning them, and
/// <c>identity.UserTokens</c> has no concurrency token for EF to check. Every request that read before the first one
/// wrote saw the old step, matched the same code and accepted it. The sequential replay test could never see that.
/// <para>
/// <b>The read now only proposes; a single conditional statement decides.</b> The watermark row is advanced by an
/// <c>UPDATE … WHERE Value = &lt;the value this request read&gt;</c> — a compare-and-swap on the row, under the row's
/// own lock — and a request that changes no row has lost: somebody else accepted a code between its read and its
/// write. When the account has no watermark row yet (its first code ever, or the first after anonymisation), the
/// write is a guarded <c>INSERT</c> instead, and the loser of two inserts gets no row (the guard) or a unique-key
/// violation (the primary key, if the guard is ever beaten): the same answer. This is the shape
/// <c>AdminAlertAcknowledger</c> and <c>NumberSeriesAllocator</c> already use, and it needed no schema change — no
/// rowversion column, so no migration.
/// </para>
/// <para>
/// <b>A loser reads again rather than giving up.</b> Losing the swap says only that the watermark moved, so the
/// request re-reads it and decides afresh: a request holding the same code now finds its step at or before the
/// newest accepted one and is refused exactly as a sequential replay is — <see langword="false"/>, which the caller
/// counts as an ordinary wrong code — while a request holding a genuinely later step still goes through. The loop
/// is bounded; running out of attempts refuses, which is the safe direction.
/// </para>
/// <para>
/// <b>Failing safe under a transient retry.</b> <c>EnableRetryOnFailure</c> re-runs a statement whose
/// acknowledgement was lost. If the first run had in fact committed, the re-run matches no row, the re-read finds
/// this request's own step already stored, and the code is refused once — a user retyping a code, never a code
/// accepted twice.
/// </para>
/// <para>
/// Reads use <c>AsNoTracking</c> and any tracked copy of the row is detached after a write, so a later read through
/// <see cref="UserManager{TUser}"/> in the same scope cannot be handed a stale value from the change tracker. Like
/// every <c>UserTokens</c> row the watermark is not audited (doc 08 §3.8), so going around <c>SaveChanges</c> (and
/// its audit interceptor) loses nothing.
/// </para>
/// </remarks>
public sealed class ReplayProtectedAuthenticatorTokenProvider(AppDbContext dbContext) : IUserTwoFactorTokenProvider<AppUser>
{
    private const int StepSeconds = 30;

    /// <summary>Steps accepted either side of the current one, the same drift allowance as Identity's provider.</summary>
    private const int DriftSteps = 2;

    /// <summary>
    /// Read-decide-swap passes. A lost swap means another request accepted a code in between; the next pass reads
    /// that and, for the same code, refuses. Three covers a loser and a second loser without being able to spin.
    /// </summary>
    private const int Attempts = 3;

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
        ArgumentNullException.ThrowIfNull(user);

        var key = await manager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(key) || !TryParseCode(token, out var code))
        {
            return false;
        }

        var keyBytes = Base32Decode(key);
        var keyFingerprint = Fingerprint(keyBytes);

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var watermark = await ReadWatermarkAsync(user.Id);
            var lastAccepted = LastAcceptedStep(watermark, keyFingerprint);
            var currentStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;

            if (MatchingStep(keyBytes, code, currentStep, lastAccepted) is not { } step)
            {
                return false;
            }

            var advanced = string.Create(CultureInfo.InvariantCulture, $"{step}:{keyFingerprint}");
            if (await TryAdvanceWatermarkAsync(user.Id, watermark, advanced))
            {
                return true;
            }

            // Another request moved the watermark after this one read it. Read it again and decide again: the same
            // code is now at or before the newest accepted step and is refused on the next pass.
        }

        return false;
    }

    /// <summary>
    /// The step inside the drift window whose code is <paramref name="code"/>, or <see langword="null"/>. A step at or
    /// before the last accepted one is never considered, so a used code simply does not match.
    /// </summary>
    private static long? MatchingStep(byte[] keyBytes, int code, long currentStep, long lastAccepted)
    {
        for (var step = currentStep - DriftSteps; step <= currentStep + DriftSteps; step++)
        {
            if (step > lastAccepted && ComputeCode(keyBytes, step) == code)
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>
    /// The stored step is tied to a fingerprint of the key it was accepted with, so a new key (re-enrolment after
    /// turning 2FA off, or any reset) starts afresh instead of refusing its first codes.
    /// </summary>
    private static long LastAcceptedStep(Watermark? watermark, string keyFingerprint)
    {
        var parts = watermark?.Value?.Split(':', 2);
        return parts is { Length: 2 }
            && string.Equals(parts[1], keyFingerprint, StringComparison.Ordinal)
            && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var step)
            ? step
            : long.MinValue;
    }

    /// <summary>
    /// The account's watermark row as the database holds it now, or <see langword="null"/> when there is no row —
    /// a distinction the write needs (update the row it read, or insert the first one). Untracked, so a copy the
    /// change tracker already holds can never stand in for the current value.
    /// </summary>
    private async Task<Watermark?> ReadWatermarkAsync(int userId) =>
        await dbContext.Set<IdentityUserToken<int>>()
            .AsNoTracking()
            .Where(t => t.UserId == userId
                && t.LoginProvider == AdminTokenNames.LoginProvider
                && t.Name == AdminTokenNames.LastAuthenticatorStep)
            .Select(t => new Watermark(t.Value))
            .SingleOrDefaultAsync();

    /// <summary>
    /// Writes <paramref name="advanced"/> only if the row still holds what <paramref name="read"/> saw (or, when
    /// there was no row, only if there still is none). <see langword="false"/> means another request got there first.
    /// </summary>
    private async Task<bool> TryAdvanceWatermarkAsync(int userId, Watermark? read, string advanced)
    {
        // IDENTITY is a reserved word in T-SQL, so the schema name is always bracketed below.
        int written;
        if (read is not null)
        {
            // A row holding NULL is compared as the empty string, as it was read, so the swap is never defeated by
            // NULL's "equal to nothing" semantics (and no NULL parameter has to be typed).
            var expected = read.Value ?? string.Empty;
            written = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE [identity].[UserTokens]
                   SET Value = {advanced}
                 WHERE UserId = {userId}
                   AND LoginProvider = {AdminTokenNames.LoginProvider}
                   AND Name = {AdminTokenNames.LastAuthenticatorStep}
                   AND ISNULL(Value, N'') = {expected};
                """);
        }
        else
        {
            try
            {
                // UPDLOCK + HOLDLOCK hold the (empty) key range from the existence check to the insert, so a
                // concurrent first insert waits and then finds the row instead of failing on the primary key. The
                // catch below is for the case the hint does not cover; either way the loser writes nothing.
                written = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO [identity].[UserTokens] (UserId, LoginProvider, Name, Value)
                    SELECT {userId}, {AdminTokenNames.LoginProvider}, {AdminTokenNames.LastAuthenticatorStep}, {advanced}
                     WHERE NOT EXISTS (
                           SELECT 1
                             FROM [identity].[UserTokens] WITH (UPDLOCK, HOLDLOCK)
                            WHERE UserId = {userId}
                              AND LoginProvider = {AdminTokenNames.LoginProvider}
                              AND Name = {AdminTokenNames.LastAuthenticatorStep});
                    """);
            }
            catch (Exception exception) when (SqlUniqueViolation.Matches(exception))
            {
                written = 0;
            }
        }

        if (written == 1)
        {
            DetachTrackedWatermark(userId);
        }

        return written == 1;
    }

    /// <summary>
    /// The statements above go around the change tracker, so an unchanged copy of the row it may hold from an
    /// earlier read in this scope is now stale; detached, the next read through Identity's store goes to the
    /// database. A copy with pending changes is left alone — that is somebody else's write to make.
    /// </summary>
    private void DetachTrackedWatermark(int userId)
    {
        var tracked = dbContext.ChangeTracker.Entries<IdentityUserToken<int>>()
            .FirstOrDefault(e => e.Entity.UserId == userId
                && e.Entity.LoginProvider == AdminTokenNames.LoginProvider
                && e.Entity.Name == AdminTokenNames.LastAuthenticatorStep);
        if (tracked is { State: EntityState.Unchanged })
        {
            tracked.State = EntityState.Detached;
        }
    }

    /// <summary>The watermark row's value as read; the record itself being present is what says the row exists.</summary>
    private sealed record Watermark(string? Value);

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
