using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

/// <summary>
/// Maps <c>identity.UserClaims</c> (doc 08 §3.1 and §3.1 "Identity standard tables"): stays empty by design.
/// <c>CK_UserClaims_NoPerm</c> stops permissions leaking onto the principal via user claims (doc 08 §3.1 R4).
/// </summary>
public sealed class UserClaimConfiguration : IEntityTypeConfiguration<IdentityUserClaim<int>>
{
    public void Configure(EntityTypeBuilder<IdentityUserClaim<int>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "UserClaims",
            "identity",
            t => t.HasCheckConstraint("CK_UserClaims_NoPerm", "[ClaimType] <> 'perm'"));

        builder.HasOne<AppUser>().WithMany().HasForeignKey(uc => uc.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
