using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Analytics;

/// <summary>
/// Maps <c>analytics.PageViews</c> (doc 08 §3.8; G4, ADM-06, ADM-20).
/// <para>
/// <b>No FK on <c>ProductId</c> or <c>CategoryId</c></b> (doc 08 §3.8: "No FK"), so a product purge never
/// has to reach into a table of millions of rows, and so <b>no FK index either</b>: DB7's rule is about FK
/// columns and these are not FKs. <b>Not audited</b> (doc 08 §3.8's "Tables never audited"), no soft delete,
/// no row version: rows are appended and later purged.
/// </para>
/// <para>
/// <c>DATA_COMPRESSION = PAGE</c> (doc 08 §3.8) is not something EF Core can declare, so the migration
/// rebuilds the table and the index with it; <c>AnalyticsSchemaTests</c> reads it back from
/// <c>sys.partitions</c>.
/// </para>
/// </summary>
internal sealed class PageViewConfiguration : IEntityTypeConfiguration<PageView>
{
    public void Configure(EntityTypeBuilder<PageView> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "PageViews",
            AppDbContext.AnalyticsSchema,
            table =>
            {
                table.HasCheckConstraint("CK_PageViews_PageType", $"[PageType] IN ({PageTypes.SqlList})");
                table.HasCheckConstraint(
                    "CK_PageViews_Channel",
                    $"[Channel] BETWEEN {(byte)TrafficChannel.Direct} AND {(byte)TrafficChannel.Other}");
                table.HasCheckConstraint(
                    "CK_PageViews_DeviceType",
                    $"[DeviceType] IN ({(byte)DeviceType.Desktop}, {(byte)DeviceType.Mobile}, {(byte)DeviceType.Tablet}, {(byte)DeviceType.Other})");
            });

        builder.HasKey(view => view.Id);
        builder.Property(view => view.Id).UseIdentityColumn();

        // Doc 08 §3.8: datetime2(0). Overrides the datetime2(3) convention; the classifier truncates to the
        // second before it gets here, so SQL Server never rounds a 23:59:59.9 onto the next Colombo day.
        builder.Property(view => view.OccurredAtUtc).HasColumnType("datetime2(0)").IsRequired();

        builder.Property(view => view.SessionId).IsRequired();
        builder.Property(view => view.Path).HasMaxLength(PageView.PathMaxLength).IsRequired();
        builder.Property(view => view.PageType).HasColumnType("tinyint").IsRequired();
        builder.Property(view => view.ProductId);
        builder.Property(view => view.CategoryId);
        builder.Property(view => view.ReferrerHost).HasColumnType($"varchar({PageView.ReferrerHostMaxLength})");
        builder.Property(view => view.UtmSource).HasMaxLength(PageView.UtmMaxLength);
        builder.Property(view => view.UtmMedium).HasMaxLength(PageView.UtmMaxLength);
        builder.Property(view => view.UtmCampaign).HasMaxLength(PageView.UtmMaxLength);
        builder.Property(view => view.Channel).HasColumnType("tinyint").IsRequired();
        builder.Property(view => view.DeviceType).HasColumnType("tinyint").IsRequired();
        builder.Property(view => view.IsBot).HasDefaultValue(false).IsRequired();

        // Doc 08 §3.8, verbatim: `IX_PageViews_Occurred` INCLUDE (SessionId, IsBot, Channel, DeviceType). It
        // serves the day-range scans: the nightly rollup reads a Colombo day's rows by OccurredAtUtc, the
        // Traffic page's "today is live" figures, and the retention purge's oldest-first deletes.
        builder
            .HasIndex(view => view.OccurredAtUtc)
            .HasDatabaseName("IX_PageViews_Occurred")
            .IncludeProperties(view => new { view.SessionId, view.IsBot, view.Channel, view.DeviceType });
    }
}
