using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketingSaleCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SaleCampaigns",
                schema: "marketing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DiscountType = table.Column<byte>(type: "tinyint", nullable: false),
                    DiscountValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    BannerAssetId = table.Column<int>(type: "int", nullable: true),
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
                    table.PrimaryKey("PK_SaleCampaigns", x => x.Id);
                    table.CheckConstraint("CK_SaleCampaigns_DiscountType", "[DiscountType] IN (1, 2)");
                    table.CheckConstraint("CK_SaleCampaigns_DiscountValue", "([DiscountType] = 1 AND [DiscountValue] > 0 AND [DiscountValue] <= 90) OR ([DiscountType] = 2 AND [DiscountValue] > 0)");
                    table.CheckConstraint("CK_SaleCampaigns_Window", "[EndsAtUtc] > [StartsAtUtc]");
                    table.ForeignKey(
                        name: "FK_SaleCampaigns_MediaAssets_BannerAssetId",
                        column: x => x.BannerAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SaleCampaignTargets",
                schema: "marketing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SaleCampaignId = table.Column<int>(type: "int", nullable: false),
                    TargetType = table.Column<byte>(type: "tinyint", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: true),
                    CategoryId = table.Column<int>(type: "int", nullable: true),
                    ProductId = table.Column<int>(type: "int", nullable: true),
                    IncludeSubcategories = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleCampaignTargets", x => x.Id);
                    table.CheckConstraint("CK_SaleCampaignTargets_Target", "([TargetType] = 1 AND [DepartmentId] IS NOT NULL AND [CategoryId] IS NULL AND [ProductId] IS NULL) OR ([TargetType] = 2 AND [CategoryId] IS NOT NULL AND [DepartmentId] IS NULL AND [ProductId] IS NULL) OR ([TargetType] = 3 AND [ProductId] IS NOT NULL AND [DepartmentId] IS NULL AND [CategoryId] IS NULL)");
                    table.CheckConstraint("CK_SaleCampaignTargets_TargetType", "[TargetType] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_SaleCampaignTargets_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "catalog",
                        principalTable: "Categories",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SaleCampaignTargets_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "catalog",
                        principalTable: "Departments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SaleCampaignTargets_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SaleCampaignTargets_SaleCampaigns_SaleCampaignId",
                        column: x => x.SaleCampaignId,
                        principalSchema: "marketing",
                        principalTable: "SaleCampaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SaleCampaigns_BannerAssetId",
                schema: "marketing",
                table: "SaleCampaigns",
                column: "BannerAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleCampaigns_Window",
                schema: "marketing",
                table: "SaleCampaigns",
                columns: new[] { "StartsAtUtc", "EndsAtUtc" },
                filter: "[IsActive] = 1 AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_SaleCampaignTargets_CategoryId",
                schema: "marketing",
                table: "SaleCampaignTargets",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleCampaignTargets_DepartmentId",
                schema: "marketing",
                table: "SaleCampaignTargets",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleCampaignTargets_ProductId",
                schema: "marketing",
                table: "SaleCampaignTargets",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleCampaignTargets_SaleCampaignId",
                schema: "marketing",
                table: "SaleCampaignTargets",
                column: "SaleCampaignId");

            migrationBuilder.AddForeignKey(
                name: "FK_OrderLines_SaleCampaigns_SaleCampaignId",
                schema: "sales",
                table: "OrderLines",
                column: "SaleCampaignId",
                principalSchema: "marketing",
                principalTable: "SaleCampaigns",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OrderLines_SaleCampaigns_SaleCampaignId",
                schema: "sales",
                table: "OrderLines");

            migrationBuilder.DropTable(
                name: "SaleCampaignTargets",
                schema: "marketing");

            migrationBuilder.DropTable(
                name: "SaleCampaigns",
                schema: "marketing");
        }
    }
}
