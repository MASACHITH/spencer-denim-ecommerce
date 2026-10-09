using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OpsEmailOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailOutbox",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TemplateCode = table.Column<string>(type: "varchar(50)", nullable: false),
                    DeduplicationKey = table.Column<string>(type: "varchar(150)", nullable: true),
                    TransportOverrideProtected = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ToEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ToName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BodyText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RelatedEntityType = table.Column<string>(type: "varchar(40)", nullable: true),
                    RelatedEntityId = table.Column<string>(type: "varchar(100)", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    AttemptCount = table.Column<short>(type: "smallint", nullable: false),
                    MaxAttempts = table.Column<short>(type: "smallint", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LockedUntilUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailOutbox", x => x.Id);
                    table.CheckConstraint("CK_EmailOutbox_Attempts", "[AttemptCount] >= 0 AND [MaxAttempts] >= 1");
                    table.CheckConstraint("CK_EmailOutbox_Status", "[Status] BETWEEN 0 AND 5");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_Due",
                schema: "ops",
                table: "EmailOutbox",
                column: "NextAttemptAtUtc",
                filter: "[Status] IN (0, 3)");

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_Stuck",
                schema: "ops",
                table: "EmailOutbox",
                column: "LockedUntilUtc",
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_EmailOutbox_Dedup",
                schema: "ops",
                table: "EmailOutbox",
                column: "DeduplicationKey",
                unique: true,
                filter: "[DeduplicationKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailOutbox",
                schema: "ops");
        }
    }
}
