namespace SpencerDenim.Application.Admin.Users.Abstractions;

/// <summary>Already-validated, already-authorized data for creating one staff user (ADM-02).</summary>
public sealed record NewStaffUserData(
    string Email,
    string FirstName,
    string? LastName,
    string InitialPassword,
    IReadOnlyList<int> RoleIds);
