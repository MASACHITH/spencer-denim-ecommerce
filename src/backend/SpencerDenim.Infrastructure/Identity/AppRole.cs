using Microsoft.AspNetCore.Identity;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// <c>identity.Roles</c> (doc 08 §3.1, ADM-02). All four default roles are seeded by
/// <c>Seeding/AdminSeedHostedService</c>: Super Admin (GrantsAll) plus, as of W1-08 (issue #8), Store Manager,
/// Order Staff and Content Editor — see <c>Seeding/DefaultRoleDefinitions</c> for their permission sets (doc 08
/// §9). Beyond the default four, roles are read-only in this slice (<c>Application/Admin/Users</c>): full role
/// CRUD (create/edit/delete a role, change its permissions) is GrantsAll-only per doc 08 §3.1 DB4 rule 1 and was
/// not asked for by this task — a future slice if the client needs custom roles beyond the four defaults.
/// </summary>
public sealed class AppRole : IdentityRole<int>
{
    public string? Description { get; set; }

    /// <summary>Seeded default roles; cannot be deleted (enforced by the future Roles management use case).</summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// DF 0, seed-only and immutable at the application login (doc 08 §3.1: only the migration login may set
    /// this flag in production — see the follow-up note on <c>sd_app</c> / <c>sd_migrator</c> SQL logins in the
    /// final report). <c>UX_Roles_GrantsAll</c> allows at most one role with this flag set.
    /// </summary>
    public bool GrantsAllPermissions { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
}
