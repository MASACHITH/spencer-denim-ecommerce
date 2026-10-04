using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpencerDenim.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The rest of <c>sales</c> (doc 08 §3.5), on top of W2-09's <c>Carts</c> / <c>CartItems</c>:
    /// <c>NumberSeries</c>, <c>PaymentProviders</c>, <c>DeliveryMethods</c>,
    /// <c>DeliveryMethodZoneRates</c>, <c>PickupLocations</c>, <c>Couriers</c>, <c>Orders</c>,
    /// <c>OrderLines</c>, <c>OrderStatusHistory</c>, <c>Payments</c>, <c>PaymentEvents</c> and
    /// <c>Refunds</c> — twelve tables. The schema itself already exists; this migration does not create it.
    /// <para>
    /// <b>It also adds <c>Carts.DeliveryMethodId</c> with its foreign key</b>, which W2-09 deliberately left
    /// out because <c>sales.DeliveryMethods</c> did not exist and the column could not have carried a
    /// constraint. Column and constraint arrive together here, in one piece, as W2-09's PR said they should.
    /// </para>
    /// <para>
    /// <b>No transitions, no provider, no seed data.</b> W3-03 step 2 builds <c>IPaymentProvider</c> and step
    /// 3 the bank-transfer flow; the order and payment state machines of doc 08 §4 are theirs. What this
    /// migration owes them is a schema that can <i>express</i> §4 — hence <c>Status</c>, <c>StockState</c>,
    /// <c>CaptureLockedUntilUtc</c>, <c>IsOrderSettlement</c> and the two filtered unique indexes on
    /// <c>Payments</c> — and nothing more. Delivery rates and provider rows are seeded by their admin
    /// milestones; doc 08 §9 marks the rate values placeholders for the client to confirm (D8).
    /// </para>
    /// <para>
    /// <b>Four of doc 08's foreign keys are columns without constraints:</b>
    /// <c>Orders.DeliveryDistrictId</c> and <c>PickupLocations.DistrictId</c> (<c>ref.Districts</c> —
    /// GLB-06 — does not exist; no migration has created the <c>ref</c> schema),
    /// <c>Orders.PromoCodeId</c> (<c>marketing.PromoCodes</c>, ADM-13) and <c>OrderLines.SaleCampaignId</c>
    /// (<c>marketing.SaleCampaigns</c>, ADM-14). Each is indexed as if the constraint existed, so adding it
    /// later is one <c>ALTER TABLE</c> and no index build. <c>Carts.DistrictId</c> and
    /// <c>Carts.PromoCodeText</c> stay out altogether, for the reasons W2-09 gave.
    /// </para>
    /// <para>
    /// <b>Exactly one cascade</b> (doc 08 §1.6's allow-list): <c>DeliveryMethodZoneRates</c> →
    /// <c>DeliveryMethods</c>. Everything else is <c>NO ACTION</c>, including <c>Orders</c> →
    /// <c>OrderLines</c>, which EF would have made a cascade by convention.
    /// </para>
    /// </summary>
    public partial class SalesOrdersAndPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeliveryMethodId",
                schema: "sales",
                table: "Carts",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Couriers",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    TrackingUrlTemplate = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Phone = table.Column<string>(type: "varchar(20)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Couriers", x => x.Id);
                    table.CheckConstraint("CK_Couriers_TrackingUrlTemplate", "[TrackingUrlTemplate] IS NULL OR CHARINDEX('{trackingNumber}', [TrackingUrlTemplate]) > 0");
                });

            migrationBuilder.CreateTable(
                name: "DeliveryMethods",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "varchar(20)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    RequiresAddress = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryMethods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberSeries",
                schema: "sales",
                columns: table => new
                {
                    SeriesKey = table.Column<string>(type: "varchar(30)", nullable: false),
                    Prefix = table.Column<string>(type: "varchar(10)", nullable: false),
                    NextValue = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberSeries", x => x.SeriesKey);
                    table.CheckConstraint("CK_NumberSeries_NextValue", "[NextValue] >= 1");
                });

            migrationBuilder.CreateTable(
                name: "PaymentProviders",
                schema: "sales",
                columns: table => new
                {
                    Code = table.Column<string>(type: "varchar(30)", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    DisplayOrder = table.Column<short>(type: "smallint", nullable: false),
                    CheckoutDescription = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentProviders", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "PickupLocations",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AddressLine = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    City = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    DistrictId = table.Column<int>(type: "int", nullable: false),
                    Phone = table.Column<string>(type: "varchar(20)", nullable: true),
                    OpeningHours = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickupLocations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryMethodZoneRates",
                schema: "sales",
                columns: table => new
                {
                    DeliveryMethodId = table.Column<int>(type: "int", nullable: false),
                    DeliveryZone = table.Column<byte>(type: "tinyint", nullable: false),
                    IsAvailable = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    Fee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    FreeAboveAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    EstimatedDaysMin = table.Column<byte>(type: "tinyint", nullable: false),
                    EstimatedDaysMax = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryMethodZoneRates", x => new { x.DeliveryMethodId, x.DeliveryZone });
                    table.CheckConstraint("CK_DeliveryMethodZoneRates_DeliveryZone", "[DeliveryZone] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_DeliveryMethodZoneRates_EstimatedDays", "[EstimatedDaysMin] <= [EstimatedDaysMax]");
                    table.CheckConstraint("CK_DeliveryMethodZoneRates_Fee", "[Fee] >= 0");
                    table.CheckConstraint("CK_DeliveryMethodZoneRates_FreeAboveAmount", "[FreeAboveAmount] IS NULL OR [FreeAboveAmount] >= 0");
                    table.ForeignKey(
                        name: "FK_DeliveryMethodZoneRates_DeliveryMethods_DeliveryMethodId",
                        column: x => x.DeliveryMethodId,
                        principalSchema: "sales",
                        principalTable: "DeliveryMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderNumber = table.Column<string>(type: "varchar(20)", nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "newid()"),
                    CheckoutIdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerUserId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    StockState = table.Column<byte>(type: "tinyint", nullable: false),
                    CaptureLockedUntilUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    PaymentProviderCode = table.Column<string>(type: "varchar(30)", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EmailNormalized = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Phone = table.Column<string>(type: "varchar(20)", nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    DeliveryDistrictId = table.Column<int>(type: "int", nullable: false),
                    DeliveryDistrictName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    DeliveryZone = table.Column<byte>(type: "tinyint", nullable: false),
                    DeliveryMethodId = table.Column<int>(type: "int", nullable: false),
                    PickupLocationId = table.Column<int>(type: "int", nullable: true),
                    Currency = table.Column<string>(type: "char(3)", nullable: false, defaultValue: "LKR"),
                    ItemsOriginalTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ItemsListTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ItemsSubtotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MarkdownDiscountTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CampaignDiscountTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PromoCodeId = table.Column<int>(type: "int", nullable: true),
                    PromoCodeText = table.Column<string>(type: "varchar(40)", nullable: true),
                    PromoDiscountAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    DeliveryFeeAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DeliveryDiscountAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    GrandTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RefundedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    PlacedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    PaymentDueAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    PaidAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ShippedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeliveredAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CourierId = table.Column<int>(type: "int", nullable: true),
                    CourierName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    TrackingNumber = table.Column<string>(type: "varchar(60)", nullable: true),
                    InvoiceNumber = table.Column<string>(type: "varchar(20)", nullable: true),
                    InvoiceIssuedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    InvoiceSellerSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CustomerNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    InternalNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PlacedIpAddress = table.Column<string>(type: "varchar(45)", nullable: true),
                    AnonymisedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    DeliveryMethodCode = table.Column<string>(type: "varchar(20)", nullable: false),
                    DeliveryMethodName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    EstimatedDaysMax = table.Column<byte>(type: "tinyint", nullable: false),
                    EstimatedDaysMin = table.Column<byte>(type: "tinyint", nullable: false),
                    PickupLocationName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Ship_City = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Ship_Landmark = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Ship_Line1 = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Ship_Line2 = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Ship_Phone = table.Column<string>(type: "varchar(20)", nullable: true),
                    Ship_PostalCode = table.Column<string>(type: "varchar(10)", nullable: true),
                    Ship_RecipientName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.CheckConstraint("CK_Orders_Currency", "[Currency] = 'LKR'");
                    table.CheckConstraint("CK_Orders_DeliveryZone", "[DeliveryZone] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_Orders_InvoiceSellerSnapshotJson", "[InvoiceSellerSnapshotJson] IS NULL OR ISJSON([InvoiceSellerSnapshotJson]) = 1");
                    table.CheckConstraint("CK_Orders_Pickup", "[DeliveryMethodCode] <> 'PICKUP' OR [PickupLocationId] IS NOT NULL");
                    table.CheckConstraint("CK_Orders_ShipAddress", "[DeliveryMethodCode] = 'PICKUP'\nOR ([Ship_RecipientName] IS NOT NULL AND [Ship_Phone] IS NOT NULL\n    AND [Ship_Line1] IS NOT NULL AND [Ship_City] IS NOT NULL)");
                    table.CheckConstraint("CK_Orders_Status", "[Status] BETWEEN 0 AND 7");
                    table.CheckConstraint("CK_Orders_StockState", "[StockState] BETWEEN 0 AND 3");
                    table.CheckConstraint("CK_Orders_Totals", "[ItemsOriginalTotal] - [ItemsListTotal] = [MarkdownDiscountTotal]\nAND [ItemsListTotal] - [ItemsSubtotal] = [CampaignDiscountTotal]\nAND [GrandTotal] = [ItemsSubtotal] - [PromoDiscountAmount] + [DeliveryFeeAmount] - [DeliveryDiscountAmount]\nAND [GrandTotal] >= 0\nAND [RefundedAmount] <= [GrandTotal]");
                    table.ForeignKey(
                        name: "FK_Orders_Couriers_CourierId",
                        column: x => x.CourierId,
                        principalSchema: "sales",
                        principalTable: "Couriers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Orders_DeliveryMethods_DeliveryMethodId",
                        column: x => x.DeliveryMethodId,
                        principalSchema: "sales",
                        principalTable: "DeliveryMethods",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Orders_PaymentProviders_PaymentProviderCode",
                        column: x => x.PaymentProviderCode,
                        principalSchema: "sales",
                        principalTable: "PaymentProviders",
                        principalColumn: "Code");
                    table.ForeignKey(
                        name: "FK_Orders_PickupLocations_PickupLocationId",
                        column: x => x.PickupLocationId,
                        principalSchema: "sales",
                        principalTable: "PickupLocations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Orders_Users_CustomerUserId",
                        column: x => x.CustomerUserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OrderLines",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    LineNumber = table.Column<short>(type: "smallint", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    VariantId = table.Column<int>(type: "int", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProductCode = table.Column<string>(type: "varchar(40)", nullable: false),
                    Sku = table.Column<string>(type: "varchar(64)", nullable: false),
                    ColourName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SizeLabel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ThumbnailStorageKey = table.Column<string>(type: "varchar(400)", nullable: true),
                    Quantity = table.Column<short>(type: "smallint", nullable: false),
                    UnitOriginalPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    UnitListPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SaleCampaignId = table.Column<int>(type: "int", nullable: true),
                    LineSubtotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PromoDiscountAllocated = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RefundedQuantity = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderLines", x => x.Id);
                    table.CheckConstraint("CK_OrderLines_LineSubtotal", "[LineSubtotal] = [UnitPrice] * [Quantity]");
                    table.CheckConstraint("CK_OrderLines_LineTotal", "[LineTotal] = [LineSubtotal] - [PromoDiscountAllocated] AND [LineTotal] >= 0");
                    table.CheckConstraint("CK_OrderLines_PriceLadder", "[UnitOriginalPrice] >= [UnitListPrice] AND [UnitListPrice] >= [UnitPrice] AND [UnitPrice] > 0");
                    table.CheckConstraint("CK_OrderLines_PromoDiscountAllocated", "[PromoDiscountAllocated] >= 0");
                    table.CheckConstraint("CK_OrderLines_Quantity", "[Quantity] > 0");
                    table.CheckConstraint("CK_OrderLines_RefundedQuantity", "[RefundedQuantity] >= 0 AND [RefundedQuantity] <= [Quantity]");
                    table.ForeignKey(
                        name: "FK_OrderLines_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "catalog",
                        principalTable: "Categories",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrderLines_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "catalog",
                        principalTable: "Departments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrderLines_Orders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "sales",
                        principalTable: "Orders",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrderLines_ProductVariants_VariantId",
                        column: x => x.VariantId,
                        principalSchema: "catalog",
                        principalTable: "ProductVariants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrderLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OrderStatusHistory",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    FromStatus = table.Column<byte>(type: "tinyint", nullable: true),
                    ToStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    ChangedByUserId = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderStatusHistory", x => x.Id);
                    table.CheckConstraint("CK_OrderStatusHistory_FromStatus", "[FromStatus] IS NULL OR [FromStatus] BETWEEN 0 AND 7");
                    table.CheckConstraint("CK_OrderStatusHistory_Source", "[Source] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_OrderStatusHistory_ToStatus", "[ToStatus] BETWEEN 0 AND 7");
                    table.ForeignKey(
                        name: "FK_OrderStatusHistory_Orders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "sales",
                        principalTable: "Orders",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    ProviderCode = table.Column<string>(type: "varchar(30)", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    IsOrderSettlement = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", nullable: false, defaultValue: "LKR"),
                    ProviderCurrency = table.Column<string>(type: "char(3)", nullable: true),
                    ProviderAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    FxBufferPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    ProviderReference = table.Column<string>(type: "varchar(100)", nullable: true),
                    ProviderTransactionId = table.Column<string>(type: "varchar(100)", nullable: true),
                    PayerReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ProofAssetId = table.Column<int>(type: "int", nullable: true),
                    CaptureStartedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    FailureCode = table.Column<string>(type: "varchar(50)", nullable: true),
                    FailureMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ApprovedByUserId = table.Column<int>(type: "int", nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ApprovalNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RefundedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.CheckConstraint("CK_Payments_Currency", "[Currency] = 'LKR'");
                    table.CheckConstraint("CK_Payments_IsOrderSettlement", "[IsOrderSettlement] = 0 OR [Status] IN (3, 8, 9)");
                    table.CheckConstraint("CK_Payments_RefundedAmount", "[RefundedAmount] >= 0");
                    table.CheckConstraint("CK_Payments_Status", "[Status] BETWEEN 0 AND 9");
                    table.ForeignKey(
                        name: "FK_Payments_MediaAssets_ProofAssetId",
                        column: x => x.ProofAssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Payments_Orders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "sales",
                        principalTable: "Orders",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Payments_PaymentProviders_ProviderCode",
                        column: x => x.ProviderCode,
                        principalSchema: "sales",
                        principalTable: "PaymentProviders",
                        principalColumn: "Code");
                });

            migrationBuilder.CreateTable(
                name: "PaymentEvents",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderCode = table.Column<string>(type: "varchar(30)", nullable: false),
                    EventSource = table.Column<byte>(type: "tinyint", nullable: false),
                    ProviderEventId = table.Column<string>(type: "varchar(100)", nullable: true),
                    EventType = table.Column<string>(type: "varchar(100)", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "varchar(160)", nullable: false),
                    PaymentId = table.Column<int>(type: "int", nullable: true),
                    OrderId = table.Column<int>(type: "int", nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProcessingStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    AttemptCount = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentEvents", x => x.Id);
                    table.CheckConstraint("CK_PaymentEvents_AttemptCount", "[AttemptCount] >= 0");
                    table.CheckConstraint("CK_PaymentEvents_EventSource", "[EventSource] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_PaymentEvents_PayloadJson", "ISJSON([PayloadJson]) = 1");
                    table.CheckConstraint("CK_PaymentEvents_ProcessingStatus", "[ProcessingStatus] BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_PaymentEvents_Orders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "sales",
                        principalTable: "Orders",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PaymentEvents_PaymentProviders_ProviderCode",
                        column: x => x.ProviderCode,
                        principalSchema: "sales",
                        principalTable: "PaymentProviders",
                        principalColumn: "Code");
                    table.ForeignKey(
                        name: "FK_PaymentEvents_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalSchema: "sales",
                        principalTable: "Payments",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Refunds",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    PaymentId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", nullable: false, defaultValue: "LKR"),
                    ProviderAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Method = table.Column<byte>(type: "tinyint", nullable: false),
                    ProviderRefundId = table.Column<string>(type: "varchar(100)", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    RestockItems = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "sysutcdatetime()"),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refunds", x => x.Id);
                    table.CheckConstraint("CK_Refunds_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_Refunds_Currency", "[Currency] = 'LKR'");
                    table.CheckConstraint("CK_Refunds_Method", "[Method] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_Refunds_ProviderAmount", "[ProviderAmount] IS NULL OR [ProviderAmount] > 0");
                    table.CheckConstraint("CK_Refunds_Status", "[Status] BETWEEN 0 AND 2");
                    table.ForeignKey(
                        name: "FK_Refunds_Orders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "sales",
                        principalTable: "Orders",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Refunds_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalSchema: "sales",
                        principalTable: "Payments",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Carts_DeliveryMethodId",
                schema: "sales",
                table: "Carts",
                column: "DeliveryMethodId");

            migrationBuilder.CreateIndex(
                name: "UX_Couriers_Name",
                schema: "sales",
                table: "Couriers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DeliveryMethods_Code",
                schema: "sales",
                table: "DeliveryMethods",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_CategoryId",
                schema: "sales",
                table: "OrderLines",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_DepartmentId",
                schema: "sales",
                table: "OrderLines",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_Product",
                schema: "sales",
                table: "OrderLines",
                column: "ProductId")
                .Annotation("SqlServer:Include", new[] { "OrderId", "Quantity", "LineTotal" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_SaleCampaignId",
                schema: "sales",
                table: "OrderLines",
                column: "SaleCampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_Variant",
                schema: "sales",
                table: "OrderLines",
                column: "VariantId")
                .Annotation("SqlServer:Include", new[] { "OrderId", "Quantity" });

            migrationBuilder.CreateIndex(
                name: "UX_OrderLines_Order_Line",
                schema: "sales",
                table: "OrderLines",
                columns: new[] { "OrderId", "LineNumber" },
                unique: true)
                .Annotation("SqlServer:Include", new[] { "ProductId", "VariantId", "DepartmentId", "CategoryId", "Quantity", "LineTotal" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CourierId",
                schema: "sales",
                table: "Orders",
                column: "CourierId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Customer_Placed",
                schema: "sales",
                table: "Orders",
                columns: new[] { "CustomerUserId", "PlacedAtUtc" },
                descending: new[] { false, true },
                filter: "[CustomerUserId] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "GrandTotal", "Status", "PaidAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_DeliveryDistrictId",
                schema: "sales",
                table: "Orders",
                column: "DeliveryDistrictId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_DeliveryMethodId",
                schema: "sales",
                table: "Orders",
                column: "DeliveryMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_EmailNormalized",
                schema: "sales",
                table: "Orders",
                column: "EmailNormalized");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Paid",
                schema: "sales",
                table: "Orders",
                column: "PaidAtUtc",
                filter: "[PaidAtUtc] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "GrandTotal", "DeliveryDistrictId", "PaymentProviderCode", "CustomerUserId", "EmailNormalized", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_PaymentProviderCode",
                schema: "sales",
                table: "Orders",
                column: "PaymentProviderCode");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Pending_Due",
                schema: "sales",
                table: "Orders",
                column: "PaymentDueAtUtc",
                filter: "[Status] = 0")
                .Annotation("SqlServer:Include", new[] { "CaptureLockedUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Phone",
                schema: "sales",
                table: "Orders",
                column: "Phone");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_PickupLocationId",
                schema: "sales",
                table: "Orders",
                column: "PickupLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Placed",
                schema: "sales",
                table: "Orders",
                column: "PlacedAtUtc",
                descending: new bool[0])
                .Annotation("SqlServer:Include", new[] { "OrderNumber", "Status", "GrandTotal", "FirstName", "LastName" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Promo_Email",
                schema: "sales",
                table: "Orders",
                columns: new[] { "PromoCodeId", "EmailNormalized" },
                filter: "[PromoCodeId] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "Status", "PaidAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Shipped",
                schema: "sales",
                table: "Orders",
                column: "ShippedAtUtc",
                filter: "[Status] = 3");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status_Placed",
                schema: "sales",
                table: "Orders",
                columns: new[] { "Status", "PlacedAtUtc" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "GrandTotal", "PaymentProviderCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_StockReserved",
                schema: "sales",
                table: "Orders",
                column: "Id",
                filter: "[StockState] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_Orders_CheckoutIdempotencyKey",
                schema: "sales",
                table: "Orders",
                column: "CheckoutIdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Orders_InvoiceNumber",
                schema: "sales",
                table: "Orders",
                column: "InvoiceNumber",
                unique: true,
                filter: "[InvoiceNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Orders_OrderNumber",
                schema: "sales",
                table: "Orders",
                column: "OrderNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Orders_PublicId",
                schema: "sales",
                table: "Orders",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderStatusHistory_Order_Occurred",
                schema: "sales",
                table: "OrderStatusHistory",
                columns: new[] { "OrderId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_OrderId",
                schema: "sales",
                table: "PaymentEvents",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_PaymentId",
                schema: "sales",
                table: "PaymentEvents",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_ProviderCode",
                schema: "sales",
                table: "PaymentEvents",
                column: "ProviderCode");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_Retry",
                schema: "sales",
                table: "PaymentEvents",
                column: "NextAttemptAtUtc",
                filter: "[ProcessingStatus] IN (0, 3)");

            migrationBuilder.CreateIndex(
                name: "UX_PaymentEvents_IdempotencyKey",
                schema: "sales",
                table: "PaymentEvents",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_BankQueue",
                schema: "sales",
                table: "Payments",
                column: "CreatedAtUtc",
                filter: "[ProviderCode] = 'BankTransfer' AND [Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Capturing",
                schema: "sales",
                table: "Payments",
                column: "CaptureStartedAtUtc",
                filter: "[Status] = 2");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Exceptions",
                schema: "sales",
                table: "Payments",
                column: "CreatedAtUtc",
                filter: "[Status] = 7");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ProofAssetId",
                schema: "sales",
                table: "Payments",
                column: "ProofAssetId");

            migrationBuilder.CreateIndex(
                name: "UX_Payments_OneOpen",
                schema: "sales",
                table: "Payments",
                column: "OrderId",
                unique: true,
                filter: "[Status] IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "UX_Payments_OneSettlement",
                schema: "sales",
                table: "Payments",
                column: "OrderId",
                unique: true,
                filter: "[IsOrderSettlement] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_Payments_Provider_Reference",
                schema: "sales",
                table: "Payments",
                columns: new[] { "ProviderCode", "ProviderReference" },
                unique: true,
                filter: "[ProviderReference] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Payments_Provider_Txn",
                schema: "sales",
                table: "Payments",
                columns: new[] { "ProviderCode", "ProviderTransactionId" },
                unique: true,
                filter: "[ProviderTransactionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PickupLocations_DistrictId",
                schema: "sales",
                table: "PickupLocations",
                column: "DistrictId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_Completed",
                schema: "sales",
                table: "Refunds",
                column: "CompletedAtUtc",
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_OrderId",
                schema: "sales",
                table: "Refunds",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PaymentId",
                schema: "sales",
                table: "Refunds",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "UX_Refunds_ProviderRefundId",
                schema: "sales",
                table: "Refunds",
                column: "ProviderRefundId",
                unique: true,
                filter: "[ProviderRefundId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Carts_DeliveryMethods_DeliveryMethodId",
                schema: "sales",
                table: "Carts",
                column: "DeliveryMethodId",
                principalSchema: "sales",
                principalTable: "DeliveryMethods",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Carts_DeliveryMethods_DeliveryMethodId",
                schema: "sales",
                table: "Carts");

            migrationBuilder.DropTable(
                name: "DeliveryMethodZoneRates",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "NumberSeries",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "OrderLines",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "OrderStatusHistory",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "PaymentEvents",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "Refunds",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "Payments",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "Orders",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "Couriers",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "DeliveryMethods",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "PaymentProviders",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "PickupLocations",
                schema: "sales");

            migrationBuilder.DropIndex(
                name: "IX_Carts_DeliveryMethodId",
                schema: "sales",
                table: "Carts");

            migrationBuilder.DropColumn(
                name: "DeliveryMethodId",
                schema: "sales",
                table: "Carts");
        }
    }
}
