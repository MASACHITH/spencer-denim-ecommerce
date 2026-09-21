using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

/// <summary>
/// Maps <c>identity.UserRoles</c> (doc 08 §3.1): composite PK, both FKs cascade. <see cref="IdentityUserRole{TKey}"/>
/// has no navigation properties, so — unlike <see cref="AppUser"/> and <see cref="AppRole"/> — the relationships
/// to them have to be declared explicitly here; EF Core cannot infer them by convention.
/// </summary>
public sealed class UserRoleConfiguration : IEntityTypeConfiguration<IdentityUserRole<int>>
{
    public void Configure(EntityTypeBuilder<IdentityUserRole<int>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserRoles", "identity");
        builder.HasKey(ur => new { ur.UserId, ur.RoleId });

        builder.HasOne<AppUser>().WithMany().HasForeignKey(ur => ur.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppRole>().WithMany().HasForeignKey(ur => ur.RoleId).OnDelete(DeleteBehavior.Cascade);

        // Doc 08 §1.6 FK-index policy: RoleId is not the leading key of the composite PK, so it needs its own index.
        builder.HasIndex(ur => ur.RoleId).HasDatabaseName("IX_UserRoles_RoleId");
    }
}
