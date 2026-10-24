using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InternationalDeliveryTiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Owner decision 2026-10-23: Standard and Express delivery tiers for overseas orders.
            // Three nullable columns added to sales.DeliveryRegions and three to ref.Countries, each with
            // explicit constraint names matching the EF configuration. Nullable with no default: existing
            // rows keep null (express not configured), which the resolver treats as "express not priced",
            // so all existing checkouts continue to work unchanged.

            migrationBuilder.AddColumn<decimal>(
                name: "ExpressFee",
                schema: "sales",
                table: "DeliveryRegions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "ExpressDaysMin",
                schema: "sales",
                table: "DeliveryRegions",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)3);

            migrationBuilder.AddColumn<byte>(
                name: "ExpressDaysMax",
                schema: "sales",
                table: "DeliveryRegions",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)7);

            migrationBuilder.AddCheckConstraint(
                name: "CK_DeliveryRegions_ExpressFee",
                schema: "sales",
                table: "DeliveryRegions",
                sql: "[ExpressFee] IS NULL OR [ExpressFee] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DeliveryRegions_ExpressDays",
                schema: "sales",
                table: "DeliveryRegions",
                sql: "[ExpressDaysMin] <= [ExpressDaysMax]");

            migrationBuilder.AddColumn<decimal>(
                name: "ExpressFeeOverride",
                schema: "ref",
                table: "Countries",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "ExpressDaysMinOverride",
                schema: "ref",
                table: "Countries",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "ExpressDaysMaxOverride",
                schema: "ref",
                table: "Countries",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Countries_ExpressFeeOverride",
                schema: "ref",
                table: "Countries",
                sql: "[ExpressFeeOverride] IS NULL OR [ExpressFeeOverride] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Countries_ExpressDaysOverride",
                schema: "ref",
                table: "Countries",
                sql: "([ExpressDaysMinOverride] IS NULL AND [ExpressDaysMaxOverride] IS NULL) "
                    + "OR ([ExpressDaysMinOverride] IS NOT NULL AND [ExpressDaysMaxOverride] IS NOT NULL "
                    + "AND [ExpressDaysMinOverride] <= [ExpressDaysMaxOverride])");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Countries_ExpressDaysOverride",
                schema: "ref",
                table: "Countries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Countries_ExpressFeeOverride",
                schema: "ref",
                table: "Countries");

            migrationBuilder.DropColumn(
                name: "ExpressDaysMaxOverride",
                schema: "ref",
                table: "Countries");

            migrationBuilder.DropColumn(
                name: "ExpressDaysMinOverride",
                schema: "ref",
                table: "Countries");

            migrationBuilder.DropColumn(
                name: "ExpressFeeOverride",
                schema: "ref",
                table: "Countries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DeliveryRegions_ExpressDays",
                schema: "sales",
                table: "DeliveryRegions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DeliveryRegions_ExpressFee",
                schema: "sales",
                table: "DeliveryRegions");

            migrationBuilder.DropColumn(
                name: "ExpressDaysMax",
                schema: "sales",
                table: "DeliveryRegions");

            migrationBuilder.DropColumn(
                name: "ExpressDaysMin",
                schema: "sales",
                table: "DeliveryRegions");

            migrationBuilder.DropColumn(
                name: "ExpressFee",
                schema: "sales",
                table: "DeliveryRegions");
        }
    }
}
