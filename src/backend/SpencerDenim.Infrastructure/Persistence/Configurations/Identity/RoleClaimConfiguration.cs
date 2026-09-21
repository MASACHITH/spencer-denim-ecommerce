using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

/// <summary>
/// Maps <c>identity.RoleClaims</c> — permissions (doc 08 §3.1, simplification 7). <c>ClaimType</c> is always
/// <c>"perm"</c> and <c>ClaimValue</c> a <see cref="Permissions"/> constant.
/// </summary>
public sealed class RoleClaimConfiguration : IEntityTypeConfiguration<IdentityRoleClaim<int>>
{
    /// <summary>The fixed <c>ClaimType</c> used for every permission grant (doc 08 §3.1).</summary>
    public const string PermissionClaimType = "perm";

    public void Configure(EntityTypeBuilder<IdentityRoleClaim<int>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "RoleClaims",
            "identity",
            t => t.HasCheckConstraint("CK_RoleClaims_NoSecurity", "[ClaimValue] <> 'Settings.Security'"));

        builder.Property(rc => rc.ClaimType).HasMaxLength(50).IsRequired();
        builder.Property(rc => rc.ClaimValue).HasMaxLength(64).IsRequired();

        builder.HasOne<AppRole>().WithMany().HasForeignKey(rc => rc.RoleId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(rc => new { rc.RoleId, rc.ClaimType, rc.ClaimValue })
            .HasDatabaseName("UX_RoleClaims_Role_Type_Value")
            .IsUnique();
    }
}
