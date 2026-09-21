using System.Reflection;

namespace SpencerDenim.Domain.Identity;

/// <summary>
/// The catalogue of valid permission values, built by reflecting over <see cref="Permissions"/> so the list
/// can never drift from the constants (doc 08 §3.1: "Code constants are the catalogue of valid values; a
/// startup check logs unknown values").
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
