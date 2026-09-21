using Microsoft.AspNetCore.Identity;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// <c>identity.Roles</c> (doc 08 §3.1, ADM-02). Only the Super Admin (GrantsAll) role is seeded by this slice
/// (see <c>Seeding/AdminSeedHostedService</c>); the other three default roles (Store Manager, Order Staff,
/// Content Editor) and their permission sets belong to the ADM-02 Users &amp; Roles management slice.
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
