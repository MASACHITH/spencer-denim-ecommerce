using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductReviews",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    CustomerUserId = table.Column<int>(type: "int", nullable: false),
                    OrderId = table.Column<int>(type: "int", nullable: true),
                    Rating = table.Column<byte>(type: "tinyint", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    IsVerifiedBuyer = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    ModeratedByUserId = table.Column<int>(type: "int", nullable: true),
                    ModeratedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ModerationNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductReviews", x => x.Id);
                    table.CheckConstraint("CK_ProductReviews_DisplayName", "ISNULL(LEN([DisplayName]), 0) > 0");
                    table.CheckConstraint("CK_ProductReviews_Rating", "[Rating] BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_ProductReviews_Status", "[Status] IN (0, 1, 2)");
                    table.CheckConstraint("CK_ProductReviews_Verified", "([IsVerifiedBuyer] = 1 AND [OrderId] IS NOT NULL) OR ([IsVerifiedBuyer] = 0 AND [OrderId] IS NULL)");
                    table.ForeignKey(
                        name: "FK_ProductReviews_Orders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "sales",
                        principalTable: "Orders",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProductReviews_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProductReviews_Users_CustomerUserId",
                        column: x => x.CustomerUserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductReviews_Customer",
                schema: "catalog",
                table: "ProductReviews",
                column: "CustomerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductReviews_Order",
                schema: "catalog",
                table: "ProductReviews",
                column: "OrderId",
                filter: "[OrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProductReviews_Product_Status_Created",
                schema: "catalog",
                table: "ProductReviews",
                columns: new[] { "ProductId", "Status", "CreatedAtUtc", "Id" },
                descending: new[] { false, false, true, true })
                .Annotation("SqlServer:Include", new[] { "Rating" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductReviews_Status_Created",
                schema: "catalog",
                table: "ProductReviews",
                columns: new[] { "Status", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_ProductReviews_Product_Customer",
                schema: "catalog",
                table: "ProductReviews",
                columns: new[] { "ProductId", "CustomerUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductReviews",
                schema: "catalog");
        }
    }
}
