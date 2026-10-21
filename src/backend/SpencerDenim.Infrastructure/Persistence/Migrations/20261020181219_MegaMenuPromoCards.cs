using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MegaMenuPromoCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MegaMenuPromoCards",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Subtitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ButtonLabel = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    LinkUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ImageAssetId = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MegaMenuPromoCards", x => x.Id);
                    table.CheckConstraint("CK_MegaMenuPromoCards_ActiveImage", "[IsActive] = 0 OR [ImageAssetId] IS NOT NULL");
                    table.CheckConstraint("CK_MegaMenuPromoCards_ButtonLabel", "LEN([ButtonLabel]) > 0");
                    table.CheckConstraint("CK_MegaMenuPromoCards_LinkUrl", "LEN([LinkUrl]) > 0");
                    table.CheckConstraint("CK_MegaMenuPromoCards_Schedule", "[StartsAtUtc] IS NULL OR [EndsAtUtc] IS NULL OR [EndsAtUtc] > [StartsAtUtc]");
                    table.CheckConstraint("CK_MegaMenuPromoCards_Title", "LEN([Title]) > 0");
                    table.ForeignKey(
                        name: "FK_MegaMenuPromoCards_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "catalog",
                        principalTable: "Departments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MegaMenuPromoCards_MediaAssets_ImageAssetId",
                        column: x => x.ImageAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_MegaMenuPromoCards_Department_SortOrder",
                schema: "content",
                table: "MegaMenuPromoCards",
                columns: new[] { "DepartmentId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_MegaMenuPromoCards_ImageAssetId",
                schema: "content",
                table: "MegaMenuPromoCards",
                column: "ImageAssetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MegaMenuPromoCards",
                schema: "content");
        }
    }
}
