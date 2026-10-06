using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates <c>sales.WishlistItems</c> (doc 08 §3.5, WISH-01) — the last of §3.5's fifteen tables, and
    /// the one <c>20261002035848_SalesCartFoundation</c> set aside for W2-08.
    /// <para>
    /// <b>Nothing in the application inserts a row yet, and that is the design rather than an omission.</b>
    /// <c>CustomerUserId</c> is <c>NOT NULL</c>, exactly as doc 08 §3.5 gives it, and there are no customer
    /// accounts until W3-01 / ACC-01. A guest's wishlist is not stored here at all: doc 08 §1.7 lists it
    /// under "Client-side only (no tables)", doc 02's WISH-01 line says "guests keep it in the browser", and
    /// doc 11 §553 says it again — so the storefront keeps it in <c>localStorage</c> and reads live cards
    /// from <c>/api/wishlist</c>. <b>W3-01 owns the merge</b> that turns that list into rows here, with doc
    /// 08 §3.5's "insert with <c>WHERE NOT EXISTS</c>".
    /// </para>
    /// <para>
    /// <b>Why there is no token column, when <c>Carts</c> has one.</b> A guest cart needs a row, so it needs
    /// an owner that may be NULL plus an unguessable token to find it by, and <c>UX_Carts_Customer</c> has to
    /// be filtered as a result. Giving this table the same shape would have put a guest's wishlist on the
    /// server, which is the opposite of what the scope baseline and §1.7 specify — and would have changed
    /// the shape of the merge W3-01 is going to write. Every row here therefore has a real owner, so
    /// <c>UX_WishlistItems_Customer_Product</c> is a plain unique index and <c>CustomerUserId</c> leads an
    /// unfiltered index without needing an entry in <c>SalesSchemaTests.FilteredLeadingIndexIsEnough</c>.
    /// </para>
    /// <para>
    /// <b>Three foreign keys, all <c>NO ACTION</c></b> (doc 08 §1.6's default): <c>WishlistItems</c> is not on
    /// §1.6's cascade allow-list. A customer is anonymised rather than deleted and §3.1's PDPA removal clears
    /// these rows with a set-based <c>ExecuteDelete</c>; a product or a colourway a shopper has saved is
    /// withdrawn from sale, not deleted, and the shopper is shown it as unavailable with a way to remove it
    /// (doc 11 §575). <c>ProductId</c> and <c>ProductColourId</c> each lead an index of their own (DB7):
    /// <c>ProductId</c> is only the <i>second</i> key of the unique index, and a second key is not a leading
    /// one.
    /// </para>
    /// <para>
    /// <b>No audit columns and no rowversion</b> — doc 08 §3.5 marks this table with neither <c>[A]</c> nor
    /// <c>[RV]</c>, and §3.8 does not audit it. A wishlist row is inserted or deleted, never updated, so
    /// there is no read-then-write for a concurrency token to protect.
    /// </para>
    /// <para>
    /// <c>Down</c> drops this one table and nothing else, so it is reversible on a database that other
    /// migrations have also written to.
    /// </para>
    /// </summary>
    public partial class SalesWishlistItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WishlistItems",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerUserId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    ProductColourId = table.Column<int>(type: "int", nullable: true),
                    AddedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WishlistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WishlistItems_ProductColours_ProductColourId",
                        column: x => x.ProductColourId,
                        principalSchema: "catalog",
                        principalTable: "ProductColours",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WishlistItems_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WishlistItems_Users_CustomerUserId",
                        column: x => x.CustomerUserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_Product",
                schema: "sales",
                table: "WishlistItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_ProductColour",
                schema: "sales",
                table: "WishlistItems",
                column: "ProductColourId");

            migrationBuilder.CreateIndex(
                name: "UX_WishlistItems_Customer_Product",
                schema: "sales",
                table: "WishlistItems",
                columns: new[] { "CustomerUserId", "ProductId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WishlistItems",
                schema: "sales");
        }
    }
}
