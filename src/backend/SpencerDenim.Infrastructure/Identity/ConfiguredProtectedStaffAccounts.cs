using Microsoft.Extensions.Options;
using SpencerDenim.Application.Admin.Users.Abstractions;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// <see cref="IProtectedStaffAccounts"/> backed by <see cref="AdminSecurityOptions.ProtectedStaffEmails"/> and
/// <see cref="AdminSecurityOptions.GuardedStaffEmails"/>.
/// </summary>
internal sealed class ConfiguredProtectedStaffAccounts(IOptions<AdminSecurityOptions> options) : IProtectedStaffAccounts
{
    private readonly HashSet<string> protectedEmails = Parse(options.Value.ProtectedStaffEmails);
    private readonly HashSet<string> guardedEmails = Parse(options.Value.GuardedStaffEmails);

    public bool IsProtected(string? email) =>
        !string.IsNullOrWhiteSpace(email) && protectedEmails.Contains(email.Trim());

    public bool IsGuarded(string? email) =>
        !string.IsNullOrWhiteSpace(email) && guardedEmails.Contains(email.Trim());

    /// <summary>
    /// Splits the configured list (commas, semicolons or white space between addresses) into a set that ignores
    /// case. Blank entries are dropped, so an unset variable is an empty set and protects nobody.
    /// </summary>
    internal static HashSet<string> Parse(string? configured) =>
        new(
            (configured ?? string.Empty).Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when every entry looks like an email address (one <c>@</c> with text either side). Checked at start-up:
    /// a typo here would not fail anywhere else, it would simply leave the account unprotected.
    /// </summary>
    internal static bool AllEntriesLookLikeEmails(string? configured) =>
        Parse(configured).All(entry =>
        {
            var at = entry.IndexOf('@', StringComparison.Ordinal);
            return at > 0 && at < entry.Length - 1 && entry.IndexOf('@', at + 1) < 0;
        });
}
