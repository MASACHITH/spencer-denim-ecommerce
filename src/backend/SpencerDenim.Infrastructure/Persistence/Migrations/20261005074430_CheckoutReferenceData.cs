using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// W3-02 (CHK-01..04): <c>ref.Provinces</c> and <c>ref.Districts</c> (doc 08 §3.1), plus the two
    /// foreign keys W3-03 had to leave as bare columns because this table did not exist.
    /// <para>
    /// <b>Why a checkout task creates reference data.</b> Doc 08 §3.1 heads both tables "GLB-06, CHK-01",
    /// and <c>sales.Orders.DeliveryDistrictId</c> is <c>NOT NULL</c> with <c>Orders.DeliveryZone</c>
    /// derived from the district's zone. No order row can be written until these twenty-five rows exist,
    /// so checkout cannot be built without them. W3-03's journal (decision 1) left the columns indexed and
    /// unconstrained on purpose, so that whichever task created the table would add the constraints in the
    /// same change; its <c>SalesSchemaTests</c> asserted the absence, which is why that assertion has been
    /// narrowed to the two columns still waiting on <c>marketing</c>.
    /// </para>
    /// <para>
    /// <b>No rows are inserted here.</b> The twenty-five districts and the nine provinces arrive through
    /// <c>SalesReferenceDataSeeder</c>, not <c>HasData</c>, because <c>Districts.DeliveryZone</c> is
    /// admin-editable (doc 08 §3.1) and the zone map is explicitly unconfirmed (D8) — an EF seed row would
    /// re-apply this task's guess over the client's decision. See that seeder for the full argument.
    /// </para>
    /// <para>
    /// <b><c>identity.Users.DefaultDistrictId</c> keeps its deferred constraint</b> and is not touched
    /// here. It has no index, so adding the foreign key would mean a new index on the busiest table in the
    /// schema for a column that no code reads — there is no customer authentication yet (ACC-01). GLB-06 or
    /// W3-01 owes both together. See <c>AppUser.DefaultDistrictId</c>.
    /// </para>
    /// <para>
    /// <b>Regenerated after rebasing onto <c>7566c1c</c></b>, which brought in #104's
    /// <c>20261005063301_OpsSettingsTable</c>. The first attempt was stamped 05:12:59 and would have sorted
    /// <i>before</i> a migration already on <c>main</c>, with a designer snapshot built against a model
    /// that had no <c>ops.Settings</c> in it. The schema operations below are byte-identical to that first
    /// attempt; only the timestamp and the snapshot it was diffed against changed.
    /// </para>
    /// </summary>
    public partial class CheckoutReferenceData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ref");

            migrationBuilder.CreateTable(
                name: "Provinces",
                schema: "ref",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "varchar(6)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Provinces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Districts",
                schema: "ref",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ProvinceId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "varchar(6)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    DeliveryZone = table.Column<byte>(type: "tinyint", nullable: false),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Districts", x => x.Id);
                    table.CheckConstraint("CK_Districts_DeliveryZone", "[DeliveryZone] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_Districts_Provinces_ProvinceId",
                        column: x => x.ProvinceId,
                        principalSchema: "ref",
                        principalTable: "Provinces",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Districts_ProvinceId",
                schema: "ref",
                table: "Districts",
                column: "ProvinceId");

            migrationBuilder.CreateIndex(
                name: "UX_Districts_Code",
                schema: "ref",
                table: "Districts",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Districts_Name",
                schema: "ref",
                table: "Districts",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Provinces_Code",
                schema: "ref",
                table: "Provinces",
                column: "Code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Districts_DeliveryDistrictId",
                schema: "sales",
                table: "Orders",
                column: "DeliveryDistrictId",
                principalSchema: "ref",
                principalTable: "Districts",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PickupLocations_Districts_DistrictId",
                schema: "sales",
                table: "PickupLocations",
                column: "DistrictId",
                principalSchema: "ref",
                principalTable: "Districts",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Districts_DeliveryDistrictId",
                schema: "sales",
                table: "Orders");

            migrationBuilder.DropForeignKey(
                name: "FK_PickupLocations_Districts_DistrictId",
                schema: "sales",
                table: "PickupLocations");

            migrationBuilder.DropTable(
                name: "Districts",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "Provinces",
                schema: "ref");
        }
    }
}
