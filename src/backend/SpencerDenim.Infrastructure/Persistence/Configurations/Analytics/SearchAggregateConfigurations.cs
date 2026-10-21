using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Analytics;

/// <summary>
/// Maps <c>analytics.SearchQueriesDaily</c> (doc 08 §3.8, DB25; issue #78). The clustered PK leads with <c>Date</c>,
/// which is what the popular-searches read (a 30-day range) and the 13-month purge (everything before a day) both
/// seek on, so the table needs no other index. No FKs and no audit columns: the rows are counts.
/// <para>
/// <b>The collation is written out</b> rather than inherited from the database default, because the key's
/// equality <i>is</i> the collation: <c>Latin1_General_100_CI_AS_SC</c> (doc 08 §1.1) decides which two terms are
/// one row, and an <c>_SC</c> collation is what keeps a Sinhala or Tamil term with supplementary characters
/// comparable at all. A database restored under another default must not silently change what counts as the
/// same search.
/// </para>
/// </summary>
internal sealed class SearchQueryDailyConfiguration : IEntityTypeConfiguration<SearchQueryDaily>
{
    /// <summary>Doc 08 §1.1.</summary>
    public const string Collation = "Latin1_General_100_CI_AS_SC";

    public void Configure(EntityTypeBuilder<SearchQueryDaily> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "SearchQueriesDaily",
            AppDbContext.AnalyticsSchema,
            table => table.HasCheckConstraint(
                "CK_SearchQueriesDaily_Counts",
                "[Searches] >= 1 AND [ZeroResultSearches] >= 0 AND [ZeroResultSearches] <= [Searches]"));

        builder.HasKey(row => new { row.Date, row.QueryNormalized }).HasName("PK_SearchQueriesDaily");

        builder.Property(row => row.Date).HasColumnType("date");
        builder.Property(row => row.QueryNormalized)
            .HasMaxLength(SearchQueryDaily.QueryMaxLength)
            .UseCollation(Collation)
            .IsRequired();
        builder.Property(row => row.Searches);
        builder.Property(row => row.ZeroResultSearches);
    }
}

/// <summary>Maps <c>analytics.HiddenSearchTerms</c>, the staff hide list for the public popular-searches list (issue #78).</summary>
internal sealed class HiddenSearchTermConfiguration : IEntityTypeConfiguration<HiddenSearchTerm>
{
    public void Configure(EntityTypeBuilder<HiddenSearchTerm> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("HiddenSearchTerms", AppDbContext.AnalyticsSchema);

        builder.HasKey(row => row.QueryNormalized).HasName("PK_HiddenSearchTerms");

        // The same width and collation as the column it is compared with, so "is this term hidden" is the same
        // equality the counts are keyed by.
        builder.Property(row => row.QueryNormalized)
            .HasMaxLength(SearchQueryDaily.QueryMaxLength)
            .UseCollation(SearchQueryDailyConfiguration.Collation)
            .IsRequired();
        builder.Property(row => row.HiddenAtUtc);
    }
}
