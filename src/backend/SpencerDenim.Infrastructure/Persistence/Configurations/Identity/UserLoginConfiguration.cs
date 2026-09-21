using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

/// <summary>Maps <c>identity.UserLogins</c> (doc 08 §3.1): Identity standard table, stays empty by design.</summary>
public sealed class UserLoginConfiguration : IEntityTypeConfiguration<IdentityUserLogin<int>>
{
    public void Configure(EntityTypeBuilder<IdentityUserLogin<int>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserLogins", "identity");
        builder.HasKey(l => new { l.LoginProvider, l.ProviderKey });

        builder.HasOne<AppUser>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(l => l.UserId).HasDatabaseName("IX_UserLogins_UserId");
    }
}
