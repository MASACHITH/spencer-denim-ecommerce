namespace SpencerDenim.Application.Settings.Abstractions;

/// <summary>A staff user a security email can be addressed to.</summary>
/// <param name="UserId">The user's id, for de-duplication only; it is never written to an outbox row.</param>
/// <param name="Email">Their sign-in address, which is also the account's password-reset channel.</param>
/// <param name="FirstName">For the greeting.</param>
public sealed record StaffAddressee(int UserId, string Email, string FirstName);

/// <summary>
/// Who security emails go to (doc 08 §3.8 DB2: "every active GrantsAll holder"). Implemented in Infrastructure over
/// the identity tables, read at the moment of the change — never from a claim on somebody's cookie, for doc 08
/// §3.1 R4's reason: a Super Admin demoted five minutes ago is no longer one.
/// </summary>
public interface ISecurityAlertRecipients
{
    /// <summary>Every active staff user holding a role that grants every permission, with an address, by id.</summary>
    Task<IReadOnlyList<StaffAddressee>> ListActiveSuperAdminsAsync(CancellationToken cancellationToken);

    /// <summary>One active staff user by id — the signed-in admin a test email goes to — or null.</summary>
    Task<StaffAddressee?> FindActiveStaffAsync(int userId, CancellationToken cancellationToken);
}
