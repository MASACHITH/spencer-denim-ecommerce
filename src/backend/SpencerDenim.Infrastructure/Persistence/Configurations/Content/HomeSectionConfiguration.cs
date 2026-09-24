using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Content;

/// <summary><c>content.HomeSections</c> (doc 08 §3.7) [A] [RV].</summary>
internal sealed class HomeSectionConfiguration : IEntityTypeConfiguration<HomeSection>
{
    public void Configure(EntityTypeBuilder<HomeSection> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "HomeSections",
            AppDbContext.ContentSchema,
            table =>
            {
                // Doc 08 §3.7: the ten blocks of HOME-01..10 and nothing else.
                table.HasCheckConstraint("CK_HomeSections_SectionType", "[SectionType] BETWEEN 1 AND 10");

                // Doc 08 §1.4 (DB15): JSON is nvarchar(max) guarded by ISJSON, never the native json type.
                table.HasCheckConstraint(
                    "CK_HomeSections_SettingsJson",
                    "[SettingsJson] IS NULL OR ISJSON([SettingsJson]) = 1");
            });

        builder.HasKey(section => section.Id);

        builder.Property(section => section.SectionType).HasColumnType("tinyint").IsRequired();
        builder.Property(section => section.Title).HasMaxLength(150);
        builder.Property(section => section.Subtitle).HasMaxLength(300);
        builder.Property(section => section.IsEnabled).HasDefaultValue(false).IsRequired();
        builder.Property(section => section.SortOrder).IsRequired();
        builder.Property(section => section.SettingsJson).HasColumnType("nvarchar(max)");
        builder.Property(section => section.StartsAtUtc);
        builder.Property(section => section.EndsAtUtc);

        builder.ApplyAuditColumns();
        builder.ApplyRowVersion(section => section.RowVersion);

        // Doc 08 §3.7: IX (IsEnabled, SortOrder) — the homepage reads the enabled sections in order
        // and nothing else.
        builder
            .HasIndex(section => new { section.IsEnabled, section.SortOrder })
            .HasDatabaseName("IX_HomeSections_Enabled_SortOrder");
    }
}
