using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SearchQueriesDaily : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HiddenSearchTerms",
                schema: "analytics",
                columns: table => new
                {
                    QueryNormalized = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_CI_AS_SC"),
                    HiddenAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiddenSearchTerms", x => x.QueryNormalized);
                });

            migrationBuilder.CreateTable(
                name: "SearchQueriesDaily",
                schema: "analytics",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    QueryNormalized = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_CI_AS_SC"),
                    Searches = table.Column<int>(type: "int", nullable: false),
                    ZeroResultSearches = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchQueriesDaily", x => new { x.Date, x.QueryNormalized });
                    table.CheckConstraint("CK_SearchQueriesDaily_Counts", "[Searches] >= 1 AND [ZeroResultSearches] >= 0 AND [ZeroResultSearches] <= [Searches]");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HiddenSearchTerms",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "SearchQueriesDaily",
                schema: "analytics");
        }
    }
}
