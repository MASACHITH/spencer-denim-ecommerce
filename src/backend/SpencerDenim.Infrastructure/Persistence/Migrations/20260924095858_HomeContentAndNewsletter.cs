using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HomeContentAndNewsletter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "content");

            migrationBuilder.EnsureSchema(
                name: "marketing");

            migrationBuilder.CreateTable(
                name: "AnnouncementMessages",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Message = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LinkUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnnouncementMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HomeSections",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SectionType = table.Column<byte>(type: "tinyint", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Subtitle = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HomeSections", x => x.Id);
                    table.CheckConstraint("CK_HomeSections_SectionType", "[SectionType] BETWEEN 1 AND 10");
                    table.CheckConstraint("CK_HomeSections_SettingsJson", "[SettingsJson] IS NULL OR ISJSON([SettingsJson]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "NewsletterSubscribers",
                schema: "marketing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EmailNormalized = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Source = table.Column<string>(type: "varchar(30)", nullable: false),
                    ConsentAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ConsentIpAddress = table.Column<string>(type: "varchar(45)", nullable: true),
                    UnsubscribeToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnsubscribedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsletterSubscribers", x => x.Id);
                    table.CheckConstraint("CK_NewsletterSubscribers_Status", "[Status] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_NewsletterSubscribers_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "HomeSectionItems",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    HomeSectionId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Subtitle = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Body = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ButtonLabel = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    LinkUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ImageAssetId = table.Column<int>(type: "int", nullable: true),
                    MobileImageAssetId = table.Column<int>(type: "int", nullable: true),
                    VideoAssetId = table.Column<int>(type: "int", nullable: true),
                    ProductId = table.Column<int>(type: "int", nullable: true),
                    DepartmentId = table.Column<int>(type: "int", nullable: true),
                    IconKey = table.Column<string>(type: "varchar(40)", nullable: true),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HomeSectionItems", x => x.Id);
                    table.CheckConstraint("CK_HomeSectionItems_VideoFallback", "[VideoAssetId] IS NULL OR [ImageAssetId] IS NOT NULL OR [MobileImageAssetId] IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_HomeSectionItems_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "catalog",
                        principalTable: "Departments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_HomeSectionItems_HomeSections_HomeSectionId",
                        column: x => x.HomeSectionId,
                        principalSchema: "content",
                        principalTable: "HomeSections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HomeSectionItems_MediaAssets_ImageAssetId",
                        column: x => x.ImageAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_HomeSectionItems_MediaAssets_MobileImageAssetId",
                        column: x => x.MobileImageAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_HomeSectionItems_MediaAssets_VideoAssetId",
                        column: x => x.VideoAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_HomeSectionItems_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnnouncementMessages_Active_SortOrder",
                schema: "content",
                table: "AnnouncementMessages",
                columns: new[] { "IsActive", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_HomeSectionItems_DepartmentId",
                schema: "content",
                table: "HomeSectionItems",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_HomeSectionItems_ImageAssetId",
                schema: "content",
                table: "HomeSectionItems",
                column: "ImageAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_HomeSectionItems_MobileImageAssetId",
                schema: "content",
                table: "HomeSectionItems",
                column: "MobileImageAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_HomeSectionItems_ProductId",
                schema: "content",
                table: "HomeSectionItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_HomeSectionItems_Section_SortOrder",
                schema: "content",
                table: "HomeSectionItems",
                columns: new[] { "HomeSectionId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_HomeSectionItems_VideoAssetId",
                schema: "content",
                table: "HomeSectionItems",
                column: "VideoAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_HomeSections_Enabled_SortOrder",
                schema: "content",
                table: "HomeSections",
                columns: new[] { "IsEnabled", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsletterSubscribers_Status_CreatedAtUtc",
                schema: "marketing",
                table: "NewsletterSubscribers",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsletterSubscribers_UserId",
                schema: "marketing",
                table: "NewsletterSubscribers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UX_NewsletterSubscribers_EmailNormalized",
                schema: "marketing",
                table: "NewsletterSubscribers",
                column: "EmailNormalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_NewsletterSubscribers_UnsubscribeToken",
                schema: "marketing",
                table: "NewsletterSubscribers",
                column: "UnsubscribeToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnnouncementMessages",
                schema: "content");

            migrationBuilder.DropTable(
                name: "HomeSectionItems",
                schema: "content");

            migrationBuilder.DropTable(
                name: "NewsletterSubscribers",
                schema: "marketing");

            migrationBuilder.DropTable(
                name: "HomeSections",
                schema: "content");
        }
    }
}
