namespace SpencerDenim.Application.Admin.Authentication;

/// <summary>Command for <c>POST /api/admin/auth/login</c>. <paramref name="Email"/> is matched case-insensitively.</summary>
public sealed record AdminLoginCommand(string Email, string Password, string? IpAddress, string? UserAgent);
