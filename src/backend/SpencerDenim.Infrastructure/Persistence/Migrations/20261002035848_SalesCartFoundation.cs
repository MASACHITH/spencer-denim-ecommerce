using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates the <c>sales</c> schema with <b>exactly two tables</b>: <c>Carts</c> and <c>CartItems</c>
    /// (doc 08 §3.5, CART-01/02).
    /// <para>
    /// <b>What it deliberately does not create, and who owns it.</b> <c>Orders</c>, <c>OrderLines</c>,
    /// <c>OrderStatusHistory</c>, <c>Payments</c>, <c>PaymentEvents</c>, <c>Refunds</c>,
    /// <c>NumberSeries</c>, <c>DeliveryMethods</c>, <c>DeliveryMethodZoneRates</c>,
    /// <c>PickupLocations</c>, <c>Couriers</c> and <c>PaymentProviders</c> all belong to #53;
    /// <c>WishlistItems</c> belongs to W2-08. The schema is created here only because the bag needed it
    /// first and the project allows one migration in flight at a time, so #53's migration builds on this
    /// one instead of creating the schema itself.
    /// </para>
    /// <para>
    /// <b>Three columns of doc 08's <c>Carts</c> are not here.</b> <c>PromoCodeText</c> (ADM-13 —
    /// <c>marketing.PromoCodes</c> does not exist, so §3.5's "revalidated on every read" is impossible),
    /// <c>DistrictId</c> (<c>ref.Districts</c> is not created yet at all) and <c>DeliveryMethodId</c>
    /// (<c>sales.DeliveryMethods</c> is #53's). Two of the three could not carry their foreign key, and
    /// none is read or written by CART-01/02. Whoever builds the feature adds the column with its
    /// constraint in one piece.
    /// </para>
    /// <para>
    /// <b>No rowversion on either table</b> (doc 08 §1.5: not on hot rows). Concurrency is the item-level
    /// upsert of §3.5 (DB22), which <c>UX_CartItems_Cart_Variant</c> is what makes safe.
    /// </para>
    /// <para>
    /// <c>Down</c> drops the two tables and leaves the empty <c>sales</c> schema behind, which is EF's
    /// normal behaviour for <c>EnsureSchema</c> and the safe behaviour here: by the time anyone rolls
    /// back, #53's tables may be in that schema.
    /// </para>
    /// </summary>
    public partial class SalesCartFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sales");

            migrationBuilder.CreateTable(
                name: "Carts",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Token = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "newid()"),
                    CustomerUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    LastActivityAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Carts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Carts_Users_CustomerUserId",
                        column: x => x.CustomerUserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CartItems",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CartId = table.Column<int>(type: "int", nullable: false),
                    VariantId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<short>(type: "smallint", nullable: false),
                    UnitPriceAtAdd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CartItems", x => x.Id);
                    table.CheckConstraint("CK_CartItems_Quantity", "[Quantity] BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_CartItems_Carts_CartId",
                        column: x => x.CartId,
                        principalSchema: "sales",
                        principalTable: "Carts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CartItems_ProductVariants_VariantId",
                        column: x => x.VariantId,
                        principalSchema: "catalog",
                        principalTable: "ProductVariants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CartItems_Variant",
                schema: "sales",
                table: "CartItems",
                column: "VariantId");

            migrationBuilder.CreateIndex(
                name: "UX_CartItems_Cart_Variant",
                schema: "sales",
                table: "CartItems",
                columns: new[] { "CartId", "VariantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Carts_LastActivity",
                schema: "sales",
                table: "Carts",
                column: "LastActivityAtUtc")
                .Annotation("SqlServer:Include", new[] { "CustomerUserId" });

            migrationBuilder.CreateIndex(
                name: "UX_Carts_Customer",
                schema: "sales",
                table: "Carts",
                column: "CustomerUserId",
                unique: true,
                filter: "[CustomerUserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Carts_Token",
                schema: "sales",
                table: "Carts",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CartItems",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "Carts",
                schema: "sales");
        }
    }
}
