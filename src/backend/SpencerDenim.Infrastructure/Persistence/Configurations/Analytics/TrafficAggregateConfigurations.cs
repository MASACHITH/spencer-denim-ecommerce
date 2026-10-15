using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Analytics;

/// <summary>
/// Maps <c>analytics.TrafficDaily</c> (doc 08 §3.8). The clustered PK leads with <c>Date</c>, which is what
/// both the nightly rebuild's <c>DELETE</c> of a day range and the Traffic page's period reads seek on
/// (doc 08 §8.2: "Traffic Sources / Traffic page | TrafficDaily, PageDaily + today live | Clustered PKs").
/// No FKs (doc 08 §3.8), no audit columns: the rows are derived and rebuilt.
/// </summary>
internal sealed class TrafficDailyConfiguration : IEntityTypeConfiguration<TrafficDaily>
{
    public void Configure(EntityTypeBuilder<TrafficDaily> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "TrafficDaily",
            AppDbContext.AnalyticsSchema,
            table =>
            {
                table.HasCheckConstraint(
                    "CK_TrafficDaily_Channel",
                    $"[Channel] BETWEEN {(byte)TrafficChannel.Direct} AND {(byte)TrafficChannel.Other}");
                table.HasCheckConstraint(
                    "CK_TrafficDaily_DeviceType",
                    $"[DeviceType] IN ({(byte)DeviceType.Desktop}, {(byte)DeviceType.Mobile}, {(byte)DeviceType.Tablet}, {(byte)DeviceType.Other})");
                table.HasCheckConstraint(
                    "CK_TrafficDaily_Counts",
                    "[PageViews] >= 0 AND [Sessions] >= 0 AND [BotPageViews] >= 0 AND [Sessions] <= [PageViews]");
            });

        builder.HasKey(row => new { row.Date, row.Channel, row.DeviceType, row.SourceKey })
            .HasName("PK_TrafficDaily");

        builder.Property(row => row.Date).HasColumnType("date");
        builder.Property(row => row.Channel).HasColumnType("tinyint");
        builder.Property(row => row.DeviceType).HasColumnType("tinyint");
        builder.Property(row => row.SourceKey).HasMaxLength(TrafficDaily.SourceKeyMaxLength);
        builder.Property(row => row.PageViews);
        builder.Property(row => row.Sessions);
        builder.Property(row => row.BotPageViews);
    }
}

/// <summary>
/// Maps <c>analytics.PageDaily</c> (doc 08 §3.8). Keyed by the path's MD5 rather than the path, so the
/// clustered key stays 19 bytes wide however long a filtered listing address is.
/// </summary>
internal sealed class PageDailyConfiguration : IEntityTypeConfiguration<PageDaily>
{
    public void Configure(EntityTypeBuilder<PageDaily> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "PageDaily",
            AppDbContext.AnalyticsSchema,
            table =>
            {
                table.HasCheckConstraint("CK_PageDaily_PageType", $"[PageType] IN ({PageTypes.SqlList})");
                table.HasCheckConstraint(
                    "CK_PageDaily_Counts",
                    "[PageViews] >= 0 AND [Sessions] >= 0 AND [Sessions] <= [PageViews]");
            });

        builder.HasKey(row => new { row.Date, row.PathHash }).HasName("PK_PageDaily");

        builder.Property(row => row.Date).HasColumnType("date");
        builder.Property(row => row.PathHash).HasColumnType("binary(16)");
        builder.Property(row => row.Path).HasMaxLength(PageView.PathMaxLength).IsRequired();
        builder.Property(row => row.PageType).HasColumnType("tinyint");
        builder.Property(row => row.ProductId);
        builder.Property(row => row.PageViews);
        builder.Property(row => row.Sessions);
    }
}
