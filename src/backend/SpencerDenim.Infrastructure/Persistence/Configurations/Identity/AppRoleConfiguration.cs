using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

/// <summary>Maps <c>identity.Roles</c> exactly as baselined in doc 08 §3.1.</summary>
public sealed class AppRoleConfiguration : IEntityTypeConfiguration<AppRole>
{
    public void Configure(EntityTypeBuilder<AppRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Roles", "identity");

        builder.Property(r => r.Name).HasMaxLength(256);
        builder.Property(r => r.NormalizedName).HasMaxLength(256);
        builder.Property(r => r.Description).HasMaxLength(300);
        builder.Property(r => r.IsSystem).HasDefaultValue(false);
        builder.Property(r => r.GrantsAllPermissions).HasDefaultValue(false);
        builder.Property(r => r.CreatedAtUtc).HasColumnType("datetime2(3)").HasDefaultValueSql("sysutcdatetime()");
        builder.Property(r => r.UpdatedAtUtc).HasColumnType("datetime2(3)");

        builder.HasIndex(r => r.NormalizedName).HasDatabaseName("UX_Roles_NormalizedName").IsUnique();

        // Doc 08 §3.1: "UX_Roles_GrantsAll (GrantsAllPermissions) WHERE GrantsAllPermissions = 1: at most one
        // all-permissions role." A filtered unique index on a bit column, matched only where it is 1, allows at
        // most one such row while leaving every non-GrantsAll role unconstrained.
        builder.HasIndex(r => r.GrantsAllPermissions)
            .HasDatabaseName("UX_Roles_GrantsAll")
            .IsUnique()
            .HasFilter("[GrantsAllPermissions] = 1");
    }
}
