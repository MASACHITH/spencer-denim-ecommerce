using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Content;

/// <summary><c>content.AnnouncementMessages</c> (doc 08 §3.7, GLB-03) [A].</summary>
internal sealed class AnnouncementMessageConfiguration : IEntityTypeConfiguration<AnnouncementMessage>
{
    public void Configure(EntityTypeBuilder<AnnouncementMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AnnouncementMessages", AppDbContext.ContentSchema);

        builder.HasKey(message => message.Id);

        builder.Property(message => message.Message).HasMaxLength(200).IsRequired();
        builder.Property(message => message.LinkUrl).HasMaxLength(500);
        builder.Property(message => message.SortOrder).IsRequired();
        builder.Property(message => message.IsActive).HasDefaultValue(true).IsRequired();
        builder.Property(message => message.StartsAtUtc);
        builder.Property(message => message.EndsAtUtc);

        builder.ApplyAuditColumns();

        // #192 (tester D1): the table has no rowversion, so the audit timestamp is the concurrency token.
        // Checked in the handler alone it was a read-then-write, and two saves from one version both won
        // under RCSI; as an EF concurrency token it is a predicate on the UPDATE and DELETE themselves, so
        // the second writer matches no row and gets a 409. Model-only: it changes no column, so no migration.
        builder.Property(message => message.UpdatedAtUtc).IsConcurrencyToken();

        // Not one of doc 08 §3.7's listed indexes, but the bar is on every page of the storefront
        // and reads exactly this predicate in exactly this order, so the alternative is a scan on
        // every request.
        builder
            .HasIndex(message => new { message.IsActive, message.SortOrder })
            .HasDatabaseName("IX_AnnouncementMessages_Active_SortOrder");
    }
}
