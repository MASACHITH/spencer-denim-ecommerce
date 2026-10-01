using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// W1-12 (ADM-11): <c>CK_Categories_Slug</c> and <c>CK_Departments_Slug</c>, the slug spelling rule of
    /// <c>SpencerDenim.Domain.Catalog.CatalogSlug</c> as a database constraint.
    /// <para>
    /// Before W1-12 only the seeder could write either column, so the domain guard was the whole story.
    /// ADM-11 gives both an admin write path, and the failure these constraints exist for is specific: a
    /// slug that is not a slug is a row the listing links to and whose own URL answers 400, which no screen
    /// shows as wrong. The same argument is already recorded for products as W1-03's
    /// <c>CK_Products_Slug</c> (see <c>ProductConfiguration</c>); this migration does not add that one,
    /// because W1-03 owns the product write path.
    /// </para>
    /// <para>
    /// <b>No data change.</b> Both constraints are added without <c>WITH NOCHECK</c>, so SQL Server
    /// validates every existing row as it applies them: a database whose slugs are already clean takes it
    /// silently, and one that is not fails the migration rather than accepting a constraint it does not
    /// honour. Verified against a database migrated to the previous migration and then seeded: all 3 seeded
    /// departments and all 29 seeded categories pass, and the constraints are present afterwards.
    /// </para>
    /// <para>
    /// The expression needs a <b>binary</b> collation; see <c>CatalogSlugConstraint</c> for why neither the
    /// database's own case-insensitive collation nor a case-sensitive one is enough.
    /// </para>
    /// </summary>
    public partial class CatalogSlugWriteGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_Departments_Slug",
                schema: "catalog",
                table: "Departments",
                sql: "[Slug] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^a-z0-9-]%' AND [Slug] NOT LIKE '-%' AND [Slug] NOT LIKE '%-' AND [Slug] NOT LIKE '%--%' AND DATALENGTH([Slug]) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Categories_Slug",
                schema: "catalog",
                table: "Categories",
                sql: "[Slug] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^a-z0-9-]%' AND [Slug] NOT LIKE '-%' AND [Slug] NOT LIKE '%-' AND [Slug] NOT LIKE '%--%' AND DATALENGTH([Slug]) > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Departments_Slug",
                schema: "catalog",
                table: "Departments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Categories_Slug",
                schema: "catalog",
                table: "Categories");
        }
    }
}
