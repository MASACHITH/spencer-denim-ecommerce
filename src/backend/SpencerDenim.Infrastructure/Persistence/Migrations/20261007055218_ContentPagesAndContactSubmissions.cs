using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContentPagesAndContactSubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContactSubmissions",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Phone = table.Column<string>(type: "varchar(20)", nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    OrderNumber = table.Column<string>(type: "varchar(20)", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    HandledByUserId = table.Column<int>(type: "int", nullable: true),
                    HandledAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    IpAddress = table.Column<string>(type: "varchar(45)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactSubmissions", x => x.Id);
                    table.CheckConstraint("CK_ContactSubmissions_Status", "[Status] BETWEEN 0 AND 3");
                });

            migrationBuilder.CreateTable(
                name: "ContentPages",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SystemKey = table.Column<string>(type: "varchar(30)", nullable: true),
                    Slug = table.Column<string>(type: "varchar(100)", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SeoTitle = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    SeoDescription = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    IsPublished = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentPages", x => x.Id);
                    table.CheckConstraint("CK_ContentPages_Slug", "[Slug] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^a-z0-9-]%' AND [Slug] NOT LIKE '-%' AND [Slug] NOT LIKE '%-' AND [Slug] NOT LIKE '%--%' AND DATALENGTH([Slug]) > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContactSubmissions_CreatedAtUtc",
                schema: "content",
                table: "ContactSubmissions",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ContactSubmissions_Status_Created",
                schema: "content",
                table: "ContactSubmissions",
                columns: new[] { "Status", "CreatedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ContentPages_Published_Title",
                schema: "content",
                table: "ContentPages",
                columns: new[] { "IsPublished", "Title" });

            migrationBuilder.CreateIndex(
                name: "UX_ContentPages_Slug",
                schema: "content",
                table: "ContentPages",
                column: "Slug",
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_ContentPages_SystemKey",
                schema: "content",
                table: "ContentPages",
                column: "SystemKey",
                unique: true,
                filter: "[IsDeleted] = 0 AND [SystemKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContactSubmissions",
                schema: "content");

            migrationBuilder.DropTable(
                name: "ContentPages",
                schema: "content");
        }
    }
}
