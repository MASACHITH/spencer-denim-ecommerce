namespace SpencerDenim.Domain.Identity;

/// <summary>
/// One permission that a later release added to one default role (<see cref="DefaultRoleSeedManifest"/>).
/// </summary>
/// <param name="Version">The default-role seed version that introduced it. Always above the baseline.</param>
/// <param name="RoleName">The default role's name, as in <see cref="DefaultRoleDefinitions"/>.</param>
/// <param name="Permission">A <see cref="Permissions"/> value.</param>
/// <param name="Origin">The task that added it, so the log and the journal can name where it came from.</param>
public sealed record DefaultRolePermissionIntroduction(int Version, string RoleName, string Permission, string Origin);

/// <summary>
/// <b>Which seed version introduced each permission a default role holds</b> — the half of doc 08 §9 / N13 that
/// <see cref="DefaultRoleDefinitions"/> cannot express on its own. Issue #122.
/// <para>
/// <b>The problem it solves.</b> The seeder creates a default role only when it does not exist and, per N13,
/// never touches one that does, so that a Super Admin's own edits to a role are not overwritten on every deploy.
/// The cost is that a permission added to a default role after a database was created reaches that database's
/// role never: the Store Manager on the client's live database silently lacks it, the screen is there, and the
/// button refuses. Super Admin is unaffected because it is evaluated rather than enumerated.
/// </para>
/// <para>
/// <b>The fix, and why a version rather than "grant whatever is missing".</b> A role that is missing a
/// permission cannot say whether it was never given or was <i>taken away on purpose</i>. A version can:
/// the database records the version it has been brought up to (<c>Seed.Version</c>, doc 08 §9), and the startup
/// step grants a role only the permissions introduced <i>after</i> that version. Anything introduced at or before
/// it was either granted already or deliberately removed, and in both cases it is left exactly as found. The step
/// only ever <b>adds</b>; it never removes a permission, never touches a role's other permissions, never creates
/// a role and never touches Super Admin or a custom role (<c>DefaultRolePermissionSync</c>).
/// </para>
/// <para>
/// <b>The baseline.</b> <see cref="BaselineVersion"/> (1) is what the default roles held when they first shipped,
/// in W1-08 (#102, 2026-10-05): every permission in <see cref="DefaultRoleDefinitions"/> that has no entry in
/// <see cref="Introductions"/> is a version-1 permission. A database with <b>no recorded version</b> is assumed
/// to be at the baseline, because the oldest default roles anywhere were seeded at it and no earlier shape
/// exists. The first start on such a database therefore grants every introduction above version 1 that the role
/// does not already hold — which is the point, since that is exactly the set the existing roles are missing — and
/// a role that deliberately lacks a version-1 permission is not changed.
/// </para>
/// <para>
/// <b>The one thing the baseline cannot know</b> is a database that was created <i>after</i> a later introduction
/// and then had that permission removed from a role by hand (SQL) before this step existed: with no version
/// recorded, the first start re-grants it. There is no role editor yet (#105), so nobody but a developer with SQL
/// access can have done that, and from that first start on the recorded version protects every later removal.
/// </para>
/// <para>
/// <b>Adding a permission to a default role</b> — the rule that stops this recurring:
/// <list type="number">
/// <item><description>Add the permission to the role in <see cref="DefaultRoleDefinitions"/> (that is what a fresh
/// database is seeded with).</description></item>
/// <item><description>Add a <see cref="DefaultRolePermissionIntroduction"/> below, at <b>the next version number</b>:
/// <see cref="CurrentVersion"/> + 1 — never at a version that has already shipped, and never by editing an
/// existing entry. That bump is what makes the permission reach databases that already exist.</description></item>
/// <item><description><c>DefaultRoleSeedManifestTests</c> fails if the first step is done without the second, or
/// if a shipped version is edited.</description></item>
/// </list>
/// </para>
/// </summary>
public static class DefaultRoleSeedManifest
{
    /// <summary>
    /// What the default roles held when they first shipped (W1-08, #102). The version a database with no
    /// recorded version is assumed to be at.
    /// </summary>
    public const int BaselineVersion = 1;

    private const string W211 = "W2-11 (GLB-10, #127): content pages and the contact enquiry inbox";

    /// <summary>
    /// Every permission added to a default role after the baseline, in version order. A permission that is not
    /// listed here is a baseline permission.
    /// </summary>
    public static IReadOnlyList<DefaultRolePermissionIntroduction> Introductions { get; } =
    [
        // Version 2 — W2-11. Customers.Edit moves a contact enquiry's status; Content.Create/Delete make and
        // remove a custom content page. Doc 08 §9 grants Store Manager every Module.Action and Content Editor
        // Content.*, so both roles gain them; Order Staff gains none of them.
        new(2, DefaultRoleDefinitions.StoreManager.Name, Permissions.Customers.Edit, W211),
        new(2, DefaultRoleDefinitions.StoreManager.Name, Permissions.Content.Create, W211),
        new(2, DefaultRoleDefinitions.StoreManager.Name, Permissions.Content.Delete, W211),
        new(2, DefaultRoleDefinitions.ContentEditor.Name, Permissions.Content.Create, W211),
        new(2, DefaultRoleDefinitions.ContentEditor.Name, Permissions.Content.Delete, W211),
    ];

    /// <summary>
    /// The version a fresh database is at, and the one the startup step records once it has run: the highest
    /// version in <see cref="Introductions"/>, or the baseline when there are none.
    /// </summary>
    public static int CurrentVersion { get; } =
        Introductions.Select(introduction => introduction.Version).Append(BaselineVersion).Max();

    /// <summary>
    /// The version that introduced <paramref name="permission"/> to <paramref name="roleName"/>, or
    /// <see cref="BaselineVersion"/> when no release added it.
    /// </summary>
    public static int IntroducedIn(string roleName, string permission) =>
        Introductions
            .Where(introduction => Matches(introduction, roleName) && string.Equals(introduction.Permission, permission, StringComparison.Ordinal))
            .Select(introduction => introduction.Version)
            .DefaultIfEmpty(BaselineVersion)
            .Min();

    /// <summary>
    /// The permissions a release newer than <paramref name="appliedVersion"/> added to <paramref name="roleName"/>,
    /// in manifest order — what the startup step grants a role that exists at that version.
    /// </summary>
    public static IReadOnlyList<DefaultRolePermissionIntroduction> IntroducedAfter(string roleName, int appliedVersion) =>
        Introductions
            .Where(introduction => Matches(introduction, roleName) && introduction.Version > appliedVersion)
            .ToList();

    private static bool Matches(DefaultRolePermissionIntroduction introduction, string roleName) =>
        string.Equals(introduction.RoleName, roleName, StringComparison.OrdinalIgnoreCase);
}
