using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary>
/// <c>sales.NumberSeries</c> (doc 08 §3.5).
/// <para>
/// Neither [A] nor [RV], and the second absence matters: doc 08 §3.5 allocates a number with
/// <c>UPDATE … SET NextValue = NextValue + 1 OUTPUT deleted.NextValue</c>, a single atomic statement. A
/// rowversion would invite someone to read the row, add one and save — which is the read-then-write race
/// the OUTPUT form exists to avoid, and the one that would hand two orders the same number.
/// </para>
/// </summary>
internal sealed class NumberSeriesConfiguration : IEntityTypeConfiguration<NumberSeries>
{
    public void Configure(EntityTypeBuilder<NumberSeries> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "NumberSeries",
            AppDbContext.SalesSchema,

            // Not in doc 08's column notes, but implied by the whole design: a series counts upwards from 1,
            // and a zero or negative NextValue would format as SD-2026-000000 or worse. The row is inserted
            // lazily by the allocator (doc 08 §3.5), so this is the only thing standing between a typo in
            // that lazy insert and a nonsensical order number.
            table => table.HasCheckConstraint("CK_NumberSeries_NextValue", "[NextValue] >= 1"));

        builder.HasKey(series => series.SeriesKey);

        builder
            .Property(series => series.SeriesKey)
            .HasColumnType("varchar(30)")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(series => series.Prefix).HasColumnType("varchar(10)").IsRequired();
        builder.Property(series => series.NextValue).IsRequired();
        builder.Property(series => series.UpdatedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();
    }
}
