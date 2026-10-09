using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Ops;

/// <summary>
/// Maps <c>ops.EmailOutbox</c> (doc 08 §3.8, ACC-03).
/// <para>
/// <b>Not <c>[A]</c>, not <c>[SD]</c>, not <c>[RV]</c>.</b> Doc 08 §3.8 lists this table under "tables
/// never audited" — a body may contain a password-reset link — so there is no audit column pair for the
/// <c>SaveChanges</c> interceptor to stamp and no <c>ops.AuditLogs</c> row for any change here. A soft
/// delete would leave that same body under a flag; a row version guards a form two people edit at once,
/// which a queue row is not — concurrency between senders is the claim's conditional <c>UPDATE</c>
/// (doc 08 §5.2).
/// </para>
/// <para>
/// <b>No foreign key to <c>sales.Orders</c>.</b> Doc 08 §3.8 gives <c>RelatedEntityType</c> /
/// <c>RelatedEntityId</c> as a loose pair and names no FK, and the table is retained for one year against
/// an order's seven (§8.4) — so a key would eventually block the purge this table owes. One consequence
/// is worth stating because it is the price: nothing in the database stops a row naming an order that
/// does not exist, and <c>IX_EmailOutbox_Related</c> does not exist either, because no query reads by it.
/// </para>
/// </summary>
internal sealed class EmailOutboxMessageConfiguration : IEntityTypeConfiguration<EmailOutboxMessage>
{
    public void Configure(EntityTypeBuilder<EmailOutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "EmailOutbox",
            AppDbContext.OpsSchema,
            table =>
            {
                // Doc 08 §3.8 marks Status "CK". Five is Cancelled: the range is the document's whole
                // status list, including the one status nothing in this build writes, because a CK that
                // allowed 0–4 would make the schema disagree with the design.
                table.HasCheckConstraint(
                    "CK_EmailOutbox_Status",
                    $"[Status] BETWEEN 0 AND {(byte)EmailOutboxStatus.Cancelled}");

                // Not in doc 08, and the two halves are what the sender's arithmetic assumes: a negative
                // attempt count would make EmailBackoff.DelayAfter answer null and strand the message,
                // and a ceiling of zero would make every message dead on arrival.
                //
                // **There is deliberately no `AttemptCount <= MaxAttempts` clause.** It is true of every
                // row the sender writes — the claim increments and the failure path turns the message
                // Dead the moment the count reaches the ceiling — but nothing reads it, and a CK with no
                // reader on a table written inside a payment transaction is a way to turn a counter bug
                // into a rolled-back approval.
                table.HasCheckConstraint(
                    "CK_EmailOutbox_Attempts",
                    "[AttemptCount] >= 0 AND [MaxAttempts] >= 1");
            });

        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).UseIdentityColumn();

        builder
            .Property(message => message.TemplateCode)
            .HasColumnType($"varchar({EmailTemplateCodes.MaxLength})")
            .IsRequired();

        builder
            .Property(message => message.DeduplicationKey)
            .HasColumnType($"varchar({EmailOutboxMessage.DeduplicationKeyMaxLength})");

        builder.Property(message => message.TransportOverrideProtected).HasColumnType("nvarchar(max)");

        builder
            .Property(message => message.ToEmail)
            .HasMaxLength(EmailOutboxMessage.ToEmailMaxLength)
            .IsRequired();

        builder.Property(message => message.ToName).HasMaxLength(EmailOutboxMessage.ToNameMaxLength);

        builder
            .Property(message => message.Subject)
            .HasMaxLength(EmailOutboxMessage.SubjectMaxLength)
            .IsRequired();

        builder.Property(message => message.BodyHtml).HasColumnType("nvarchar(max)");
        builder.Property(message => message.BodyText).HasColumnType("nvarchar(max)");

        builder
            .Property(message => message.RelatedEntityType)
            .HasColumnType($"varchar({EmailOutboxMessage.RelatedEntityTypeMaxLength})");

        builder
            .Property(message => message.RelatedEntityId)
            .HasColumnType($"varchar({EmailOutboxMessage.RelatedEntityIdMaxLength})");

        builder.Property(message => message.Status).HasColumnType("tinyint").IsRequired();
        builder.Property(message => message.AttemptCount).HasColumnType("smallint").IsRequired();
        builder.Property(message => message.MaxAttempts).HasColumnType("smallint").IsRequired();
        builder.Property(message => message.NextAttemptAtUtc).IsRequired();
        builder.Property(message => message.LockedUntilUtc);
        builder.Property(message => message.LastError).HasMaxLength(EmailOutboxMessage.LastErrorMaxLength);

        builder
            .Property(message => message.CreatedAtUtc)
            .HasDefaultValueSql("sysutcdatetime()")
            .IsRequired();

        builder.Property(message => message.SentAtUtc);

        // Doc 08 §3.8, verbatim: `IX_EmailOutbox_Due (NextAttemptAtUtc) WHERE Status IN (0,3)`.
        //
        // **The filter is the working set and the reason this index is small.** Pending (0) and Failed (3)
        // are the only two statuses a sweep can claim from; Sent, Dead and Cancelled are terminal and make
        // up every row the table will ever hold a week later, so an unfiltered index would grow for a year
        // and be read for thirty seconds' worth of rows.
        //
        // The filter is written as literals here and the claim's own `Status IN (0, 3)` is written as
        // literals too (DB28): a parameterised `Status = @p` does not provably imply `Status IN (0,3)` at
        // compile time, so the optimiser would refuse this index and scan a table full of message bodies.
        builder
            .HasIndex(message => message.NextAttemptAtUtc)
            .HasDatabaseName("IX_EmailOutbox_Due")
            .HasFilter($"[Status] IN ({(byte)EmailOutboxStatus.Pending}, {(byte)EmailOutboxStatus.Failed})");

        // **Not in doc 08, and here for a cost measured against the alternative.** A sender that dies
        // between the claim and the send leaves its rows at Sending (1), which is *outside* the index
        // above — so recovering them needs a predicate on `Status = 1`, and with no index for it that is
        // a clustered scan of a table whose rows carry `nvarchar(max)` bodies, run once every sweep for
        // ever. This index is almost always empty (a row sits in Sending for the length of one SMTP
        // conversation), so it costs a few pages and turns that scan into a seek.
        //
        // It cannot collide with `IX_EmailOutbox_Due`: different key column, different filter. EF's
        // index-collapsing convention is about *foreign-key* indexes, and this table has no foreign key
        // at all — but both indexes are named explicitly anyway, which is what makes a collapse
        // impossible to happen quietly.
        builder
            .HasIndex(message => message.LockedUntilUtc)
            .HasDatabaseName("IX_EmailOutbox_Stuck")
            .HasFilter($"[Status] = {(byte)EmailOutboxStatus.Sending}");

        // Doc 08 §3.8 marks DeduplicationKey "Filtered UX". Filtered on IS NOT NULL, because a plain
        // unique index would allow exactly one row in the whole table with no key — and every row this
        // build writes has none (see EmailOutboxMessage.DeduplicationKey).
        builder
            .HasIndex(message => message.DeduplicationKey)
            .HasDatabaseName("UX_EmailOutbox_Dedup")
            .IsUnique()
            .HasFilter("[DeduplicationKey] IS NOT NULL");
    }
}
