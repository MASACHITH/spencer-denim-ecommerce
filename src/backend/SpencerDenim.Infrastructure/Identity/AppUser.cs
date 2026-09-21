using Microsoft.AspNetCore.Identity;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// <c>identity.Users</c> (doc 08 §3.1, D7): one Identity store for customers and staff, distinguished by
/// <see cref="UserType"/>. This class is a persistence-layer construct (it must extend
/// <see cref="IdentityUser{TKey}"/>, which needs the Identity packages), which is why it lives in Infrastructure
/// rather than Domain — the framework-free RBAC rules live in <c>SpencerDenim.Domain.Identity</c> instead.
/// </summary>
public sealed class AppUser : IdentityUser<int>
{
    public UserType UserType { get; set; }

    public string FirstName { get; set; } = string.Empty;

    public string? LastName { get; set; }

    /// <summary>
    /// FK <c>ref.Districts</c> in doc 08, left unconfigured here (see doc): follow-up when reference data lands.
    /// </summary>
    public int? DefaultDistrictId { get; set; }

    public bool IsActive { get; set; } = true;

    public bool MustChangePassword { get; set; }

    public DateTime? LastLoginAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>No FK (doc 08 §1.5): users are never hard-deleted.</summary>
    public int? CreatedByUserId { get; set; }

    public int? UpdatedByUserId { get; set; }

    /// <summary>PDPA anonymisation timestamp (doc 08 §3.1). Set only by the (future) anonymisation use case.</summary>
    public DateTime? AnonymisedAtUtc { get; set; }
}
