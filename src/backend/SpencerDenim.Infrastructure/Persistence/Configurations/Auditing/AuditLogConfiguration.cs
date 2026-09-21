using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Auditing;

/// <summary>
/// Maps <c>ops.AuditLogs</c> (doc 08 §3.8, ADM-03). <c>DATA_COMPRESSION = PAGE</c> is applied by the migration's
/// raw SQL (EF Core's SQL Server data-compression builder targets a newer engine baseline than this project's
/// minimum-supported compatibility level, doc 08 §1.1), not here.
/// </summary>
public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "AuditLogs",
            "ops",
            t =>
            {
                t.HasCheckConstraint("CK_AuditLogs_BeforeJson", "[BeforeJson] IS NULL OR ISJSON([BeforeJson]) = 1");
                t.HasCheckConstraint("CK_AuditLogs_AfterJson", "[AfterJson] IS NULL OR ISJSON([AfterJson]) = 1");
            });

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).UseIdentityColumn();

        builder.Property(a => a.OccurredAtUtc).HasColumnType("datetime2(3)").IsRequired();
        builder.Property(a => a.UserEmail).HasMaxLength(256);
        builder.Property(a => a.Action).HasColumnType("varchar(30)").IsRequired();
        builder.Property(a => a.EntityType).HasColumnType("varchar(100)").IsRequired();
        builder.Property(a => a.EntityId).HasColumnType("varchar(100)");
        builder.Property(a => a.BeforeJson).HasColumnType("nvarchar(max)");
        builder.Property(a => a.AfterJson).HasColumnType("nvarchar(max)");
        builder.Property(a => a.IpAddress).HasColumnType("varchar(45)");
        builder.Property(a => a.UserAgent).HasMaxLength(400);
        builder.Property(a => a.CorrelationId).HasColumnType("varchar(64)");

        builder.HasIndex(a => new { a.EntityType, a.EntityId, a.OccurredAtUtc })
            .HasDatabaseName("IX_AuditLogs_Entity")
            .IsDescending(false, false, true);

        builder.HasIndex(a => new { a.UserId, a.OccurredAtUtc })
            .HasDatabaseName("IX_AuditLogs_User")
            .IsDescending(false, true);

        builder.HasIndex(a => a.OccurredAtUtc).HasDatabaseName("IX_AuditLogs_Occurred");
    }
}
