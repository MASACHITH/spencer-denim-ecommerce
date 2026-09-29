namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// The application's own <c>identity.UserTokens</c> rows (W1-10), kept under a login-provider name of their own so
/// they never collide with Identity's (<c>[AspNetUserStore]</c>: the authenticator key and recovery codes). Like
/// every UserTokens row they are never audited (doc 08 §3.8), and PDPA anonymisation deletes them with the rest.
/// </summary>
internal static class AdminTokenNames
{
    public const string LoginProvider = "SpencerDenim.Admin";

    /// <summary>
    /// The one open "password accepted, code still needed" sign-in: a SHA-256 hash of its random id and its expiry.
    /// Consumed on success and on lockout, so a copied pending cookie cannot be used a second time.
    /// </summary>
    public const string PendingTwoFactorSignIn = "PendingTwoFactorSignIn";

    /// <summary>The last authenticator time step accepted, so the same code cannot be used twice.</summary>
    public const string LastAuthenticatorStep = "LastAuthenticatorStep";
}
