using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatalogueFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.EnsureSchema(
                name: "media");

            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "AgeGroups",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Slug = table.Column<string>(type: "varchar(40)", nullable: false),
                    MinMonths = table.Column<short>(type: "smallint", nullable: true),
                    MaxMonths = table.Column<short>(type: "smallint", nullable: true),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgeGroups", x => x.Id);
                    table.CheckConstraint("CK_AgeGroups_Months", "[MinMonths] IS NULL OR [MaxMonths] IS NULL OR [MinMonths] <= [MaxMonths]");
                });

            migrationBuilder.CreateTable(
                name: "AttributeOptions",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AttributeType = table.Column<byte>(type: "tinyint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Slug = table.Column<string>(type: "varchar(60)", nullable: false),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttributeOptions", x => x.Id);
                    table.CheckConstraint("CK_AttributeOptions_AttributeType", "[AttributeType] IN (1, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "ColourFamilies",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Slug = table.Column<string>(type: "varchar(30)", nullable: false),
                    HexCode = table.Column<string>(type: "char(7)", nullable: false),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColourFamilies", x => x.Id);
                    table.CheckConstraint("CK_ColourFamilies_HexCode", "[HexCode] LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'");
                });

            migrationBuilder.CreateTable(
                name: "MediaAssets",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MediaType = table.Column<byte>(type: "tinyint", nullable: false),
                    Visibility = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    StorageKey = table.Column<string>(type: "varchar(400)", nullable: false),
                    ContentType = table.Column<string>(type: "varchar(100)", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    DurationSeconds = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: true),
                    PosterAssetId = table.Column<int>(type: "int", nullable: true),
                    RenditionSetVersion = table.Column<byte>(type: "tinyint", nullable: true),
                    Checksum = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    AltText = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    UploadedByUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAssets", x => x.Id);
                    table.CheckConstraint("CK_MediaAssets_MediaType", "[MediaType] IN (1, 2, 3)");
                    table.CheckConstraint("CK_MediaAssets_SizeBytes", "[SizeBytes] > 0 AND ([MediaType] <> 2 OR [SizeBytes] <= 52428800) AND ([Visibility] <> 2 OR [SizeBytes] <= 5242880)");
                    table.CheckConstraint("CK_MediaAssets_Status", "[Status] IN (0, 1, 2)");
                    table.CheckConstraint("CK_MediaAssets_VideoHasPoster", "[MediaType] <> 2 OR [PosterAssetId] IS NOT NULL");
                    table.CheckConstraint("CK_MediaAssets_Visibility", "[Visibility] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_MediaAssets_MediaAssets_PosterAssetId",
                        column: x => x.PosterAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Sizes",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScaleName = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sizes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Departments",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Slug = table.Column<string>(type: "varchar(60)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    HeroImageAssetId = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SeoTitle = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    SeoDescription = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Departments_MediaAssets_HeroImageAssetId",
                        column: x => x.HeroImageAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MediaUsages",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MediaAssetId = table.Column<int>(type: "int", nullable: false),
                    OwnerType = table.Column<string>(type: "varchar(40)", nullable: false),
                    OwnerId = table.Column<string>(type: "varchar(100)", nullable: false),
                    UsageRole = table.Column<string>(type: "varchar(40)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaUsages_MediaAssets_MediaAssetId",
                        column: x => x.MediaAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SizeGuides",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IntroHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TableJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MeasuringTipsHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ImageAssetId = table.Column<int>(type: "int", nullable: true),
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
                    table.PrimaryKey("PK_SizeGuides", x => x.Id);
                    table.CheckConstraint("CK_SizeGuides_TableJson", "ISJSON([TableJson]) = 1");
                    table.ForeignKey(
                        name: "FK_SizeGuides_MediaAssets_ImageAssetId",
                        column: x => x.ImageAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Categories",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    ParentCategoryId = table.Column<int>(type: "int", nullable: true),
                    Path = table.Column<string>(type: "varchar(200)", nullable: false),
                    Depth = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)1),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Slug = table.Column<string>(type: "varchar(100)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ImageAssetId = table.Column<int>(type: "int", nullable: true),
                    BannerAssetId = table.Column<int>(type: "int", nullable: true),
                    BannerLinkUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AgeGroupId = table.Column<int>(type: "int", nullable: true),
                    SizeGuideId = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    ShowInMegaMenu = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    MegaMenuColumn = table.Column<byte>(type: "tinyint", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SeoTitle = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    SeoDescription = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
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
                    table.PrimaryKey("PK_Categories", x => x.Id);
                    table.CheckConstraint("CK_Categories_Depth", "[Depth] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_Categories_MegaMenuColumn", "[MegaMenuColumn] IS NULL OR [MegaMenuColumn] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_Categories_NotOwnParent", "[ParentCategoryId] IS NULL OR [ParentCategoryId] <> [Id]");
                    table.ForeignKey(
                        name: "FK_Categories_AgeGroups_AgeGroupId",
                        column: x => x.AgeGroupId,
                        principalSchema: "catalog",
                        principalTable: "AgeGroups",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Categories_Categories_ParentCategoryId",
                        column: x => x.ParentCategoryId,
                        principalSchema: "catalog",
                        principalTable: "Categories",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Categories_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "catalog",
                        principalTable: "Departments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Categories_MediaAssets_BannerAssetId",
                        column: x => x.BannerAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Categories_MediaAssets_ImageAssetId",
                        column: x => x.ImageAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Categories_SizeGuides_SizeGuideId",
                        column: x => x.SizeGuideId,
                        principalSchema: "catalog",
                        principalTable: "SizeGuides",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Products",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductCode = table.Column<string>(type: "varchar(40)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "varchar(220)", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    PrimaryCategoryId = table.Column<int>(type: "int", nullable: false),
                    AgeGroupId = table.Column<int>(type: "int", nullable: true),
                    ShortDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CareInstructions = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    MaterialId = table.Column<int>(type: "int", nullable: true),
                    FitId = table.Column<int>(type: "int", nullable: true),
                    OccasionId = table.Column<int>(type: "int", nullable: true),
                    BasePrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CompareAtPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    BadgeOverride = table.Column<byte>(type: "tinyint", nullable: true),
                    NewUntilUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RecommendedRank = table.Column<int>(type: "int", nullable: true),
                    SizeGuideId = table.Column<int>(type: "int", nullable: true),
                    CardImageAssetId = table.Column<int>(type: "int", nullable: true),
                    HoverImageAssetId = table.Column<int>(type: "int", nullable: true),
                    IsPublished = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    FirstPublishedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SeoTitle = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    SeoDescription = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
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
                    table.PrimaryKey("PK_Products", x => x.Id);
                    table.CheckConstraint("CK_Products_BadgeOverride", "[BadgeOverride] IS NULL OR [BadgeOverride] IN (1, 2, 3, 4)");
                    table.CheckConstraint("CK_Products_BasePrice", "[BasePrice] > 0");
                    table.CheckConstraint("CK_Products_CompareAtGtBase", "[CompareAtPrice] IS NULL OR [CompareAtPrice] > [BasePrice]");
                    table.ForeignKey(
                        name: "FK_Products_AgeGroups_AgeGroupId",
                        column: x => x.AgeGroupId,
                        principalSchema: "catalog",
                        principalTable: "AgeGroups",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Products_AttributeOptions_FitId",
                        column: x => x.FitId,
                        principalSchema: "catalog",
                        principalTable: "AttributeOptions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Products_AttributeOptions_MaterialId",
                        column: x => x.MaterialId,
                        principalSchema: "catalog",
                        principalTable: "AttributeOptions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Products_AttributeOptions_OccasionId",
                        column: x => x.OccasionId,
                        principalSchema: "catalog",
                        principalTable: "AttributeOptions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Products_Categories_PrimaryCategoryId",
                        column: x => x.PrimaryCategoryId,
                        principalSchema: "catalog",
                        principalTable: "Categories",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Products_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "catalog",
                        principalTable: "Departments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Products_MediaAssets_CardImageAssetId",
                        column: x => x.CardImageAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Products_MediaAssets_HoverImageAssetId",
                        column: x => x.HoverImageAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Products_SizeGuides_SizeGuideId",
                        column: x => x.SizeGuideId,
                        principalSchema: "catalog",
                        principalTable: "SizeGuides",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ProductCategories",
                schema: "catalog",
                columns: table => new
                {
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCategories", x => new { x.CategoryId, x.ProductId });
                    table.ForeignKey(
                        name: "FK_ProductCategories_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "catalog",
                        principalTable: "Categories",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProductCategories_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductColours",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ColourFamilyId = table.Column<int>(type: "int", nullable: false),
                    SwatchHex = table.Column<string>(type: "char(7)", nullable: true),
                    SwatchAssetId = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductColours", x => x.Id);
                    table.UniqueConstraint("UX_ProductColours_Id_ProductId", x => new { x.Id, x.ProductId });
                    table.CheckConstraint("CK_ProductColours_SwatchHex", "[SwatchHex] IS NULL OR [SwatchHex] LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'");
                    table.ForeignKey(
                        name: "FK_ProductColours_ColourFamilies_ColourFamilyId",
                        column: x => x.ColourFamilyId,
                        principalSchema: "catalog",
                        principalTable: "ColourFamilies",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProductColours_MediaAssets_SwatchAssetId",
                        column: x => x.SwatchAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProductColours_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductSearchIndex",
                schema: "catalog",
                columns: table => new
                {
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    NameText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SearchText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductSearchIndex", x => x.ProductId);
                    table.ForeignKey(
                        name: "FK_ProductSearchIndex_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductStats",
                schema: "catalog",
                columns: table => new
                {
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    MinListPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxListPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MinSalePrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxSalePrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxOriginalPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxDiscountPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    HasDiscount = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    RatingAverage = table.Column<decimal>(type: "decimal(3,2)", precision: 3, scale: 2, nullable: true),
                    RatingCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    SoldQty30d = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    SoldQtyTotal = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    PricingRefreshedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    StatsRefreshedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductStats", x => x.ProductId);
                    table.CheckConstraint("CK_ProductStats_Counts", "[RatingCount] >= 0 AND [SoldQty30d] >= 0 AND [SoldQtyTotal] >= 0");
                    table.CheckConstraint("CK_ProductStats_Discount", "[MaxDiscountPercent] BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_ProductStats_PriceRange", "[MinListPrice] <= [MaxListPrice] AND [MinSalePrice] <= [MaxSalePrice]");
                    table.CheckConstraint("CK_ProductStats_Rating", "[RatingAverage] IS NULL OR [RatingAverage] BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "FK_ProductStats_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RelatedProducts",
                schema: "catalog",
                columns: table => new
                {
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    RelatedProductId = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelatedProducts", x => new { x.ProductId, x.RelatedProductId });
                    table.CheckConstraint("CK_RelatedProducts_NotSelf", "[RelatedProductId] <> [ProductId]");
                    table.ForeignKey(
                        name: "FK_RelatedProducts_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RelatedProducts_Products_RelatedProductId",
                        column: x => x.RelatedProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ProductMedia",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    MediaAssetId = table.Column<int>(type: "int", nullable: false),
                    ProductColourId = table.Column<int>(type: "int", nullable: true),
                    MediaRole = table.Column<byte>(type: "tinyint", nullable: false),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    AltTextOverride = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductMedia", x => x.Id);
                    table.CheckConstraint("CK_ProductMedia_MediaRole", "[MediaRole] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_ProductMedia_MediaAssets_MediaAssetId",
                        column: x => x.MediaAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProductMedia_ProductColours_ProductColourId",
                        column: x => x.ProductColourId,
                        principalSchema: "catalog",
                        principalTable: "ProductColours",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProductMedia_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductVariants",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    ProductColourId = table.Column<int>(type: "int", nullable: false),
                    SizeId = table.Column<int>(type: "int", nullable: false),
                    Sku = table.Column<string>(type: "varchar(64)", nullable: false),
                    Barcode = table.Column<string>(type: "varchar(32)", nullable: true),
                    PriceOverride = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CompareAtPriceOverride = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductVariants", x => x.Id);
                    table.CheckConstraint("CK_ProductVariants_CompareAtGtPrice", "[CompareAtPriceOverride] IS NULL OR [PriceOverride] IS NULL OR [CompareAtPriceOverride] > [PriceOverride]");
                    table.CheckConstraint("CK_ProductVariants_CompareAtOverride", "[CompareAtPriceOverride] IS NULL OR [CompareAtPriceOverride] > 0");
                    table.CheckConstraint("CK_ProductVariants_PriceOverride", "[PriceOverride] IS NULL OR [PriceOverride] > 0");
                    table.ForeignKey(
                        name: "FK_ProductVariants_ProductColours_ProductColourId_ProductId",
                        columns: x => new { x.ProductColourId, x.ProductId },
                        principalSchema: "catalog",
                        principalTable: "ProductColours",
                        principalColumns: new[] { "Id", "ProductId" });
                    table.ForeignKey(
                        name: "FK_ProductVariants_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProductVariants_Sizes_SizeId",
                        column: x => x.SizeId,
                        principalSchema: "catalog",
                        principalTable: "Sizes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "StockLevels",
                schema: "inventory",
                columns: table => new
                {
                    VariantId = table.Column<int>(type: "int", nullable: false),
                    OnHand = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Reserved = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Available = table.Column<int>(type: "int", nullable: false, computedColumnSql: "[OnHand] - [Reserved]", stored: true),
                    LowStockThreshold = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockLevels", x => x.VariantId);
                    table.CheckConstraint("CK_StockLevels_LowStockThreshold", "[LowStockThreshold] IS NULL OR [LowStockThreshold] >= 0");
                    table.CheckConstraint("CK_StockLevels_OnHand", "[OnHand] >= 0");
                    table.CheckConstraint("CK_StockLevels_Reserved", "[Reserved] >= 0");
                    table.CheckConstraint("CK_StockLevels_ReservedLeOnHand", "[Reserved] <= [OnHand]");
                    table.ForeignKey(
                        name: "FK_StockLevels_ProductVariants_VariantId",
                        column: x => x.VariantId,
                        principalSchema: "catalog",
                        principalTable: "ProductVariants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "UX_AgeGroups_Name",
                schema: "catalog",
                table: "AgeGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_AgeGroups_Slug",
                schema: "catalog",
                table: "AgeGroups",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_AttributeOptions_Type_Slug",
                schema: "catalog",
                table: "AttributeOptions",
                columns: new[] { "AttributeType", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Categories_AgeGroupId",
                schema: "catalog",
                table: "Categories",
                column: "AgeGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_BannerAssetId",
                schema: "catalog",
                table: "Categories",
                column: "BannerAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Dept_Parent_Sort",
                schema: "catalog",
                table: "Categories",
                columns: new[] { "DepartmentId", "ParentCategoryId", "SortOrder" })
                .Annotation("SqlServer:Include", new[] { "Name", "Slug", "IsActive", "ShowInMegaMenu" });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_ImageAssetId",
                schema: "catalog",
                table: "Categories",
                column: "ImageAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_ParentCategoryId",
                schema: "catalog",
                table: "Categories",
                column: "ParentCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Path",
                schema: "catalog",
                table: "Categories",
                column: "Path");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_SizeGuideId",
                schema: "catalog",
                table: "Categories",
                column: "SizeGuideId");

            migrationBuilder.CreateIndex(
                name: "UX_Categories_Dept_Parent_Slug",
                schema: "catalog",
                table: "Categories",
                columns: new[] { "DepartmentId", "ParentCategoryId", "Slug" },
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_ColourFamilies_Name",
                schema: "catalog",
                table: "ColourFamilies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ColourFamilies_Slug",
                schema: "catalog",
                table: "ColourFamilies",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Departments_HeroImageAssetId",
                schema: "catalog",
                table: "Departments",
                column: "HeroImageAssetId");

            migrationBuilder.CreateIndex(
                name: "UX_Departments_Name",
                schema: "catalog",
                table: "Departments",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Departments_Slug",
                schema: "catalog",
                table: "Departments",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_Checksum",
                schema: "media",
                table: "MediaAssets",
                column: "Checksum");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_Library",
                schema: "media",
                table: "MediaAssets",
                columns: new[] { "Visibility", "MediaType", "CreatedAtUtc" },
                descending: new[] { false, false, true },
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_PosterAssetId",
                schema: "media",
                table: "MediaAssets",
                column: "PosterAssetId");

            migrationBuilder.CreateIndex(
                name: "UX_MediaAssets_PublicId",
                schema: "media",
                table: "MediaAssets",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_MediaAssets_StorageKey",
                schema: "media",
                table: "MediaAssets",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaUsages_Owner",
                schema: "media",
                table: "MediaUsages",
                columns: new[] { "OwnerType", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "UX_MediaUsages_Asset_Owner_Role",
                schema: "media",
                table: "MediaUsages",
                columns: new[] { "MediaAssetId", "OwnerType", "OwnerId", "UsageRole" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_ProductId",
                schema: "catalog",
                table: "ProductCategories",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductColours_ColourFamily",
                schema: "catalog",
                table: "ProductColours",
                columns: new[] { "ColourFamilyId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductColours_SwatchAssetId",
                schema: "catalog",
                table: "ProductColours",
                column: "SwatchAssetId");

            migrationBuilder.CreateIndex(
                name: "UX_ProductColours_Product_Name",
                schema: "catalog",
                table: "ProductColours",
                columns: new[] { "ProductId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductMedia_MediaAssetId",
                schema: "catalog",
                table: "ProductMedia",
                column: "MediaAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductMedia_Product_Colour_Sort",
                schema: "catalog",
                table: "ProductMedia",
                columns: new[] { "ProductId", "ProductColourId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductMedia_ProductColourId",
                schema: "catalog",
                table: "ProductMedia",
                column: "ProductColourId");

            migrationBuilder.CreateIndex(
                name: "UX_ProductMedia_Product_Asset_Colour",
                schema: "catalog",
                table: "ProductMedia",
                columns: new[] { "ProductId", "MediaAssetId", "ProductColourId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_AgeGroupId",
                schema: "catalog",
                table: "Products",
                column: "AgeGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_CardImageAssetId",
                schema: "catalog",
                table: "Products",
                column: "CardImageAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_DepartmentId",
                schema: "catalog",
                table: "Products",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_FitId",
                schema: "catalog",
                table: "Products",
                column: "FitId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_HoverImageAssetId",
                schema: "catalog",
                table: "Products",
                column: "HoverImageAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Listing",
                schema: "catalog",
                table: "Products",
                columns: new[] { "DepartmentId", "PrimaryCategoryId" },
                filter: "[IsPublished] = 1 AND [IsDeleted] = 0")
                .Annotation("SqlServer:Include", new[] { "AgeGroupId", "MaterialId", "FitId", "OccasionId", "FirstPublishedAtUtc", "RecommendedRank", "NewUntilUtc", "BadgeOverride", "Name", "Slug", "CardImageAssetId", "HoverImageAssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_MaterialId",
                schema: "catalog",
                table: "Products",
                column: "MaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_NewUntilUtc",
                schema: "catalog",
                table: "Products",
                column: "NewUntilUtc",
                filter: "[IsPublished] = 1 AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Products_OccasionId",
                schema: "catalog",
                table: "Products",
                column: "OccasionId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_PrimaryCategoryId",
                schema: "catalog",
                table: "Products",
                column: "PrimaryCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_SizeGuideId",
                schema: "catalog",
                table: "Products",
                column: "SizeGuideId");

            migrationBuilder.CreateIndex(
                name: "UX_Products_ProductCode",
                schema: "catalog",
                table: "Products",
                column: "ProductCode",
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_Products_Slug",
                schema: "catalog",
                table: "Products",
                column: "Slug",
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ProductSearchIndex_NameText",
                schema: "catalog",
                table: "ProductSearchIndex",
                column: "NameText");

            migrationBuilder.CreateIndex(
                name: "IX_ProductStats_MinSalePrice",
                schema: "catalog",
                table: "ProductStats",
                column: "MinSalePrice")
                .Annotation("SqlServer:Include", new[] { "HasDiscount", "MaxDiscountPercent", "RatingAverage", "SoldQty30d" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_Product",
                schema: "catalog",
                table: "ProductVariants",
                column: "ProductId")
                .Annotation("SqlServer:Include", new[] { "IsActive", "SizeId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_ProductColourId_ProductId",
                schema: "catalog",
                table: "ProductVariants",
                columns: new[] { "ProductColourId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_Size",
                schema: "catalog",
                table: "ProductVariants",
                columns: new[] { "SizeId", "ProductId" })
                .Annotation("SqlServer:Include", new[] { "IsActive" });

            migrationBuilder.CreateIndex(
                name: "UX_ProductVariants_Barcode",
                schema: "catalog",
                table: "ProductVariants",
                column: "Barcode",
                unique: true,
                filter: "[Barcode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_ProductVariants_Colour_Size",
                schema: "catalog",
                table: "ProductVariants",
                columns: new[] { "ProductColourId", "SizeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ProductVariants_Sku",
                schema: "catalog",
                table: "ProductVariants",
                column: "Sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RelatedProducts_RelatedProductId",
                schema: "catalog",
                table: "RelatedProducts",
                column: "RelatedProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SizeGuides_ImageAssetId",
                schema: "catalog",
                table: "SizeGuides",
                column: "ImageAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Sizes_Label",
                schema: "catalog",
                table: "Sizes",
                column: "Label");

            migrationBuilder.CreateIndex(
                name: "IX_Sizes_Scale_Sort",
                schema: "catalog",
                table: "Sizes",
                columns: new[] { "ScaleName", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "UX_Sizes_Scale_Label",
                schema: "catalog",
                table: "Sizes",
                columns: new[] { "ScaleName", "Label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockLevels_Available",
                schema: "inventory",
                table: "StockLevels",
                column: "Available")
                .Annotation("SqlServer:Include", new[] { "LowStockThreshold", "OnHand" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaUsages",
                schema: "media");

            migrationBuilder.DropTable(
                name: "ProductCategories",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ProductMedia",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ProductSearchIndex",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ProductStats",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "RelatedProducts",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "StockLevels",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "ProductVariants",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ProductColours",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Sizes",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ColourFamilies",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Products",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "AttributeOptions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Categories",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "AgeGroups",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Departments",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "SizeGuides",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "MediaAssets",
                schema: "media");
        }
    }
}
