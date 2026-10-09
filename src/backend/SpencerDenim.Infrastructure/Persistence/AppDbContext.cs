using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Media;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Reference;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Identity;

namespace SpencerDenim.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for the platform (doc 08 §10.1). Entity configurations live in
/// <c>Persistence/Configurations</c> per module and are picked up from this assembly.
/// <para>
/// A plain <see cref="DbContext"/> rather than <c>IdentityDbContext</c>: <see cref="AppUser"/> and
/// <see cref="AppRole"/> carry doc 08's extra columns, and the supporting Identity tables (<c>UserRoles</c>,
/// <c>RoleClaims</c>, <c>UserClaims</c>, <c>UserLogins</c>, <c>UserTokens</c>) are configured explicitly via
/// <c>IEntityTypeConfiguration&lt;T&gt;</c> in <c>Persistence/Configurations/Identity</c> instead, so every
/// table name/shape matches doc 08 §3.1 exactly.
/// </para>
/// </summary>
/// <remarks>
/// Mapping rules for entity configurations (doc 08 §1.4 and §10.1):
/// <list type="bullet">
/// <item><description>
/// JSON columns are always mapped explicitly with <c>HasColumnType("nvarchar(max)")</c>, including
/// <c>ToJson()</c> mappings, plus an <c>ISJSON(col) = 1</c> check constraint. EF Core must never pick
/// the native <c>json</c> type, which SQL Server 2025 and Azure SQL offer but LocalDB 2019 and bacpac
/// tooling do not handle the same way.
/// </description></item>
/// <item><description>
/// The database collation <c>Latin1_General_100_CI_AS_SC</c> is set when the database is created
/// (provisioning script), never with <c>UseCollation</c>, so migrations do not try to alter it.
/// </description></item>
/// </list>
/// </remarks>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Schema that holds the EF migrations history table (doc 08 §10.1).</summary>
    public const string MigrationsHistorySchema = "ops";

    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// Doc 08 §1.2's operational schema: the migrations history table, <c>AuditLogs</c>,
    /// <c>Settings</c>, <c>EmailOutbox</c> and <c>AdminAlerts</c>. The same string as <see cref="MigrationsHistorySchema"/> and kept separate from
    /// it — one names where EF records its own bookkeeping, the other names a module's tables, and a
    /// future decision to move the history table must not silently move <c>ops.Settings</c> with it.
    /// </summary>
    public const string OpsSchema = "ops";

    /// <summary>Doc 08 §1.2 schemas.</summary>
    public const string CatalogSchema = "catalog";

    public const string MediaSchema = "media";

    public const string InventorySchema = "inventory";

    public const string ContentSchema = "content";

    public const string MarketingSchema = "marketing";

    /// <summary>
    /// Doc 08 §1.2's shared reference data: <c>Provinces</c> and <c>Districts</c>. Created by W3-02's
    /// migration, because <c>sales.Orders.DeliveryDistrictId</c> is <c>NOT NULL</c> and no order can be
    /// written without it (doc 08 §3.1 heads both tables "GLB-06, CHK-01").
    /// </summary>
    public const string ReferenceSchema = "ref";

    /// <summary>
    /// Doc 08 §1.2. Created by W2-09's migration with <c>Carts</c> and <c>CartItems</c>; W3-03 added the rest
    /// of §3.5 — number series, payment providers, delivery, orders, payments and refunds. W2-08 added the
    /// last of them, <c>WishlistItems</c>, so §3.5's fifteen tables are now all here.
    /// </summary>
    public const string SalesSchema = "sales";

    /// <summary>Name of the EF Core 10 named query filter that hides soft-deleted rows (doc 08 §10.2).</summary>
    public const string SoftDeleteFilter = "SoftDelete";

    public DbSet<AppUser> Users => Set<AppUser>();

    public DbSet<AppRole> Roles => Set<AppRole>();

    public DbSet<IdentityUserRole<int>> UserRoles => Set<IdentityUserRole<int>>();

    public DbSet<IdentityRoleClaim<int>> RoleClaims => Set<IdentityRoleClaim<int>>();

    public DbSet<IdentityUserClaim<int>> UserClaims => Set<IdentityUserClaim<int>>();

    public DbSet<IdentityUserLogin<int>> UserLogins => Set<IdentityUserLogin<int>>();

    public DbSet<IdentityUserToken<int>> UserTokens => Set<IdentityUserToken<int>>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<AgeGroup> AgeGroups => Set<AgeGroup>();

    public DbSet<AttributeOption> AttributeOptions => Set<AttributeOption>();

    public DbSet<ColourFamily> ColourFamilies => Set<ColourFamily>();

    public DbSet<Size> Sizes => Set<Size>();

    public DbSet<SizeGuide> SizeGuides => Set<SizeGuide>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductStats> ProductStats => Set<ProductStats>();

    public DbSet<ProductSearchIndex> ProductSearchIndexes => Set<ProductSearchIndex>();

    public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();

    public DbSet<ProductColour> ProductColours => Set<ProductColour>();

    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    public DbSet<ProductMedia> ProductMedia => Set<ProductMedia>();

    public DbSet<RelatedProduct> RelatedProducts => Set<RelatedProduct>();

    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();

    public DbSet<MediaUsage> MediaUsages => Set<MediaUsage>();

    public DbSet<StockLevel> StockLevels => Set<StockLevel>();

    public DbSet<HomeSection> HomeSections => Set<HomeSection>();

    public DbSet<HomeSectionItem> HomeSectionItems => Set<HomeSectionItem>();

    public DbSet<AnnouncementMessage> AnnouncementMessages => Set<AnnouncementMessage>();

    /// <summary><c>content.ContentPages</c> (doc 08 §3.7, GLB-10). Created by W2-11's migration.</summary>
    public DbSet<ContentPage> ContentPages => Set<ContentPage>();

    /// <summary><c>content.ContactSubmissions</c> (doc 08 §3.7, GLB-10). Created by W2-11's migration.</summary>
    public DbSet<ContactSubmission> ContactSubmissions => Set<ContactSubmission>();

    public DbSet<NewsletterSubscriber> NewsletterSubscribers => Set<NewsletterSubscriber>();

    public DbSet<Province> Provinces => Set<Province>();

    public DbSet<District> Districts => Set<District>();

    public DbSet<Cart> Carts => Set<Cart>();

    public DbSet<CartItem> CartItems => Set<CartItem>();

    /// <summary>
    /// <c>sales.WishlistItems</c> (doc 08 §3.5, WISH-01), created by W2-08's migration.
    /// <para>
    /// <b>Nothing in the application reads or writes it yet.</b> Every row needs a registered customer
    /// (<see cref="WishlistItem.CustomerUserId"/> is <c>NOT NULL</c>, as doc 08 §3.5 specifies) and there
    /// are no customer accounts until W3-01 / ACC-01. A guest's wishlist lives in the browser — doc 08
    /// §1.7, doc 02's WISH-01 line and doc 11 §553 all say so — and W3-01 owns the merge that turns it
    /// into rows here. The set exists so that the schema, the integration tests and the merge's target
    /// are all in place before the task that needs them.
    /// </para>
    /// </summary>
    public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>();

    public DbSet<NumberSeries> NumberSeries => Set<NumberSeries>();

    public DbSet<PaymentProvider> PaymentProviders => Set<PaymentProvider>();

    public DbSet<DeliveryMethod> DeliveryMethods => Set<DeliveryMethod>();

    public DbSet<DeliveryMethodZoneRate> DeliveryMethodZoneRates => Set<DeliveryMethodZoneRate>();

    public DbSet<PickupLocation> PickupLocations => Set<PickupLocation>();

    public DbSet<Courier> Couriers => Set<Courier>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<OrderStatusHistoryEntry> OrderStatusHistory => Set<OrderStatusHistoryEntry>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();

    public DbSet<Refund> Refunds => Set<Refund>();

    /// <summary><c>ops.Settings</c> (doc 08 §3.8, ADM-22). Created by W1-16's migration.</summary>
    public DbSet<Setting> Settings => Set<Setting>();

    /// <summary>
    /// <c>ops.EmailOutbox</c> (doc 08 §3.8, ACC-03). Created by W3-06's migration.
    /// <para>
    /// Rows are added through <c>IEmailOutboxWriter.Enlist</c> inside the business transaction that the
    /// message is about, never saved on their own; the <c>SendEmails</c> job reads and updates them with
    /// set-based statements. This set exists so both sides compile against one mapping.
    /// </para>
    /// </summary>
    public DbSet<EmailOutboxMessage> EmailOutbox => Set<EmailOutboxMessage>();

    /// <summary>
    /// <c>ops.AdminAlerts</c> (doc 08 §3.8 N2; ADM-04's banner). Created by W3-07's migration.
    /// <para>
    /// Rows are inserted by whatever raised the alert and read by the admin shell's banner through
    /// <c>IAdminAlertReader</c>; the acknowledgement is a conditional <c>UPDATE</c> so that two Super
    /// Admins pressing the button together record one acknowledgement (doc 08 §5.2).
    /// </para>
    /// </summary>
    public DbSet<AdminAlert> AdminAlerts => Set<AdminAlert>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Doc 08 §1.4: money is decimal(18,2). Percentages and averages override this per property.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);

        // Doc 08 §1.4: UTC datetime2(3) everywhere. The converter also stamps DateTimeKind.Utc on read,
        // so a value that has been round-tripped through the database still says it is UTC.
        configurationBuilder.Properties<DateTime>()
            .HaveColumnType("datetime2(3)")
            .HaveConversion<UtcDateTimeConverter>();

        base.ConfigureConventions(configurationBuilder);
    }

    /// <summary>
    /// Doc 08 §10.2. Soft delete is modelled on the four aggregate roots that own it (Products,
    /// Categories, MediaAssets, SizeGuides). Their children keep no filter of their own, which EF warns
    /// about because a child is the required end of a filtered relationship.
    /// <para>
    /// The warning does not apply here: every storefront query starts at a filtered root (see
    /// <c>ProductCatalogReader</c>), so a child of a deleted product is never reached, and the admin
    /// reaches deleted rows deliberately with <c>IgnoreQueryFilters()</c>. Adding the same filter to
    /// every child would put an extra join to Products inside each <c>EXISTS</c> of the listing query
    /// (doc 08 §7.1) for no behavioural gain.
    /// </para>
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        optionsBuilder.ConfigureWarnings(warnings =>
            warnings.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));

        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(null);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
