using System.Reflection;

namespace SpencerDenim.Domain.Identity;

/// <summary>
/// The catalogue of valid permission values, built by reflecting over <see cref="Permissions"/> so the list
/// can never drift from the constants (doc 08 §3.1: "Code constants are the catalogue of valid values; a
/// startup check logs unknown values").
/// <para>
/// <b>Adding a permission is not finished when the constant exists.</b> A new value reaches the default roles
/// (Store Manager, Order Staff, Content Editor) on a <i>fresh</i> database only, because the seeder never
/// touches a role that already exists (doc 08 §9, N13) — so on any database that already exists, including the
/// client's live one, their staff would silently lack it: the screen is there and the button refuses (issue #122).
/// If a default role should hold the new permission: (1) add it to that role in
/// <see cref="DefaultRoleDefinitions"/>, and (2) add a <see cref="DefaultRolePermissionIntroduction"/> to
/// <see cref="DefaultRoleSeedManifest.Introductions"/> at the <b>next</b> version number
/// (<see cref="DefaultRoleSeedManifest.CurrentVersion"/> + 1). Step 2 is what makes it reach existing databases,
/// once, on the next start, without touching anything a Super Admin changed. <c>DefaultRoleSeedManifestTests</c>
/// fails if step 1 is done without step 2. If no default role should hold it, say so in the PR: Super Admin
/// needs nothing, because it is evaluated rather than enumerated.
/// </para>
/// </summary>
public static class PermissionCatalog
{
    /// <summary>Every declared <c>Module.Action</c> permission string.</summary>
    public static IReadOnlySet<string> All { get; } = BuildCatalog();

    /// <summary>
    /// <c>CK_RoleClaims_NoSecurity</c> (doc 08 §3.1): <see cref="Permissions.Settings.Security"/> can never be
    /// granted to a role, GrantsAll or otherwise — it only ever applies through the GrantsAll bypass.
    /// </summary>
    public static bool IsGrantableToRoles(string permission) =>
        All.Contains(permission) && !string.Equals(permission, Permissions.Settings.Security, StringComparison.Ordinal);

    private static HashSet<string> BuildCatalog()
    {
        var values = new HashSet<string>(StringComparer.Ordinal);

        foreach (var module in typeof(Permissions).GetNestedTypes(BindingFlags.Public | BindingFlags.Static))
        {
            foreach (var field in module.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
                {
                    var value = (string?)field.GetRawConstantValue();
                    if (!string.IsNullOrEmpty(value))
                    {
                        values.Add(value);
                    }
                }
            }
        }

        return values;
    }
}
