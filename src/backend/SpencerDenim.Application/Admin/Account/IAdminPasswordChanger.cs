namespace SpencerDenim.Application.Admin.Account;

/// <summary>
/// Port that changes a staff user's own password through ASP.NET Core Identity. The implementation owns every
/// Identity detail: checking the current password with the same lockout bookkeeping as sign-in (doc 08 §3.1
/// N14, so this endpoint is no side door for guessing a password), the password policy, clearing
/// <c>MustChangePassword</c> and rotating the <c>SecurityStamp</c>. It never touches the cookie — re-issuing the
/// caller's own session is the Api endpoint's job, as it is for sign-in.
/// </summary>
public interface IAdminPasswordChanger
{
    Task<AdminPasswordChangeOutcome> ChangePasswordAsync(
        int userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken);
}
