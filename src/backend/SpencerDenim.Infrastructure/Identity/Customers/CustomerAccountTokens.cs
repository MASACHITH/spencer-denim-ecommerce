using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SpencerDenim.Infrastructure.Identity.Customers;

/// <summary>
/// The one-time links a customer account needs: a password reset and an email confirmation, minted into
/// <c>identity.UserTokens</c> and redeemable exactly once (doc 08 §3.1; the brief's "a reset token is a
/// credential — it belongs in <c>UserTokens</c>, it must be single-use and time-boxed").
/// </summary>
/// <remarks>
/// <b>Why not Identity's own token providers.</b> <c>AddDefaultTokenProviders</c> gives
/// <c>GeneratePasswordResetTokenAsync</c> and <c>GenerateEmailConfirmationTokenAsync</c>, and both are
/// <i>stateless</i>: a data-protected blob carrying a timestamp and the user's security stamp. Two
/// consequences rule them out here.
/// <list type="number">
/// <item><description>
/// <b>Neither is single-use.</b> A reset token is spent only incidentally — the reset bumps the stamp, so
/// the old token stops verifying — but a confirmation token is not, because confirming an address does
/// not rotate the stamp. Identity's confirmation link therefore stays live for its whole lifetime and can
/// be replayed. Replaying a confirmation is harmless today; it is live state in an inbox that nobody can
/// revoke, which is not what "single-use" means.
/// </description></item>
/// <item><description>
/// <b>Nothing can revoke either one.</b> A customer who presses "send me a new link" twice has two live
/// links, and a customer who suspects their mailbox was read has no way to invalidate the one that was
/// sent. Minting into a table means the <i>row</i> is the token: a new mint overwrites the old one, a
/// redemption deletes it, and both are one statement.
/// </description></item>
/// </list>
/// <para>
/// <b>Shape: a selector and a verifier in one opaque string.</b> The link carries
/// <c>{userId base36}.{32 random bytes, base64url}</c>. The prefix is only a lookup key — the
/// <c>UserTokens</c> primary key is <c>(UserId, LoginProvider, Name)</c>, so a redemption is one keyed
/// read and never a scan of a table whose other rows are staff authenticator keys. The suffix is the
/// credential, and only its <b>SHA-256 hash</b> is stored, so a reader of the database cannot mint a
/// working link from a stored row. Comparison is <see cref="CryptographicOperations.FixedTimeEquals"/>.
/// </para>
/// <para>
/// <b>Why the user id travels in the link at all.</b> The alternative is a value with no selector, which
/// forces the lookup to scan <c>UserTokens</c> on an unindexed column. The id is not a secret — it is the
/// recipient's own — and it is not an address: doc 02's GLB-11 friendly-URL rule is about pages a crawler
/// and a shopper read, and this is a query parameter on a link that names no page content. It does leak
/// an upper bound on how many accounts the shop has, to somebody who already holds their own link; that
/// is the whole cost, and it buys a keyed read instead of a table scan on an authentication path.
/// </para>
/// <para>
/// <b>32 bytes, not 16.</b> 256 bits of <see cref="RandomNumberGenerator"/> output, so the verifier is
/// not guessable even with the selector known and even without rate limiting — which this build does not
/// have on these endpoints (see the task report, and doc 11 §7 G-41 asking for the same thing on the
/// guest order link).
/// </para>
/// </remarks>
internal static class CustomerAccountTokens
{
    /// <summary>
    /// <c>identity.UserTokens.LoginProvider</c> for every row this class writes.
    /// </summary>
    /// <remarks>
    /// <b>Its own provider string, not the admin side's <c>SpencerDenim.Admin</c>.</b> One store holds
    /// both kinds of user (doc 08 D7), so the table holds staff authenticator keys and customer
    /// one-time links side by side; a provider per audience means a query for one audience's rows can
    /// never see the other's, and the names below are prefixed as well so a row is readable on its own.
    /// Like every <c>UserTokens</c> row these are never audited (doc 08 §3.8), and PDPA anonymisation
    /// (§3.1 step 2) deletes them with the rest.
    /// </remarks>
    public const string LoginProvider = "SpencerDenim.Customer";

    /// <summary>The pending password-reset token for this customer. At most one per account.</summary>
    public const string PasswordResetName = "CustomerPasswordReset";

    /// <summary>The pending email-confirmation token for this customer. At most one per account.</summary>
    public const string EmailConfirmationName = "CustomerEmailConfirmation";

    /// <summary>
    /// The address a confirmation token was minted for, so a redemption confirms <i>that</i> address and
    /// not whatever the row says now (doc 11 P11 step 10's email change).
    /// </summary>
    private const char FieldSeparator = '|';

    /// <summary>Separates the selector from the verifier in the link's token.</summary>
    private const char SelectorSeparator = '.';

    /// <summary>A freshly minted token: what goes in the link, and what goes in the row.</summary>
    /// <param name="Link">The opaque string for the URL. Never stored.</param>
    /// <param name="StoredValue">
    /// What <c>UserTokens.Value</c> holds: the verifier's hash, the expiry, and (for a confirmation) the
    /// address it was minted for. Never enough to rebuild <see cref="Link"/>.
    /// </param>
    public readonly record struct MintedToken(string Link, string StoredValue);

    /// <summary>What a stored row says, once it has been parsed and the verifier checked.</summary>
    /// <param name="ExpiresAtUtc">When the link stops working.</param>
    /// <param name="Subject">
    /// The address a confirmation token was minted for, or <see langword="null"/> for a reset token,
    /// which is about the account rather than about an address.
    /// </param>
    public readonly record struct RedeemedToken(DateTime ExpiresAtUtc, string? Subject);

    /// <summary>
    /// Mints a token for <paramref name="userId"/> that expires at <paramref name="expiresAtUtc"/>.
    /// </summary>
    /// <param name="subject">
    /// The normalised address a confirmation is for, or null for a reset. Stored so that a confirmation
    /// link emailed to one address cannot confirm a different one the customer has since typed in.
    /// </param>
    public static MintedToken Mint(int userId, DateTime expiresAtUtc, string? subject = null)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var selector = userId.ToString(CultureInfo.InvariantCulture);

        var stored = new StringBuilder()
            .Append(Hash(verifier))
            .Append(FieldSeparator)
            .Append(new DateTimeOffset(expiresAtUtc, TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        if (subject is not null)
        {
            stored.Append(FieldSeparator).Append(subject);
        }

        return new MintedToken($"{selector}{SelectorSeparator}{verifier}", stored.ToString());
    }

    /// <summary>
    /// The user id a link claims to be for, or <see langword="null"/> when the string is not shaped like
    /// one of ours.
    /// </summary>
    /// <remarks>
    /// <b>It is a claim and not a fact</b> — anybody can write <c>7.rubbish</c> — so it is only ever used
    /// to choose which row to read, and the verifier decides whether the holder may have it.
    /// </remarks>
    public static int? ReadUserId(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var separator = token.IndexOf(SelectorSeparator, StringComparison.Ordinal);
        return separator > 0
            && int.TryParse(
                token.AsSpan(0, separator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var userId)
            && userId > 0
            ? userId
            : null;
    }

    /// <summary>
    /// Checks <paramref name="token"/> against <paramref name="storedValue"/>, returning what the row
    /// said when — and only when — the verifier matches and the row parses.
    /// </summary>
    /// <remarks>
    /// <b>Expiry is deliberately <i>not</i> checked here.</b> The caller holds the clock (every use case
    /// in this codebase takes <c>IClock</c> rather than reading <c>DateTime.UtcNow</c> in a helper), and
    /// separating "is this the right secret" from "is it still in date" is what lets the redemption path
    /// delete an expired row instead of leaving it to rot.
    /// </remarks>
    public static RedeemedToken? Verify(string? token, string? storedValue)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(storedValue))
        {
            return null;
        }

        var separator = token.IndexOf(SelectorSeparator, StringComparison.Ordinal);
        if (separator < 0 || separator == token.Length - 1)
        {
            return null;
        }

        var verifier = token[(separator + 1)..];

        // Three fields at most, and the third (a confirmation's address) may itself contain nothing
        // interesting — addresses cannot contain '|', so splitting on it is safe.
        var parts = storedValue.Split(FieldSeparator, 3);
        if (parts.Length < 2
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresUnix))
        {
            return null;
        }

        if (!HashesMatch(parts[0], verifier))
        {
            return null;
        }

        return new RedeemedToken(
            DateTimeOffset.FromUnixTimeSeconds(expiresUnix).UtcDateTime,
            parts.Length == 3 ? parts[2] : null);
    }

    private static string Hash(string verifier) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    private static bool HashesMatch(string expectedHash, string verifier) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedHash),
            Encoding.ASCII.GetBytes(Hash(verifier)));

    /// <summary>
    /// Base64 with the URL-safe alphabet and no padding, so the value survives a query string untouched
    /// and a shopper's mail client cannot break it on a <c>+</c> or a <c>/</c>.
    /// </summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
