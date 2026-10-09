using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OpsAdminAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminAlerts",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AlertType = table.Column<string>(type: "varchar(40)", nullable: false),
                    Severity = table.Column<byte>(type: "tinyint", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RelatedEntityType = table.Column<string>(type: "varchar(40)", nullable: true),
                    RelatedEntityId = table.Column<string>(type: "varchar(100)", nullable: true),
                    AuditLogId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    AcknowledgedByUserId = table.Column<int>(type: "int", nullable: true),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    AcknowledgementNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAlerts", x => x.Id);
                    table.CheckConstraint("CK_AdminAlerts_Acknowledged", "([AcknowledgedAtUtc] IS NULL AND [AcknowledgedByUserId] IS NULL AND [AcknowledgementNote] IS NULL) OR ([AcknowledgedAtUtc] IS NOT NULL AND [AcknowledgedByUserId] IS NOT NULL)");
                    table.CheckConstraint("CK_AdminAlerts_DetailsJson", "[DetailsJson] IS NULL OR ISJSON([DetailsJson]) = 1");
                    table.CheckConstraint("CK_AdminAlerts_Severity", "[Severity] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_AdminAlerts_AcknowledgedBy_Users",
                        column: x => x.AcknowledgedByUserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAlerts_AcknowledgedBy",
                schema: "ops",
                table: "AdminAlerts",
                column: "AcknowledgedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAlerts_Open",
                schema: "ops",
                table: "AdminAlerts",
                column: "CreatedAtUtc",
                descending: new bool[0],
                filter: "[AcknowledgedAtUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminAlerts",
                schema: "ops");
        }
    }
}
