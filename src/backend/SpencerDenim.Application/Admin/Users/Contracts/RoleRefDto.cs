namespace SpencerDenim.Application.Admin.Users.Contracts;

/// <summary>One role, named, as referenced from a staff user (list and detail views alike).</summary>
public sealed record RoleRefDto(int Id, string Name, bool GrantsAllPermissions);
