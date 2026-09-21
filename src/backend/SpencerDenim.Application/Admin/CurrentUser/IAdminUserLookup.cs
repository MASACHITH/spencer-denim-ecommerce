namespace SpencerDenim.Application.Admin.CurrentUser;

/// <summary>Port for reading a staff user's profile by id. Implemented in Infrastructure over <c>UserManager</c>.</summary>
public interface IAdminUserLookup
{
    Task<AdminUserSummary?> FindByIdAsync(int userId, CancellationToken cancellationToken);
}
