namespace SpencerDenim.Application.Admin.CurrentUser;

/// <summary>Basic profile fields a staff user's principal is built from and displayed with.</summary>
public sealed record AdminUserSummary(int UserId, string Email, string FirstName, string? LastName, bool IsActive);
