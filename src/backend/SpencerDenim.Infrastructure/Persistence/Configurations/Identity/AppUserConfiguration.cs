using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Reference;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

/// <summary>Maps <c>identity.Users</c> exactly as baselined in doc 08 §3.1.</summary>
public sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Users", "identity", t => t.HasCheckConstraint("CK_Users_UserType", "[UserType] IN (1,2)"));

        builder.Property(u => u.UserType).HasConversion<byte>().IsRequired();

        builder.Property(u => u.UserName).HasMaxLength(256);
        builder.Property(u => u.NormalizedUserName).HasMaxLength(256);
        builder.Property(u => u.Email).HasMaxLength(256);
        builder.Property(u => u.NormalizedEmail).HasMaxLength(256);
        builder.Property(u => u.PhoneNumber).HasColumnType("varchar(20)");

        builder.Property(u => u.FirstName).HasMaxLength(80).IsRequired();
        builder.Property(u => u.LastName).HasMaxLength(80);

        builder.Property(u => u.IsActive).HasDefaultValue(true);
        builder.Property(u => u.MustChangePassword).HasDefaultValue(false);

        builder.Property(u => u.LastLoginAtUtc).HasColumnType("datetime2(3)");
        builder.Property(u => u.AnonymisedAtUtc).HasColumnType("datetime2(3)");

        // Issue #18: was two manual .Property() calls here for CreatedAtUtc/UpdatedAtUtc — same column type and
        // default as every other [A] table (doc 08 §1.5), now via the shared extension like the rest of them, now
        // that AppUser implements IAuditable. No column, type or default changes; see the PR for the model diff.
        builder.ApplyAuditColumns();

        // Doc 08 §3.1: "UX_Users_NormalizedEmail and UX_Users_NormalizedUserName (filtered NOT NULL)".
        builder.HasIndex(u => u.NormalizedEmail)
            .HasDatabaseName("UX_Users_NormalizedEmail")
            .IsUnique()
            .HasFilter("[NormalizedEmail] IS NOT NULL");

        builder.HasIndex(u => u.NormalizedUserName)
            .HasDatabaseName("UX_Users_NormalizedUserName")
            .IsUnique()
            .HasFilter("[NormalizedUserName] IS NOT NULL");

        builder.HasIndex(u => new { u.UserType, u.CreatedAtUtc }).HasDatabaseName("IX_Users_UserType_CreatedAtUtc");

        // Doc 08 §3.1 gives `DefaultDistrictId` as "FK Districts", and until W3-01 the column had neither
        // the constraint nor an index: there was no customer sign-in, so nothing could set or read it.
        // `AppUser.DefaultDistrictId`'s own remark names this change as the one that owes both, "in the
        // change that gives the column its first reader" — the account profile (doc 11 P11 step 10) is
        // that reader, so both land here.
        //
        // The index is doc 08 §1.6's DB7 rule (every FK column leads at least one index) and nothing more:
        // no screen asks "which customers default to Jaffna". It is the price of the constraint, which is
        // worth paying — without it a profile save could store a district id that names no district, and
        // the delivery estimate GLB-06 draws from it would then have no zone.
        builder.HasIndex(u => u.DefaultDistrictId).HasDatabaseName("IX_Users_DefaultDistrictId");

        // NO ACTION (doc 08 §1.6's default): a district is deactivated, never deleted, and a customer
        // whose district the shop stops serving keeps their profile and simply gets no estimate.
        builder
            .HasOne<District>()
            .WithMany()
            .HasForeignKey(u => u.DefaultDistrictId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
