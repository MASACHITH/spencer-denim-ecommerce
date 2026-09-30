namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// Edits a staff user's profile fields (ADM-02). <see cref="NewEmail"/> is null when the email is not
/// changing; when it is set, doc 08 §3.1 DB4 rule 2 requires the actor to hold GrantsAll (email is the
/// password-reset channel), regardless of whether the rest of the edit would otherwise pass the subset rule.
/// </summary>
public sealed record UpdateStaffUserProfileCommand(
    int ActorUserId,
    int TargetUserId,
    string FirstName,
    string? LastName,
    string? NewEmail,
    string? IpAddress,
    string? UserAgent);
