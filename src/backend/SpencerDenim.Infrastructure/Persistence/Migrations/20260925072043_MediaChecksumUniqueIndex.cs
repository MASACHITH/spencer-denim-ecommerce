using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MediaChecksumUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_MediaAssets_Checksum_Ready",
                schema: "media",
                table: "MediaAssets",
                columns: new[] { "Checksum", "MediaType", "Visibility" },
                unique: true,
                filter: "[IsDeleted] = 0 AND [Status] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_MediaAssets_Checksum_Ready",
                schema: "media",
                table: "MediaAssets");
        }
    }
}
