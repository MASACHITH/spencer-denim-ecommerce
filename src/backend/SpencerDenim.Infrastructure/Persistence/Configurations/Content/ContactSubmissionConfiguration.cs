using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Content;

/// <summary>
/// <c>content.ContactSubmissions</c> (doc 08 §3.7, GLB-10).
/// <para>
/// <b>Not <c>[A]</c>, not <c>[SD]</c> and not <c>[RV]</c>, and each of those is doc 08's own choice
/// rather than an omission.</b> The audit columns would record a <i>visitor</i> as the creator, and
/// there is no user row for them; a soft delete would leave the bank account number doc 08 §3.7
/// anticipates sitting in the table under a flag; and a row version guards a form two people edit at
/// once, which a status change on an inbox is not — the status transitions are idempotent by
/// <see cref="ContactSubmission.SetStatus"/>, so two staff marking the same enquiry Replied is a
/// no-op rather than a lost update.
/// </para>
/// </summary>
internal sealed class ContactSubmissionConfiguration : IEntityTypeConfiguration<ContactSubmission>
{
    public void Configure(EntityTypeBuilder<ContactSubmission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "ContactSubmissions",
            AppDbContext.ContentSchema,
            table => table.HasCheckConstraint(
                "CK_ContactSubmissions_Status",
                $"[Status] BETWEEN 0 AND {(byte)ContactSubmissionStatus.Spam}"));

        builder.HasKey(submission => submission.Id);

        builder.Property(submission => submission.Name).HasMaxLength(ContactSubmission.NameMaxLength).IsRequired();
        builder.Property(submission => submission.Email).HasMaxLength(ContactSubmission.EmailMaxLength).IsRequired();

        builder
            .Property(submission => submission.Phone)
            .HasColumnType($"varchar({ContactSubmission.PhoneMaxLength})");

        builder.Property(submission => submission.Subject).HasMaxLength(ContactSubmission.SubjectMaxLength);
        builder.Property(submission => submission.Message).HasMaxLength(ContactSubmission.MessageMaxLength).IsRequired();

        builder
            .Property(submission => submission.OrderNumber)
            .HasColumnType($"varchar({ContactSubmission.OrderNumberMaxLength})");

        builder.Property(submission => submission.Status).HasColumnType("tinyint").IsRequired();
        builder.Property(submission => submission.HandledAtUtc);

        builder
            .Property(submission => submission.IpAddress)
            .HasColumnType($"varchar({ContactSubmission.IpAddressMaxLength})");

        builder
            .Property(submission => submission.CreatedAtUtc)
            .HasDefaultValueSql("sysutcdatetime()")
            .IsRequired();

        // Doc 08 §3.7: IX (Status, CreatedAtUtc DESC). The admin inbox filters by status and orders
        // newest first, which is exactly this.
        builder
            .HasIndex(submission => new { submission.Status, submission.CreatedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("IX_ContactSubmissions_Status_Created");

        // Not in doc 08, and it is the retention step of §8.4 that needs it: "enquiries keep 2 years"
        // is `DELETE TOP (4000) … WHERE CreatedAtUtc < @cutoff`, and "IP addresses 90 days" is
        // `UPDATE … SET IpAddress = NULL WHERE CreatedAtUtc < @cutoff AND IpAddress IS NOT NULL`.
        // Neither can seek on the index above, whose leading column is Status. The purge job itself is
        // not built here (there is no job host for it yet); this is the part of it that has to exist
        // before the table is written to, because adding an index to a large table later is the
        // expensive half.
        builder
            .HasIndex(submission => submission.CreatedAtUtc)
            .HasDatabaseName("IX_ContactSubmissions_CreatedAtUtc");

        // No FK on HandledByUserId: doc 08 §1.5's convention for user columns, as
        // OrderStatusHistoryEntry.ChangedByUserId and Payment.ApprovedByUserId already apply it — both
        // are the same thing, a column recording which staff member acted. Doc 08 §3.7's table does write
        // "FK Users" here, so this is a deliberate deviation, taken for consistency with those two rather
        // than invented: a FK would also oblige an index leading this column (doc 08 §1.6 DB7) that
        // nothing queries by, on the one table in this schema a public endpoint inserts into.
        builder.Property(submission => submission.HandledByUserId);
    }
}
