using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InternationalDeliveryPayPalOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Owner decision 2026-10-21: Sri Lanka plus worldwide delivery. Hand-ordered rather than as generated,
            // for two reasons a reader must not undo:
            //   1. Every existing order and saved address is backfilled as Sri Lanka (LK) *before* the country
            //      columns become NOT NULL and before their foreign keys exist, so ref.Countries gets its LK row
            //      here — the seeder adds the other 248 at startup, insert-missing, and never touches LK again.
            //   2. SQL Server refuses ALTER COLUMN on a column a CHECK, an index or a foreign key depends on
            //      (Msg 5074), so the district columns' dependents are dropped, the columns made nullable, and
            //      the dependents put back exactly as they were.
            // Index and constraint names are explicit. String columns take the database collation
            // (Latin1_General_100_CI_AS_SC), as every other table does (AppDbContext's remarks).

            migrationBuilder.CreateTable(
                name: "DeliveryRegions",
                schema: "sales",
                columns: table => new
                {
                    Code = table.Column<string>(type: "varchar(20)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Fee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    EstimatedDaysMin = table.Column<byte>(type: "tinyint", nullable: false),
                    EstimatedDaysMax = table.Column<byte>(type: "tinyint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryRegions", x => x.Code);
                    table.CheckConstraint("CK_DeliveryRegions_EstimatedDays", "[EstimatedDaysMin] <= [EstimatedDaysMax]");
                    table.CheckConstraint("CK_DeliveryRegions_Fee", "[Fee] IS NULL OR [Fee] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "Countries",
                schema: "ref",
                columns: table => new
                {
                    Code = table.Column<string>(type: "char(2)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    RegionCode = table.Column<string>(type: "varchar(20)", nullable: true),
                    PostalCodeRequired = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    RegionRequired = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    IsShippingEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    DeliveryFeeOverride = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    EstimatedDaysMinOverride = table.Column<byte>(type: "tinyint", nullable: true),
                    EstimatedDaysMaxOverride = table.Column<byte>(type: "tinyint", nullable: true),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Countries", x => x.Code);
                    table.CheckConstraint("CK_Countries_Code", "[Code] COLLATE Latin1_General_100_BIN2 LIKE '[A-Z][A-Z]'");
                    table.CheckConstraint("CK_Countries_DaysOverride", "([EstimatedDaysMinOverride] IS NULL AND [EstimatedDaysMaxOverride] IS NULL) OR ([EstimatedDaysMinOverride] IS NOT NULL AND [EstimatedDaysMaxOverride] IS NOT NULL AND [EstimatedDaysMinOverride] <= [EstimatedDaysMaxOverride])");
                    table.CheckConstraint("CK_Countries_DeliveryFeeOverride", "[DeliveryFeeOverride] IS NULL OR [DeliveryFeeOverride] >= 0");
                    table.CheckConstraint("CK_Countries_Region", "([Code] = 'LK' AND [RegionCode] IS NULL) OR ([Code] <> 'LK' AND [RegionCode] IS NOT NULL)");
                    table.CheckConstraint("CK_Countries_SriLankaEnabled", "[Code] <> 'LK' OR [IsShippingEnabled] = 1");
                    table.ForeignKey(
                        name: "FK_Countries_DeliveryRegions_RegionCode",
                        column: x => x.RegionCode,
                        principalSchema: "sales",
                        principalTable: "DeliveryRegions",
                        principalColumn: "Code");
                });

            migrationBuilder.Sql(
                """
                INSERT INTO [ref].[Countries] ([Code], [Name], [RegionCode], [PostalCodeRequired], [RegionRequired], [IsShippingEnabled], [SortOrder])
                SELECT 'LK', N'Sri Lanka', NULL, 0, 0, 1, 0
                WHERE NOT EXISTS (SELECT 1 FROM [ref].[Countries] WHERE [Code] = 'LK');
                """);

            // The district columns become nullable: an overseas order has no district or zone.
            migrationBuilder.DropIndex(name: "IX_Orders_Paid", schema: "sales", table: "Orders");
            migrationBuilder.DropIndex(name: "IX_Orders_DeliveryDistrictId", schema: "sales", table: "Orders");
            migrationBuilder.DropForeignKey(name: "FK_Orders_Districts_DeliveryDistrictId", schema: "sales", table: "Orders");
            migrationBuilder.DropCheckConstraint(name: "CK_Orders_DeliveryZone", schema: "sales", table: "Orders");

            migrationBuilder.AlterColumn<byte>(
                name: "DeliveryZone",
                schema: "sales",
                table: "Orders",
                type: "tinyint",
                nullable: true,
                oldClrType: typeof(byte),
                oldType: "tinyint");

            migrationBuilder.AlterColumn<string>(
                name: "DeliveryDistrictName",
                schema: "sales",
                table: "Orders",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(60)",
                oldMaxLength: 60);

            migrationBuilder.AlterColumn<int>(
                name: "DeliveryDistrictId",
                schema: "sales",
                table: "Orders",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_DeliveryZone",
                schema: "sales",
                table: "Orders",
                sql: "[DeliveryZone] BETWEEN 1 AND 3");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_DeliveryDistrictId",
                schema: "sales",
                table: "Orders",
                column: "DeliveryDistrictId");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Districts_DeliveryDistrictId",
                schema: "sales",
                table: "Orders",
                column: "DeliveryDistrictId",
                principalSchema: "ref",
                principalTable: "Districts",
                principalColumn: "Id");

            // The order's country: added nullable, backfilled as Sri Lanka, then NOT NULL.
            migrationBuilder.AddColumn<string>(
                name: "DeliveryCountryCode",
                schema: "sales",
                table: "Orders",
                type: "char(2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryCountryName",
                schema: "sales",
                table: "Orders",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ship_Region",
                schema: "sales",
                table: "Orders",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE [sales].[Orders]
                   SET [DeliveryCountryCode] = 'LK', [DeliveryCountryName] = N'Sri Lanka'
                 WHERE [DeliveryCountryCode] IS NULL OR [DeliveryCountryName] IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "DeliveryCountryCode",
                schema: "sales",
                table: "Orders",
                type: "char(2)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "char(2)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "DeliveryCountryName",
                schema: "sales",
                table: "Orders",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(80)",
                oldMaxLength: 80,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_DeliveryCountryCode",
                schema: "sales",
                table: "Orders",
                column: "DeliveryCountryCode");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Paid",
                schema: "sales",
                table: "Orders",
                column: "PaidAtUtc",
                filter: "[PaidAtUtc] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "GrandTotal", "DeliveryDistrictId", "DeliveryCountryCode", "PaymentProviderCode", "CustomerUserId", "EmailNormalized", "Status" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_Destination",
                schema: "sales",
                table: "Orders",
                sql: "([DeliveryCountryCode] = 'LK' AND [DeliveryDistrictId] IS NOT NULL AND [DeliveryDistrictName] IS NOT NULL AND [DeliveryZone] IS NOT NULL AND [DeliveryMethodCode] <> 'INTERNATIONAL') OR ([DeliveryCountryCode] <> 'LK' AND [DeliveryDistrictId] IS NULL AND [DeliveryDistrictName] IS NULL AND [DeliveryZone] IS NULL AND [DeliveryMethodCode] = 'INTERNATIONAL')");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Countries_DeliveryCountryCode",
                schema: "sales",
                table: "Orders",
                column: "DeliveryCountryCode",
                principalSchema: "ref",
                principalTable: "Countries",
                principalColumn: "Code");

            // The saved address's country, the same way; its district becomes nullable.
            migrationBuilder.DropIndex(name: "IX_CustomerAddresses_District", schema: "identity", table: "CustomerAddresses");
            migrationBuilder.DropForeignKey(name: "FK_CustomerAddresses_Districts_DistrictId", schema: "identity", table: "CustomerAddresses");

            migrationBuilder.AlterColumn<int>(
                name: "DistrictId",
                schema: "identity",
                table: "CustomerAddresses",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAddresses_District",
                schema: "identity",
                table: "CustomerAddresses",
                column: "DistrictId");

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerAddresses_Districts_DistrictId",
                schema: "identity",
                table: "CustomerAddresses",
                column: "DistrictId",
                principalSchema: "ref",
                principalTable: "Districts",
                principalColumn: "Id");

            migrationBuilder.AddColumn<string>(
                name: "CountryCode",
                schema: "identity",
                table: "CustomerAddresses",
                type: "char(2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                schema: "identity",
                table: "CustomerAddresses",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE [identity].[CustomerAddresses] SET [CountryCode] = 'LK' WHERE [CountryCode] IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "CountryCode",
                schema: "identity",
                table: "CustomerAddresses",
                type: "char(2)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "char(2)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAddresses_CountryCode",
                schema: "identity",
                table: "CustomerAddresses",
                column: "CountryCode");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustomerAddresses_Destination",
                schema: "identity",
                table: "CustomerAddresses",
                sql: "([CountryCode] = 'LK' AND [DistrictId] IS NOT NULL) OR ([CountryCode] <> 'LK' AND [DistrictId] IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerAddresses_Countries_CountryCode",
                schema: "identity",
                table: "CustomerAddresses",
                column: "CountryCode",
                principalSchema: "ref",
                principalTable: "Countries",
                principalColumn: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_Countries_RegionCode",
                schema: "ref",
                table: "Countries",
                column: "RegionCode");

            migrationBuilder.CreateIndex(
                name: "UX_Countries_Name",
                schema: "ref",
                table: "Countries",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DeliveryRegions_Name",
                schema: "sales",
                table: "DeliveryRegions",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuses to run once an overseas order or address exists: the district columns cannot become NOT
            // NULL again without inventing a Sri Lankan district for a parcel that went abroad.
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM [sales].[Orders] WHERE [DeliveryCountryCode] <> 'LK')
                   OR EXISTS (SELECT 1 FROM [identity].[CustomerAddresses] WHERE [CountryCode] <> 'LK')
                    THROW 50001, 'Overseas orders or addresses exist; this migration cannot be reverted without losing them.', 1;
                """);

            migrationBuilder.DropForeignKey(name: "FK_CustomerAddresses_Countries_CountryCode", schema: "identity", table: "CustomerAddresses");
            migrationBuilder.DropForeignKey(name: "FK_Orders_Countries_DeliveryCountryCode", schema: "sales", table: "Orders");
            migrationBuilder.DropCheckConstraint(name: "CK_CustomerAddresses_Destination", schema: "identity", table: "CustomerAddresses");
            migrationBuilder.DropCheckConstraint(name: "CK_Orders_Destination", schema: "sales", table: "Orders");
            migrationBuilder.DropIndex(name: "IX_CustomerAddresses_CountryCode", schema: "identity", table: "CustomerAddresses");
            migrationBuilder.DropIndex(name: "IX_Orders_DeliveryCountryCode", schema: "sales", table: "Orders");
            migrationBuilder.DropIndex(name: "IX_Orders_Paid", schema: "sales", table: "Orders");

            migrationBuilder.DropTable(name: "Countries", schema: "ref");
            migrationBuilder.DropTable(name: "DeliveryRegions", schema: "sales");

            migrationBuilder.DropColumn(name: "DeliveryCountryCode", schema: "sales", table: "Orders");
            migrationBuilder.DropColumn(name: "DeliveryCountryName", schema: "sales", table: "Orders");
            migrationBuilder.DropColumn(name: "Ship_Region", schema: "sales", table: "Orders");
            migrationBuilder.DropColumn(name: "CountryCode", schema: "identity", table: "CustomerAddresses");
            migrationBuilder.DropColumn(name: "Region", schema: "identity", table: "CustomerAddresses");

            migrationBuilder.DropIndex(name: "IX_Orders_DeliveryDistrictId", schema: "sales", table: "Orders");
            migrationBuilder.DropForeignKey(name: "FK_Orders_Districts_DeliveryDistrictId", schema: "sales", table: "Orders");
            migrationBuilder.DropCheckConstraint(name: "CK_Orders_DeliveryZone", schema: "sales", table: "Orders");
            migrationBuilder.DropIndex(name: "IX_CustomerAddresses_District", schema: "identity", table: "CustomerAddresses");
            migrationBuilder.DropForeignKey(name: "FK_CustomerAddresses_Districts_DistrictId", schema: "identity", table: "CustomerAddresses");

            migrationBuilder.AlterColumn<byte>(
                name: "DeliveryZone",
                schema: "sales",
                table: "Orders",
                type: "tinyint",
                nullable: false,
                oldClrType: typeof(byte),
                oldType: "tinyint",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "DeliveryDistrictName",
                schema: "sales",
                table: "Orders",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(60)",
                oldMaxLength: 60,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "DeliveryDistrictId",
                schema: "sales",
                table: "Orders",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "DistrictId",
                schema: "identity",
                table: "CustomerAddresses",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_DeliveryZone",
                schema: "sales",
                table: "Orders",
                sql: "[DeliveryZone] BETWEEN 1 AND 3");

            migrationBuilder.CreateIndex(name: "IX_Orders_DeliveryDistrictId", schema: "sales", table: "Orders", column: "DeliveryDistrictId");
            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Districts_DeliveryDistrictId",
                schema: "sales",
                table: "Orders",
                column: "DeliveryDistrictId",
                principalSchema: "ref",
                principalTable: "Districts",
                principalColumn: "Id");

            migrationBuilder.CreateIndex(name: "IX_CustomerAddresses_District", schema: "identity", table: "CustomerAddresses", column: "DistrictId");
            migrationBuilder.AddForeignKey(
                name: "FK_CustomerAddresses_Districts_DistrictId",
                schema: "identity",
                table: "CustomerAddresses",
                column: "DistrictId",
                principalSchema: "ref",
                principalTable: "Districts",
                principalColumn: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Paid",
                schema: "sales",
                table: "Orders",
                column: "PaidAtUtc",
                filter: "[PaidAtUtc] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "GrandTotal", "DeliveryDistrictId", "PaymentProviderCode", "CustomerUserId", "EmailNormalized", "Status" });
        }
    }
}
