using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AnalyticsTraffic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "analytics");

            migrationBuilder.CreateTable(
                name: "PageDaily",
                schema: "analytics",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    PathHash = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PageType = table.Column<byte>(type: "tinyint", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: true),
                    PageViews = table.Column<int>(type: "int", nullable: false),
                    Sessions = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageDaily", x => new { x.Date, x.PathHash });
                    table.CheckConstraint("CK_PageDaily_Counts", "[PageViews] >= 0 AND [Sessions] >= 0 AND [Sessions] <= [PageViews]");
                    table.CheckConstraint("CK_PageDaily_PageType", "[PageType] IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 99)");
                });

            migrationBuilder.CreateTable(
                name: "PageViews",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2(0)", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PageType = table.Column<byte>(type: "tinyint", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: true),
                    CategoryId = table.Column<int>(type: "int", nullable: true),
                    ReferrerHost = table.Column<string>(type: "varchar(255)", nullable: true),
                    UtmSource = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmMedium = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmCampaign = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Channel = table.Column<byte>(type: "tinyint", nullable: false),
                    DeviceType = table.Column<byte>(type: "tinyint", nullable: false),
                    IsBot = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageViews", x => x.Id);
                    table.CheckConstraint("CK_PageViews_Channel", "[Channel] BETWEEN 1 AND 7");
                    table.CheckConstraint("CK_PageViews_DeviceType", "[DeviceType] IN (1, 2, 3, 9)");
                    table.CheckConstraint("CK_PageViews_PageType", "[PageType] IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 99)");
                });

            migrationBuilder.CreateTable(
                name: "TrafficDaily",
                schema: "analytics",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Channel = table.Column<byte>(type: "tinyint", nullable: false),
                    DeviceType = table.Column<byte>(type: "tinyint", nullable: false),
                    SourceKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PageViews = table.Column<int>(type: "int", nullable: false),
                    Sessions = table.Column<int>(type: "int", nullable: false),
                    BotPageViews = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrafficDaily", x => new { x.Date, x.Channel, x.DeviceType, x.SourceKey });
                    table.CheckConstraint("CK_TrafficDaily_Channel", "[Channel] BETWEEN 1 AND 7");
                    table.CheckConstraint("CK_TrafficDaily_Counts", "[PageViews] >= 0 AND [Sessions] >= 0 AND [BotPageViews] >= 0 AND [Sessions] <= [PageViews]");
                    table.CheckConstraint("CK_TrafficDaily_DeviceType", "[DeviceType] IN (1, 2, 3, 9)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PageViews_Occurred",
                schema: "analytics",
                table: "PageViews",
                column: "OccurredAtUtc")
                .Annotation("SqlServer:Include", new[] { "SessionId", "IsBot", "Channel", "DeviceType" });

            // Doc 08 §3.8: "DATA_COMPRESSION = PAGE". EF Core cannot declare it, so the empty table and its index
            // are rebuilt with it here. Page compression is in every edition since SQL Server 2016 SP1, Express
            // and LocalDB included, and in Azure SQL. AnalyticsSchemaTests reads it back from sys.partitions.
            migrationBuilder.Sql("ALTER TABLE [analytics].[PageViews] REBUILD WITH (DATA_COMPRESSION = PAGE);");
            migrationBuilder.Sql(
                "ALTER INDEX [IX_PageViews_Occurred] ON [analytics].[PageViews] REBUILD WITH (DATA_COMPRESSION = PAGE);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PageDaily",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "PageViews",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "TrafficDaily",
                schema: "analytics");
        }
    }
}
