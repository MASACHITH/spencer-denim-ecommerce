using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Marketing;

/// <summary><c>marketing.NewsletterSubscribers</c> (doc 08 §3.6; HOME-10, GLB-07, ADM-21).</summary>
internal sealed class NewsletterSubscriberConfiguration : IEntityTypeConfiguration<NewsletterSubscriber>
{
    public void Configure(EntityTypeBuilder<NewsletterSubscriber> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "NewsletterSubscribers",
            AppDbContext.MarketingSchema,
            table => table.HasCheckConstraint(
                "CK_NewsletterSubscribers_Status",
                "[Status] IN (1, 2)"));

        builder.HasKey(subscriber => subscriber.Id);

        builder.Property(subscriber => subscriber.Email).HasMaxLength(NewsletterEmail.MaxLength).IsRequired();
        builder.Property(subscriber => subscriber.EmailNormalized).HasMaxLength(NewsletterEmail.MaxLength).IsRequired();
        builder.Property(subscriber => subscriber.Status).HasColumnType("tinyint").IsRequired();
        builder.Property(subscriber => subscriber.Source).HasColumnType("varchar(30)").IsRequired();
        builder.Property(subscriber => subscriber.ConsentAtUtc).IsRequired();

        // Doc 08 §3.7 sizes an IP column at varchar(45), which is a full IPv6 address with a
        // scope id; the same width is used everywhere one is stored.
        builder.Property(subscriber => subscriber.ConsentIpAddress).HasColumnType("varchar(45)");

        builder.Property(subscriber => subscriber.UnsubscribeToken).IsRequired();
        builder.Property(subscriber => subscriber.UnsubscribedAtUtc);
        builder.Property(subscriber => subscriber.CreatedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();

        // Doc 08 §3.6: UX on EmailNormalized. It is what makes a second signup an update rather
        // than a duplicate, under the race as well: two simultaneous posts cannot both insert.
        builder
            .HasIndex(subscriber => subscriber.EmailNormalized)
            .IsUnique()
            .HasDatabaseName("UX_NewsletterSubscribers_EmailNormalized");

        // Doc 08 §1.3: an unguessable public token always carries a unique index.
        builder
            .HasIndex(subscriber => subscriber.UnsubscribeToken)
            .IsUnique()
            .HasDatabaseName("UX_NewsletterSubscribers_UnsubscribeToken");

        builder
            .HasIndex(subscriber => new { subscriber.Status, subscriber.CreatedAtUtc })
            .HasDatabaseName("IX_NewsletterSubscribers_Status_CreatedAtUtc");

        // No navigation on the domain entity: AppUser is an infrastructure type (ASP.NET Core
        // Identity), and the domain does not know about it. The FK is real all the same.
        builder
            .HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(subscriber => subscriber.UserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(subscriber => subscriber.UserId).HasDatabaseName("IX_NewsletterSubscribers_UserId");
    }
}
