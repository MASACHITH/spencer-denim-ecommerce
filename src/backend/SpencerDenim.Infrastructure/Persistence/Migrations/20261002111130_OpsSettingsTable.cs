using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates <c>ops.Settings</c> (doc 08 §3.8; ADM-22, GLB-02) and nothing else.
    /// <para>
    /// <b>Exactly one table.</b> Ten columns — <c>Key</c> <c>varchar(100)</c> as the primary key,
    /// <c>GroupKey</c> <c>varchar(50)</c>, <c>ValueType</c> <c>tinyint</c>, <c>Value</c>
    /// <c>nvarchar(max)</c> NULL, <c>IsSecret</c> <c>bit</c> defaulting to 0, <c>RequiredPermission</c>
    /// <c>varchar(64)</c>, <c>Description</c> <c>nvarchar(300)</c> NULL, <c>UpdatedAtUtc</c>
    /// <c>datetime2(3)</c> NULL, <c>UpdatedByUserId</c> <c>int</c> NULL and <c>RowVersion</c>
    /// <c>rowversion</c>. Three CHECK constraints: <c>CK_Settings_ValueType</c> (1–8),
    /// <c>CK_Settings_IsSecret</c> (<c>IsSecret = 1</c> if and only if <c>ValueType = 7</c>) and
    /// <c>CK_Settings_RequiredPermission</c> (non-empty). One non-unique index,
    /// <c>IX_Settings_GroupKey</c>. No foreign key, no seed data, and <b>no change to any other
    /// table</b> — the <c>media.MediaUsages</c> rows an ADM-22 write creates fit the table W1-04
    /// already built, because doc 08 §3.3 specified <c>OwnerType = Setting</c> from the start.
    /// </para>
    /// <para>
    /// <b>The whole value-type range is allowed although five keys are seeded and all of them are types 1
    /// and 8.</b> That is deliberate: <c>Secret</c> (7) arrives with PayPal's client secret and the SMTP
    /// password, <c>Decimal</c> (4) with the FX buffer, both in issue #53 and W3. A constraint written to
    /// today's keys would be a constraint the next task has to widen with a migration of its own, under
    /// its own deadline. Those tasks add <i>rows</i>.
    /// </para>
    /// <para>
    /// <b>The <c>ops</c> schema already exists</b> — the initial migration created it for
    /// <c>__EFMigrationsHistory</c> and <c>ops.AuditLogs</c> — so there is no <c>EnsureSchema</c> here and
    /// nothing in this migration can collide with <c>sales</c> (W2-09, an hour earlier) or with issue
    /// #53's tables.
    /// </para>
    /// <para><c>Down</c> drops the table. No data is migrated in either direction.</para>
    /// </summary>
    public partial class OpsSettingsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Settings",
                schema: "ops",
                columns: table => new
                {
                    Key = table.Column<string>(type: "varchar(100)", nullable: false),
                    GroupKey = table.Column<string>(type: "varchar(50)", nullable: false),
                    ValueType = table.Column<byte>(type: "tinyint", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsSecret = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    RequiredPermission = table.Column<string>(type: "varchar(64)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                    table.CheckConstraint("CK_Settings_IsSecret", "([IsSecret] = CAST(1 AS bit) AND [ValueType] = 7) OR ([IsSecret] = CAST(0 AS bit) AND [ValueType] <> 7)");
                    table.CheckConstraint("CK_Settings_RequiredPermission", "LEN([RequiredPermission]) > 0");
                    table.CheckConstraint("CK_Settings_ValueType", "[ValueType] >= 1 AND [ValueType] <= 8");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Settings_GroupKey",
                schema: "ops",
                table: "Settings",
                column: "GroupKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Settings",
                schema: "ops");
        }
    }
}
