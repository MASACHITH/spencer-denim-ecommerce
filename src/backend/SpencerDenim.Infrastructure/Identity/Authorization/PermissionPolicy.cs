using System.Globalization;

namespace SpencerDenim.Infrastructure.Identity.Authorization;

/// <summary>Maps a permission string to the ASP.NET Core authorization policy name that guards it.</summary>
public static class PermissionPolicy
{
    private const string PolicyPrefix = "Permission:";

    public static string NameFor(string permission) => string.Create(
        CultureInfo.InvariantCulture,
        $"{PolicyPrefix}{permission}");
}
