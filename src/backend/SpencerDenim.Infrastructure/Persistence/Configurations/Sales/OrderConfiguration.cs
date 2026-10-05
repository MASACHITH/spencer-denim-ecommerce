using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Reference;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence.Configurations.Sales;

/// <summary>
/// <c>sales.Orders</c> (doc 08 §3.5) [A] [RV], with the invoice columns folded in.
/// <para>
/// <b>Nothing cascades into or out of this table.</b> Doc 08 §1.6 lists Orders among the rows that are
/// never deleted and does not name any order table in the cascade allow-list, so every relationship here
/// is <c>NO ACTION</c> — including the obvious-looking Orders → OrderLines one, which EF would otherwise
/// make a cascade by convention because the line's <c>OrderId</c> is required.
/// </para>
/// <para>
/// <b>Two of doc 08's foreign keys are still columns only:</b> <c>PromoCodeId</c> here and
/// <c>SaleCampaignId</c> on the line, because <c>marketing.PromoCodes</c> (ADM-13) and
/// <c>marketing.SaleCampaigns</c> (ADM-14) have not been created by any migration. Each is indexed as if
/// the constraint existed, so adding it later is one <c>ALTER TABLE</c>.
/// </para>
/// <para>
/// <b><c>DeliveryDistrictId</c> was the third, and W3-02 has made it a real constraint.</b> W3-03 built
/// the column with its index and no foreign key because <c>ref.Districts</c> did not exist; checkout
/// created that table (doc 08 §3.1 heads it "GLB-06, CHK-01, ADM-08") and so owed the constraint in the
/// same change. <c>NO ACTION</c>, like every other relationship here: a district is reference data that is
/// deactivated rather than deleted, and an order's own <c>DeliveryDistrictName</c> and
/// <c>DeliveryZone</c> are snapshots precisely so the row keeps reading correctly whatever happens to the
/// district afterwards.
/// </para>
/// </summary>
internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Orders", AppDbContext.SalesSchema, ConfigureConstraints);

        builder.HasKey(order => order.Id);

        builder.Property(order => order.OrderNumber).HasColumnType("varchar(20)").IsRequired();
        builder.Property(order => order.PublicId).HasDefaultValueSql("newid()").IsRequired();
        builder.Property(order => order.CheckoutIdempotencyKey).IsRequired();
        builder.Property(order => order.Status).HasColumnType("tinyint").IsRequired();
        builder.Property(order => order.StockState).HasColumnType("tinyint").IsRequired();
        builder.Property(order => order.CaptureLockedUntilUtc);

        builder
            .Property(order => order.PaymentProviderCode)
            .HasColumnType($"varchar({PaymentProviderCodes.MaxCodeLength})")
            .IsRequired();

        builder.Property(order => order.Email).HasMaxLength(256).IsRequired();
        builder.Property(order => order.EmailNormalized).HasMaxLength(256).IsRequired();
        builder.Property(order => order.Phone).HasColumnType("varchar(20)");
        builder.Property(order => order.FirstName).HasMaxLength(80).IsRequired();
        builder.Property(order => order.LastName).HasMaxLength(80);

        builder.Property(order => order.DeliveryDistrictId).IsRequired();
        builder.Property(order => order.DeliveryDistrictName).HasMaxLength(60).IsRequired();
        builder.Property(order => order.DeliveryZone).HasColumnType("tinyint").IsRequired();

        ConfigureShipAddress(builder);
        ConfigureDeliverySnapshot(builder);

        builder
            .Property(order => order.Currency)
            .HasColumnType($"char({SalesCurrency.CodeLength})")
            .HasDefaultValue(SalesCurrency.Lkr)
            .IsRequired();

        builder.Property(order => order.ItemsOriginalTotal).IsRequired();
        builder.Property(order => order.ItemsListTotal).IsRequired();
        builder.Property(order => order.ItemsSubtotal).IsRequired();
        builder.Property(order => order.MarkdownDiscountTotal).IsRequired();
        builder.Property(order => order.CampaignDiscountTotal).IsRequired();
        builder.Property(order => order.PromoCodeText).HasColumnType("varchar(40)");
        builder.Property(order => order.PromoDiscountAmount).HasDefaultValue(0m).IsRequired();
        builder.Property(order => order.DeliveryFeeAmount).IsRequired();
        builder.Property(order => order.DeliveryDiscountAmount).IsRequired();
        builder.Property(order => order.TaxAmount).HasDefaultValue(0m).IsRequired();
        builder.Property(order => order.GrandTotal).IsRequired();
        builder.Property(order => order.RefundedAmount).HasDefaultValue(0m).IsRequired();

        builder.Property(order => order.PlacedAtUtc).IsRequired();
        builder.Property(order => order.PaymentDueAtUtc).IsRequired();

        builder.Property(order => order.CourierName).HasMaxLength(80);
        builder.Property(order => order.TrackingNumber).HasColumnType("varchar(60)");
        builder.Property(order => order.InvoiceNumber).HasColumnType("varchar(20)");
        builder.Property(order => order.InvoiceSellerSnapshotJson).HasColumnType("nvarchar(max)");
        builder.Property(order => order.CustomerNote).HasMaxLength(500);
        builder.Property(order => order.InternalNote).HasMaxLength(2000);
        builder.Property(order => order.CancelReason).HasMaxLength(500);
        builder.Property(order => order.PlacedIpAddress).HasColumnType("varchar(45)");

        builder.ApplyAuditColumns();
        builder.ApplyRowVersion(order => order.RowVersion);

        ConfigureIndexes(builder);
        ConfigureRelationships(builder);
    }

    private static void ConfigureConstraints(TableBuilder<Order> table)
    {
        // Doc 08 §3.5/§4.1: eight statuses and four stock states. The CK is what makes an enum a contract
        // (doc 08 §1.4) — a tinyint on its own would accept 200.
        table.HasCheckConstraint("CK_Orders_Status", "[Status] BETWEEN 0 AND 7");
        table.HasCheckConstraint("CK_Orders_StockState", "[StockState] BETWEEN 0 AND 3");

        // Doc 08 §3.1: the same three zones as ref.Districts.DeliveryZone, snapshotted here (N4).
        table.HasCheckConstraint("CK_Orders_DeliveryZone", "[DeliveryZone] BETWEEN 1 AND 3");

        // Doc 08 §1.4: Currency appears only on Orders, Payments and Refunds, each with a CK 'LKR'.
        table.HasCheckConstraint("CK_Orders_Currency", $"[Currency] = '{SalesCurrency.Lkr}'");

        // Doc 08 §3.5 (DB11), verbatim. It branches on the snapshotted method code rather than joining to
        // DeliveryMethods because a check constraint cannot join — which is the reason the code is on the
        // order row at all. It "stays strict because PDPA anonymisation writes placeholders" (N5): the
        // anonymiser overwrites these columns, it does not NULL them, so the constraint keeps holding for
        // the life of the row.
        table.HasCheckConstraint(
            "CK_Orders_ShipAddress",
            $"""
            [DeliveryMethodCode] = '{DeliveryMethod.PickupCode}'
            OR ([Ship_RecipientName] IS NOT NULL AND [Ship_Phone] IS NOT NULL
                AND [Ship_Line1] IS NOT NULL AND [Ship_City] IS NOT NULL)
            """);

        // Doc 08 §3.5: a pickup order must say which shop. The converse is deliberately not constrained —
        // doc 08 does not forbid a PickupLocationId on a delivered order, and inventing that rule here
        // would be a scope decision, not a transcription.
        table.HasCheckConstraint(
            "CK_Orders_Pickup",
            $"[DeliveryMethodCode] <> '{DeliveryMethod.PickupCode}' OR [PickupLocationId] IS NOT NULL");

        // Doc 08 §3.5's five clauses, as the one constraint doc 08 names. They are in the database rather
        // than only in the totals code because the money is the part nobody can recompute afterwards: if a
        // rounding change ever makes the parts stop adding up, the insert has to fail at the point the bug
        // happens, not be discovered in a reconciliation months later.
        table.HasCheckConstraint(
            "CK_Orders_Totals",
            """
            [ItemsOriginalTotal] - [ItemsListTotal] = [MarkdownDiscountTotal]
            AND [ItemsListTotal] - [ItemsSubtotal] = [CampaignDiscountTotal]
            AND [GrandTotal] = [ItemsSubtotal] - [PromoDiscountAmount] + [DeliveryFeeAmount] - [DeliveryDiscountAmount]
            AND [GrandTotal] >= 0
            AND [RefundedAmount] <= [GrandTotal]
            """);

        // Doc 08 §1.4 (DB15).
        table.HasCheckConstraint(
            "CK_Orders_InvoiceSellerSnapshotJson",
            "[InvoiceSellerSnapshotJson] IS NULL OR ISJSON([InvoiceSellerSnapshotJson]) = 1");
    }

    /// <summary>
    /// Doc 08 §3.5's <c>Ship_*</c> columns, as an EF complex type: one table, no second row identity, no
    /// risk of an order with an orphaned address. Every inner column is nullable on purpose —
    /// <c>CK_Orders_ShipAddress</c> carries the rule, because only it can see the delivery method too.
    /// </summary>
    private static void ConfigureShipAddress(EntityTypeBuilder<Order> builder) =>
        builder.ComplexProperty(order => order.ShipAddress, ship =>
        {
            ship.Property(address => address.RecipientName).HasColumnName("Ship_RecipientName").HasMaxLength(120);
            ship.Property(address => address.Phone).HasColumnName("Ship_Phone").HasColumnType("varchar(20)");
            ship.Property(address => address.Line1).HasColumnName("Ship_Line1").HasMaxLength(150);
            ship.Property(address => address.Line2).HasColumnName("Ship_Line2").HasMaxLength(150);
            ship.Property(address => address.City).HasColumnName("Ship_City").HasMaxLength(80);
            ship.Property(address => address.PostalCode).HasColumnName("Ship_PostalCode").HasColumnType("varchar(10)");
            ship.Property(address => address.Landmark).HasColumnName("Ship_Landmark").HasMaxLength(150);
        });

    /// <summary>
    /// Doc 08 §3.5's <c>Delivery</c> snapshot. The column names carry no prefix because doc 08 writes them
    /// without one — and <c>DeliveryMethodCode</c> in particular is named in two check constraints and in
    /// §4.1's G-18 guard, so it has to be exactly that.
    /// </summary>
    private static void ConfigureDeliverySnapshot(EntityTypeBuilder<Order> builder) =>
        builder.ComplexProperty(order => order.Delivery, delivery =>
        {
            delivery
                .Property(snapshot => snapshot.DeliveryMethodCode)
                .HasColumnName("DeliveryMethodCode")
                .HasColumnType($"varchar({DeliveryMethod.MaxCodeLength})")
                .IsRequired();

            delivery
                .Property(snapshot => snapshot.DeliveryMethodName)
                .HasColumnName("DeliveryMethodName")
                .HasMaxLength(60)
                .IsRequired();

            delivery
                .Property(snapshot => snapshot.PickupLocationName)
                .HasColumnName("PickupLocationName")
                .HasMaxLength(100);

            delivery
                .Property(snapshot => snapshot.EstimatedDaysMin)
                .HasColumnName("EstimatedDaysMin")
                .HasColumnType("tinyint")
                .IsRequired();

            delivery
                .Property(snapshot => snapshot.EstimatedDaysMax)
                .HasColumnName("EstimatedDaysMax")
                .HasColumnType("tinyint")
                .IsRequired();
        });

    private static void ConfigureIndexes(EntityTypeBuilder<Order> builder)
    {
        // Doc 08 §1.3: three unguessable or client-supplied keys, each a unique alternate key and none of
        // them the clustered one.
        builder.HasIndex(order => order.OrderNumber).IsUnique().HasDatabaseName("UX_Orders_OrderNumber");
        builder.HasIndex(order => order.PublicId).IsUnique().HasDatabaseName("UX_Orders_PublicId");

        // The uniqueness *is* the idempotency mechanism: a resubmitted checkout loses the insert race and
        // the caller returns the order that won, instead of reserving the stock twice.
        builder
            .HasIndex(order => order.CheckoutIdempotencyKey)
            .IsUnique()
            .HasDatabaseName("UX_Orders_CheckoutIdempotencyKey");

        // Doc 08 §3.5: filtered NOT NULL, because the number is only issued at Paid and every unpaid order
        // would otherwise collide on NULL. Gapless (allocated inside the Paid transaction), so a duplicate
        // here would be a legal problem, not just a display one.
        builder
            .HasIndex(order => order.InvoiceNumber)
            .IsUnique()
            .HasFilter("[InvoiceNumber] IS NOT NULL")
            .HasDatabaseName("UX_Orders_InvoiceNumber");

        // "My orders", newest first (ACC-02). Filtered so guest orders do not bloat it.
        builder
            .HasIndex(order => new { order.CustomerUserId, order.PlacedAtUtc })
            .IsDescending(false, true)
            .IncludeProperties(order => new { order.GrandTotal, order.Status, order.PaidAtUtc })
            .HasFilter("[CustomerUserId] IS NOT NULL")
            .HasDatabaseName("IX_Orders_Customer_Placed");

        // The admin order list filtered by status (ADM-09).
        builder
            .HasIndex(order => new { order.Status, order.PlacedAtUtc })
            .IsDescending(false, true)
            .IncludeProperties(order => new { order.GrandTotal, order.PaymentProviderCode })
            .HasDatabaseName("IX_Orders_Status_Placed");

        // The unfiltered admin list.
        builder
            .HasIndex(order => order.PlacedAtUtc)
            .IsDescending(true)
            .IncludeProperties(order => new
            {
                order.OrderNumber,
                order.Status,
                order.GrandTotal,
                order.FirstName,
                order.LastName,
            })
            .HasDatabaseName("IX_Orders_Placed");

        // The expiry job's index (every 5 minutes). CaptureLockedUntilUtc is included because the job's
        // guard reads it, so the scan needs no lookup; the filter on Status = 0 is a literal, which is what
        // lets the optimiser match it (doc 08 §1.6, DB28).
        builder
            .HasIndex(order => order.PaymentDueAtUtc)
            .IncludeProperties(order => order.CaptureLockedUntilUtc)
            .HasFilter($"[Status] = {(byte)OrderStatus.PendingPayment}")
            .HasDatabaseName("IX_Orders_Pending_Due");

        // Doc 08 §3.5: "for reconciliation" — which orders still hold a reservation.
        builder
            .HasIndex(order => order.Id)
            .HasFilter($"[StockState] = {(byte)OrderStockState.Reserved}")
            .HasDatabaseName("IX_Orders_StockReserved");

        // The live dashboard (ADM-04/ADM-20). Everything it sums is included, so it never touches the table.
        builder
            .HasIndex(order => order.PaidAtUtc)
            .IncludeProperties(order => new
            {
                order.GrandTotal,
                order.DeliveryDistrictId,
                order.PaymentProviderCode,
                order.CustomerUserId,
                order.EmailNormalized,
                order.Status,
            })
            .HasFilter("[PaidAtUtc] IS NOT NULL")
            .HasDatabaseName("IX_Orders_Paid");

        // Promo limits (§3.6): how many times this code has been used, and by this email. It also gives
        // PromoCodeId a leading index ahead of the foreign key marketing.PromoCodes will carry.
        builder
            .HasIndex(order => new { order.PromoCodeId, order.EmailNormalized })
            .IncludeProperties(order => new { order.Status, order.PaidAtUtc })
            .HasFilter("[PromoCodeId] IS NOT NULL")
            .HasDatabaseName("IX_Orders_Promo_Email");

        // Auto-Delivered (D4). The job also checks the method code is not PICKUP (G-18), which is a column
        // on the row rather than part of the filter, so one index serves both branches.
        builder
            .HasIndex(order => order.ShippedAtUtc)
            .HasFilter($"[Status] = {(byte)OrderStatus.Shipped}")
            .HasDatabaseName("IX_Orders_Shipped");

        builder.HasIndex(order => order.EmailNormalized).HasDatabaseName("IX_Orders_EmailNormalized");
        builder.HasIndex(order => order.Phone).HasDatabaseName("IX_Orders_Phone");

        // Doc 08 §1.6 (DB7): the convention indexes on every FK column, named explicitly so the migration
        // does not depend on EF's naming. DeliveryDistrictId is here although its constraint is deferred
        // (see the class remarks).
        builder.HasIndex(order => order.DeliveryDistrictId).HasDatabaseName("IX_Orders_DeliveryDistrictId");
        builder.HasIndex(order => order.PaymentProviderCode).HasDatabaseName("IX_Orders_PaymentProviderCode");
        builder.HasIndex(order => order.DeliveryMethodId).HasDatabaseName("IX_Orders_DeliveryMethodId");
        builder.HasIndex(order => order.PickupLocationId).HasDatabaseName("IX_Orders_PickupLocationId");
        builder.HasIndex(order => order.CourierId).HasDatabaseName("IX_Orders_CourierId");
    }

    private static void ConfigureRelationships(EntityTypeBuilder<Order> builder)
    {
        // No navigation on AppUser, for the same reason CartConfiguration gives: a sales collection on an
        // Identity entity would put the two modules in one type. NO ACTION — customers are anonymised.
        builder
            .HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(order => order.CustomerUserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(order => order.PaymentProvider)
            .WithMany()
            .HasForeignKey(order => order.PaymentProviderCode)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(order => order.DeliveryMethod)
            .WithMany()
            .HasForeignKey(order => order.DeliveryMethodId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(order => order.PickupLocation)
            .WithMany()
            .HasForeignKey(order => order.PickupLocationId)
            .OnDelete(DeleteBehavior.NoAction);

        builder
            .HasOne(order => order.Courier)
            .WithMany()
            .HasForeignKey(order => order.CourierId)
            .OnDelete(DeleteBehavior.NoAction);

        // No navigation property, for the reason the AppUser relationship above gives: a collection of
        // orders on a `ref` entity would make reference data depend on sales. The constraint is what
        // matters — doc 08 §3.5 calls this an "entity-level FK Districts" — and `DeliveryDistrictName`
        // plus `DeliveryZone` are the snapshots the order reads from, so nothing here ever navigates.
        builder
            .HasOne<District>()
            .WithMany()
            .HasForeignKey(order => order.DeliveryDistrictId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
