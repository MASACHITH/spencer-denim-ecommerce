using System.Globalization;

namespace SpencerDenim.Infrastructure.Identity.Authorization;

/// <summary>Maps a permission string to the ASP.NET Core authorization policy name that guards it.</summary>
public static class PermissionPolicy
{
    private const string PolicyPrefix = "Permission:";

    public static string NameFor(string permission) => string.Create(
        CultureInfo.InvariantCulture,
        $"{PolicyPrefix}{permission}");

    /// <summary>
    /// The settings screen's read: <b>any</b> settings permission opens it, and the read model then
    /// filters group by group against the caller's own permissions (doc 08 §3.8 DB2).
    /// <para>
    /// A named policy rather than a generated one, because it is the only endpoint in the project that
    /// works this way and a general "any of these" naming scheme would invite the pattern doc 03 §3a's
    /// one-permission-per-endpoint rule exists to prevent. See
    /// <see cref="AnyPermissionRequirement"/> for why the read is the exception.
    /// </para>
    /// </summary>
    public const string ReadAnySettingsGroup = PolicyPrefix + "ReadAnySettingsGroup";

    /// <summary>
    /// The admin alert banner and its acknowledge (doc 08 §3.8 R10, W4-02a): <b>any</b> audience R10 names
    /// may reach the routes, and the use case then decides which alert types that caller sees.
    /// <para>
    /// The second endpoint in the project to work this way, and for the same reason as the first:
    /// R10 gives GrantsAll holders every type and <c>Orders.Approve</c> / <c>Orders.Refund</c> holders
    /// two of them, so the guard cannot be one permission without shutting an order staff member out of
    /// the payment alerts that are addressed to them. GrantsAll passes through the bypass in
    /// <c>EffectivePermissions.Has</c>, which is why only the two order permissions are named.
    /// See <see cref="AnyPermissionRequirement"/> for why this mechanism is deliberately rare.
    /// </para>
    /// </summary>
    public const string ReadAnyAlertAudience = PolicyPrefix + "ReadAnyAlertAudience";
}
