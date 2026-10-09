using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Ops;

/// <summary>
/// Maps <c>ops.AdminAlerts</c> (doc 08 §3.8 N2; ADM-04's banner).
/// <para>
/// <b>Not <c>[A]</c>, not <c>[SD]</c>, not <c>[RV]</c>.</b> Doc 08 §3.8 gives the table no audit column
/// pair: the rows are written by jobs with no acting user, and what is audited is the acknowledgement
/// (<c>AuditActions.AlertAcknowledged</c>), which the acknowledge use case writes into
/// <c>ops.AuditLogs</c> itself. A soft delete would hide an alert nobody acknowledged, which is the one
/// thing this table exists to prevent; a row version guards a form two people edit at once, and the
/// acknowledgement is a conditional <c>UPDATE</c> instead (doc 08 §5.2) so that of two Super Admins
/// pressing the button at the same moment exactly one is recorded.
/// </para>
/// </summary>
internal sealed class AdminAlertConfiguration : IEntityTypeConfiguration<AdminAlert>
{
    public void Configure(EntityTypeBuilder<AdminAlert> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "AdminAlerts",
            AppDbContext.OpsSchema,
            table =>
            {
                // Doc 08 §3.8: "Severity tinyint | 1 Info, 2 Warning, 3 Critical. CK". Written as the
                // enum's own bounds so the constraint and the type cannot drift: a fourth severity added
                // to the enum without a migration would fail on its first insert rather than storing a
                // number no screen can render.
                table.HasCheckConstraint(
                    "CK_AdminAlerts_Severity",
                    $"[Severity] BETWEEN {(byte)AdminAlertSeverity.Info} AND {(byte)AdminAlertSeverity.Critical}");

                // Doc 08 §3.8: "DetailsJson | nvarchar(max) | Keys, entity IDs and amounts; never secret
                // values. CK ISJSON". The `IS NULL OR` half is what the audit log's two JSON constraints
                // also carry: the column is nullable, and ISJSON(NULL) is NULL, which a check constraint
                // treats as "not violated" — but writing it out makes the nullability deliberate rather
                // than something a reader has to know about three-valued logic to be sure of.
                table.HasCheckConstraint(
                    "CK_AdminAlerts_DetailsJson",
                    "[DetailsJson] IS NULL OR ISJSON([DetailsJson]) = 1");

                // Not in doc 08, and it pins the one invariant the read path depends on: an alert is open
                // exactly when AcknowledgedAtUtc IS NULL, which is IX_AdminAlerts_Open's filter. A row
                // with an acknowledging user and no time would be invisible to the banner and look
                // acknowledged in a report — two readers disagreeing about the same row. The note is
                // allowed only on an acknowledged row for the same reason.
                table.HasCheckConstraint(
                    "CK_AdminAlerts_Acknowledged",
                    "([AcknowledgedAtUtc] IS NULL AND [AcknowledgedByUserId] IS NULL AND [AcknowledgementNote] IS NULL)"
                        + " OR ([AcknowledgedAtUtc] IS NOT NULL AND [AcknowledgedByUserId] IS NOT NULL)");
            });

        builder.HasKey(alert => alert.Id);
        builder.Property(alert => alert.Id).UseIdentityColumn();

        builder
            .Property(alert => alert.AlertType)
            .HasColumnType($"varchar({AdminAlertTypes.MaxLength})")
            .IsRequired();

        builder.Property(alert => alert.Severity).HasColumnType("tinyint").IsRequired();

        builder
            .Property(alert => alert.Title)
            .HasMaxLength(AdminAlert.TitleMaxLength)
            .IsRequired();

        // Doc 08 §1.4: a JSON column is always mapped explicitly to nvarchar(max), never left for EF to
        // pick SQL Server 2025's native `json` type, which LocalDB 2019 does not have.
        builder.Property(alert => alert.DetailsJson).HasColumnType("nvarchar(max)");

        builder
            .Property(alert => alert.RelatedEntityType)
            .HasColumnType($"varchar({AdminAlert.RelatedEntityTypeMaxLength})");

        builder
            .Property(alert => alert.RelatedEntityId)
            .HasColumnType($"varchar({AdminAlert.RelatedEntityIdMaxLength})");

        builder.Property(alert => alert.AuditLogId);

        builder
            .Property(alert => alert.CreatedAtUtc)
            .HasDefaultValueSql("sysutcdatetime()")
            .IsRequired();

        builder.Property(alert => alert.AcknowledgedByUserId);
        builder.Property(alert => alert.AcknowledgedAtUtc);

        builder
            .Property(alert => alert.AcknowledgementNote)
            .HasMaxLength(AdminAlert.AcknowledgementNoteMaxLength);

        // Doc 08 §3.8: "AcknowledgedByUserId | int | Y | FK Users". Restrict rather than SetNull: a staff
        // user is never hard-deleted (doc 08 §3.1 — they are deactivated), and SetNull would quietly
        // rewrite who acknowledged an alert if one ever were.
        builder
            .HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(alert => alert.AcknowledgedByUserId)
            .HasConstraintName("FK_AdminAlerts_AcknowledgedBy_Users")
            .OnDelete(DeleteBehavior.Restrict);

        // Doc 08 §3.8, verbatim: `IX_AdminAlerts_Open (CreatedAtUtc DESC) WHERE AcknowledgedAtUtc IS
        // NULL`: the dashboard banner.
        //
        // **The filter is the working set.** Acknowledged rows are kept for a year (doc 08 §8.4) and
        // unacknowledged ones are the handful a banner reads every time an admin page loads, so an
        // unfiltered index would grow all year to serve a query that only ever wants the open rows.
        //
        // `IS NULL` is a literal predicate with nothing to parameterise, so — unlike the outbox's
        // `Status IN (0,3)` filter, where a parameterised status cost 20,101 logical reads against 18
        // (DB28, EmailOutboxPlanTests) — this filter is matched by any query that spells the same
        // predicate. `AdminAlertsPlanTests` proves the read path's own statement seeks it, because that
        // is not visible from here and the alert-type restriction R10 adds must not change it.
        builder
            .HasIndex(alert => alert.CreatedAtUtc)
            .HasDatabaseName("IX_AdminAlerts_Open")
            .IsDescending(true)
            .HasFilter("[AcknowledgedAtUtc] IS NULL");

        // **EF's own foreign-key index, named explicitly rather than left to the convention.** The
        // convention would create it as `IX_AdminAlerts_AcknowledgedByUserId`, and it ignores filters
        // when it decides whether an index it needs already exists — so a convention index and a
        // hand-written filtered index on the same column collapse into one, silently. These two are on
        // different columns and cannot collide, but naming both is what makes that true by inspection
        // instead of by argument. It is not in doc 08's index list, and it is here because the FK is: it
        // is what keeps a user's row from being checked by a table scan.
        builder
            .HasIndex(alert => alert.AcknowledgedByUserId)
            .HasDatabaseName("IX_AdminAlerts_AcknowledgedBy");
    }
}
