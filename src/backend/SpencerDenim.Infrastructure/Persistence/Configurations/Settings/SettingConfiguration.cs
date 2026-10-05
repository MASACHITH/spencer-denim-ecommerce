using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Settings;

/// <summary>
/// Maps <c>ops.Settings</c> (doc 08 §3.8; ADM-22, GLB-02, PDP-05, PAY-03) <c>[RV]</c>.
/// <para>
/// <b>The whole table and every constraint, including the parts nothing uses yet.</b> The
/// <see cref="SettingValueType.Secret"/> and <see cref="SettingValueType.Decimal"/> machinery, and the
/// <c>Settings.Payments</c> / <c>Settings.Security</c> permission values, are all reachable by this schema
/// although no seeded key is one of them. That is the point: issue #53 and W3 add <i>rows</i>, not a
/// migration — and a CHECK constraint written to the five keys that exist today is a constraint the next
/// task has to widen, under its own deadline, with a migration that nobody connected to this table's
/// design.
/// </para>
/// <para>
/// <b>What is deliberately not here.</b> The sensitive-change alert of doc 08 §3.8 N2 — the
/// <c>ops.AdminAlerts</c> row, the dashboard banner and the <c>SensitiveSettingsChanged</c> email through
/// both the new and previous SMTP configurations. It fires only for <c>Bank.*</c>, <c>Payments.*</c>,
/// <c>Email.Smtp.*</c>, <c>Security.*</c> and <c>Privacy.RetainInvoiceBillTo</c>, none of which this build
/// seeds, and it needs two tables that do not exist. Writing a guard for keys that cannot be written would
/// be untestable by construction.
/// </para>
/// </summary>
internal sealed class SettingConfiguration : IEntityTypeConfiguration<Setting>
{
    public void Configure(EntityTypeBuilder<Setting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "Settings",
            AppDbContext.OpsSchema,
            table =>
            {
                // Doc 08 §3.8: "1 String, 2 Text/HTML, 3 Int, 4 Decimal, 5 Bool, 6 Json, 7 Secret,
                // 8 MediaAssetRef. CK". Written as a range rather than an IN list because the eight are
                // contiguous and a gap would be a redesign rather than an addition.
                table.HasCheckConstraint(
                    "CK_Settings_ValueType",
                    "[ValueType] >= 1 AND [ValueType] <= 8");

                // Doc 08 §3.8: "CK IsSecret = 1 <=> ValueType = 7". Both directions, which is what the
                // biconditional means and what a single-sided check would miss: a Secret row with
                // IsSecret = 0 would have its value logged in full by the audit trail, and a String row
                // with IsSecret = 1 would hide a value that is not encrypted and cannot be decrypted.
                table.HasCheckConstraint(
                    "CK_Settings_IsSecret",
                    "([IsSecret] = CAST(1 AS bit) AND [ValueType] = 7) "
                    + "OR ([IsSecret] = CAST(0 AS bit) AND [ValueType] <> 7)");

                // Not in doc 08, and it belongs here: RequiredPermission is the column that decides who may
                // write a key, so a blank one is a row with no guard at all. The CHECK is the floor under
                // SettingCatalog — a row inserted by a script or a future migration cannot be ungoverned.
                table.HasCheckConstraint(
                    "CK_Settings_RequiredPermission",
                    "LEN([RequiredPermission]) > 0");
            });

        // The dotted key is the primary key (doc 08 §3.8), so a row is addressed by what it means. The
        // database collation is case-insensitive, which is the behaviour wanted here: `store.logoassetid`
        // and `Store.LogoAssetId` must not be two rows.
        builder.HasKey(setting => setting.Key);

        builder.Property(setting => setting.Key)
            .HasColumnType($"varchar({SettingKeys.KeyMaxLength})")
            .IsRequired();

        builder.Property(setting => setting.GroupKey)
            .HasColumnType($"varchar({SettingKeys.GroupKeyMaxLength})")
            .IsRequired();

        builder.Property(setting => setting.ValueType)
            .HasColumnType("tinyint")
            .IsRequired();

        // Doc 08 §3.8: nvarchar(max), nullable, invariant culture. Mapped explicitly rather than left to
        // convention so the column cannot quietly become nvarchar(450) and start truncating a Json value.
        builder.Property(setting => setting.Value)
            .HasColumnType("nvarchar(max)");

        builder.Property(setting => setting.IsSecret)
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(setting => setting.RequiredPermission)
            .HasColumnType($"varchar({SettingKeys.RequiredPermissionMaxLength})")
            .IsRequired();

        builder.Property(setting => setting.Description)
            .HasMaxLength(SettingKeys.DescriptionMaxLength);

        builder.Property(setting => setting.UpdatedAtUtc);
        builder.Property(setting => setting.UpdatedByUserId);

        // Doc 08 §1.5 [RV]. Unlike sales.Carts, which the document does not mark — two staff users on one
        // settings screen is the ordinary case rather than a race worth ignoring.
        builder.ApplyRowVersion(setting => setting.RowVersion);

        builder.HasIndex(setting => setting.GroupKey).HasDatabaseName("IX_Settings_GroupKey");
    }
}
