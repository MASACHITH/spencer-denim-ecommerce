using Microsoft.AspNetCore.Identity;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// <c>identity.Roles</c> (doc 08 §3.1, ADM-02). All four default roles are seeded by
/// <c>Seeding/AdminSeedHostedService</c>: Super Admin (GrantsAll) plus, as of W1-08 (issue #8), Store Manager,
/// Order Staff and Content Editor — see <c>Seeding/DefaultRoleDefinitions</c> for their permission sets (doc 08
/// §9). Beyond the default four, roles are read-only in this slice (<c>Application/Admin/Users</c>): full role
/// CRUD (create/edit/delete a role, change its permissions) is GrantsAll-only per doc 08 §3.1 DB4 rule 1.
/// <para>
/// <b>It is in the signed scope, and the earlier note here that it "was not asked for" was wrong</b> — about the
/// scope and about the task. Five sources ask for it, the clearest being doc 11 P14 §C steps 9–10, which specify
/// the editing screen down to the control ("tick permissions per module … <c>Settings.Security</c> cannot be
/// ticked for any role"). <c>TASKS.md</c>'s W1-08 line says "CRUD endpoints/UI", and doc 02 ADM-02 says
/// "Super Admin can edit these or create new ones". Owner decision, 2026-10-05: W1-08 ships the staff-user half
/// as part 1, and role CRUD is issue <b>#105</b> — which also owns the <see cref="IsSystem"/> enforcement this
/// type is waiting for, and carries #101 plus a stale permission snapshot as blockers that go live the moment a
/// role's permissions can change.
/// </para>
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
