using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>marketing.PromoCodes</c> (doc 08 §3.6; ADM-13, W3-05) — one table — and the foreign key that
    /// <c>sales.Orders.PromoCodeId</c> has been carrying an index for without a constraint.
    /// <para>
    /// <b>The column was already there and already null on every order.</b> The orders migration
    /// (<c>20261004073717_SalesOrdersAndPayments</c>) lists it among "four of doc 08's foreign keys …
    /// columns without constraints", with
    /// <c>IX_Orders_Promo_Email</c> built "as if the constraint existed, so adding it later is one
    /// <c>ALTER TABLE</c> and no index build". That is what the <c>AddForeignKey</c> below is: no index is
    /// created by this migration for <c>Orders</c>, because the filtered composite index already leads on
    /// <c>PromoCodeId</c>. <c>NO ACTION</c>, like every other relationship on <c>Orders</c> — a promo code
    /// is soft-deleted and never removed, so the rule can never refuse an admin a delete.
    /// </para>
    /// <para>
    /// <b>Nothing is seeded.</b> Doc 02 line 167 replaces the old demo's hard-coded <c>TUASL10</c> and
    /// <c>FREESHIP</c> with admin-managed codes; a plausible-looking code in the database would be a
    /// discount the client never agreed to, which is the same rule the invoice follows for an unset
    /// address and the bank-instructions email for unset account details.
    /// </para>
    /// <para>
    /// <b><c>marketing.SaleCampaigns</c> and <c>marketing.SaleCampaignTargets</c> are deliberately not
    /// here</b> (ADM-14, doc 02 line 119). They are a separate feature with their own
    /// <c>RefreshCampaignPricing</c> job, and <c>OrderLines.SaleCampaignId</c> therefore stays a column
    /// without a constraint — the last of W3-03's four.
    /// </para>
    /// <para>
    /// <b>The two limits are not check constraints, and doc 08 §3.6 (DB8) is explicit about why.</b>
    /// <c>UsageCount</c> carries <c>&gt;= 0</c> only: the total limit lives in the conditional UPDATE at
    /// placement, and a constraint of <c>UsageCount &lt;= TotalUsageLimit</c> would make §4.2's
    /// "Expired → Paid: +1 unconditionally, limit ignored" unwritable. The per-customer limit is counted
    /// from <c>sales.Orders</c> under an app lock and has no column here at all.
    /// </para>
    /// </summary>
    /// <inheritdoc />
    public partial class MarketingPromoCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PromoCodes",
                schema: "marketing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "varchar(40)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    DiscountType = table.Column<byte>(type: "tinyint", nullable: false),
                    DiscountValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    MaxDiscountAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    MinSpendAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ExcludeSaleItems = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    TotalUsageLimit = table.Column<int>(type: "int", nullable: true),
                    PerCustomerLimit = table.Column<int>(type: "int", nullable: true),
                    UsageCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
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
                    table.PrimaryKey("PK_PromoCodes", x => x.Id);
                    table.CheckConstraint("CK_PromoCodes_DiscountType", "[DiscountType] IN (1, 2, 3)");
                    table.CheckConstraint("CK_PromoCodes_DiscountValue", "([DiscountType] = 1 AND [DiscountValue] > 0 AND [DiscountValue] <= 100) OR ([DiscountType] = 2 AND [DiscountValue] > 0) OR ([DiscountType] = 3 AND [DiscountValue] IS NULL)");
                    table.CheckConstraint("CK_PromoCodes_MaxDiscountAmount", "[MaxDiscountAmount] IS NULL OR [MaxDiscountAmount] > 0");
                    table.CheckConstraint("CK_PromoCodes_MinSpendAmount", "[MinSpendAmount] IS NULL OR [MinSpendAmount] > 0");
                    table.CheckConstraint("CK_PromoCodes_PerCustomerLimit", "[PerCustomerLimit] IS NULL OR [PerCustomerLimit] >= 1");
                    table.CheckConstraint("CK_PromoCodes_TotalUsageLimit", "[TotalUsageLimit] IS NULL OR [TotalUsageLimit] >= 1");
                    table.CheckConstraint("CK_PromoCodes_UsageCount", "[UsageCount] >= 0");
                    table.CheckConstraint("CK_PromoCodes_Window", "[EndsAtUtc] IS NULL OR [EndsAtUtc] > [StartsAtUtc]");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PromoCodes_Active",
                schema: "marketing",
                table: "PromoCodes",
                columns: new[] { "IsActive", "StartsAtUtc", "EndsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_PromoCodes_Code",
                schema: "marketing",
                table: "PromoCodes",
                column: "Code",
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_PromoCodes_PromoCodeId",
                schema: "sales",
                table: "Orders",
                column: "PromoCodeId",
                principalSchema: "marketing",
                principalTable: "PromoCodes",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_PromoCodes_PromoCodeId",
                schema: "sales",
                table: "Orders");

            migrationBuilder.DropTable(
                name: "PromoCodes",
                schema: "marketing");
        }
    }
}
