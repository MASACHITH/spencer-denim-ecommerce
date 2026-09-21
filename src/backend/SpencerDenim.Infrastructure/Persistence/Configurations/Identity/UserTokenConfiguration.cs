using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

/// <summary>
/// Maps <c>identity.UserTokens</c> (doc 08 §3.1): holds the staff authenticator key and recovery codes for the
/// future Super Admin 2FA flow (schema-only in this slice — no enrolment/verification UI is built yet).
/// </summary>
public sealed class UserTokenConfiguration : IEntityTypeConfiguration<IdentityUserToken<int>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<int>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserTokens", "identity");
        builder.HasKey(t => new { t.UserId, t.LoginProvider, t.Name });

        builder.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
