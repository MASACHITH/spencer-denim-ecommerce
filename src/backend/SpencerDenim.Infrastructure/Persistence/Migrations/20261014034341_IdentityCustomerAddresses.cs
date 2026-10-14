using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IdentityCustomerAddresses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerAddresses",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    RecipientName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Phone = table.Column<string>(type: "varchar(20)", nullable: false),
                    Line1 = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Line2 = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    City = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    DistrictId = table.Column<int>(type: "int", nullable: false),
                    PostalCode = table.Column<string>(type: "varchar(10)", nullable: true),
                    Landmark = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerAddresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerAddresses_Districts_DistrictId",
                        column: x => x.DistrictId,
                        principalSchema: "ref",
                        principalTable: "Districts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CustomerAddresses_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_DefaultDistrictId",
                schema: "identity",
                table: "Users",
                column: "DefaultDistrictId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAddresses_District",
                schema: "identity",
                table: "CustomerAddresses",
                column: "DistrictId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAddresses_User",
                schema: "identity",
                table: "CustomerAddresses",
                columns: new[] { "UserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_CustomerAddresses_UserId_Default",
                schema: "identity",
                table: "CustomerAddresses",
                column: "UserId",
                unique: true,
                filter: "[IsDefault] = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Districts_DefaultDistrictId",
                schema: "identity",
                table: "Users",
                column: "DefaultDistrictId",
                principalSchema: "ref",
                principalTable: "Districts",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Districts_DefaultDistrictId",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropTable(
                name: "CustomerAddresses",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "IX_Users_DefaultDistrictId",
                schema: "identity",
                table: "Users");
        }
    }
}
