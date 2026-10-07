using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Auditing;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Application.Catalog.Admin.Products.Abstractions;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Application.Marketing.Newsletter;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Wishlist.Abstractions;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Infrastructure.Admin.Auditing;
using SpencerDenim.Infrastructure.Admin.Users;
using SpencerDenim.Infrastructure.Catalog;
using SpencerDenim.Infrastructure.Content;
using SpencerDenim.Infrastructure.Marketing;
using SpencerDenim.Infrastructure.Media;
using SpencerDenim.Infrastructure.Health;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Interceptors;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.Infrastructure.Sales;
using SpencerDenim.Infrastructure.Sales.Payments;
using SpencerDenim.Infrastructure.Settings;
using SpencerDenim.Infrastructure.Time;

namespace SpencerDenim.Infrastructure;

public static class DependencyInjection
{
    private static readonly TimeSpan DatabaseHealthCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Registers infrastructure adapters: SQL Server persistence, clock and readiness checks.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, SystemClock>();

        // Bound lazily from IConfiguration, so values set by hosts and test factories are honoured.
        services.AddOptions<DatabaseOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                configuration.GetSection(DatabaseOptions.SectionName).Bind(options);
                options.ConnectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName) ?? string.Empty;
            })
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                "The database connection string is missing. Set the ConnectionStrings__Database environment variable.")
            .Validate(options => options.MaxRetryCount >= 0, "Database:MaxRetryCount must be zero or more.")
            .Validate(options => options.CommandTimeoutSeconds > 0, "Database:CommandTimeoutSeconds must be greater than zero.")
            .Validate(
                options => options.CompatibilityLevel is >= DatabaseOptions.MinimumCompatibilityLevel and <= DatabaseOptions.MaximumCompatibilityLevel,
                $"Database:CompatibilityLevel must be between {DatabaseOptions.MinimumCompatibilityLevel} and {DatabaseOptions.MaximumCompatibilityLevel} (150 = SQL Server 2019, 170 = SQL Server 2025 / Azure SQL).")
            .ValidateOnStart();

        services.AddOptions<CatalogOptions>()
            .BindConfiguration(CatalogOptions.SectionName)
            .Validate(options => options.DefaultLowStockThreshold >= 0, "Catalog:DefaultLowStockThreshold must be zero or more.")
            .Validate(options => options.NewArrivalDays >= 0, "Catalog:NewArrivalDays must be zero or more.")
            .Validate(options => options.BestSellerTopN > 0, "Catalog:BestSellerTopN must be greater than zero.")
            .Validate(options => options.DefaultPageSize > 0, "Catalog:DefaultPageSize must be greater than zero.")
            .Validate(options => options.MaxPageSize >= options.DefaultPageSize, "Catalog:MaxPageSize must be at least Catalog:DefaultPageSize.")
            .Validate(options => options.RelatedProductCount >= 0, "Catalog:RelatedProductCount must be zero or more.")
            .Validate(options => options.SitemapMaxProducts > 0, "Catalog:SitemapMaxProducts must be greater than zero.")
            .ValidateOnStart();

        // The bound value, for use cases in the Application layer: that project deliberately takes
        // no options package, so a handler that needs a setting asks for the settings object rather
        // than for IOptions<T>. IOptions<T> is itself a singleton, so this is the same instance.
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<CatalogOptions>>().Value);

        // The one bank-transfer setting that belongs in ops.Settings and has no table to live in yet
        // (ADM-22 — see BankTransferOptions). Bound and validated here, and exposed as the bare value for
        // the same reason CatalogOptions is.
        services.AddOptions<BankTransferOptions>()
            .BindConfiguration(BankTransferOptions.SectionName)
            .Validate(
                options => options.BankTransferExpiryHours > 0,
                "Orders:BankTransferExpiryHours must be greater than zero: an order placed with a payment "
                + "deadline in the past would be expired by the first run of the expiry job.")
            .ValidateOnStart();

        services.AddSingleton(provider => provider.GetRequiredService<IOptions<BankTransferOptions>>().Value);

        services.AddScoped<AuditAndSoftDeleteInterceptor>();

        services.AddDbContext<AppDbContext>((serviceProvider, builder) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

            builder.UseSqlServer(options.ConnectionString, sql =>
            {
                sql.EnableRetryOnFailure(options.MaxRetryCount);
                sql.UseCompatibilityLevel(options.CompatibilityLevel);
                sql.CommandTimeout(options.CommandTimeoutSeconds);
                sql.MigrationsHistoryTable(AppDbContext.MigrationsHistoryTable, AppDbContext.MigrationsHistorySchema);
            });

            builder.AddInterceptors(serviceProvider.GetRequiredService<AuditAndSoftDeleteInterceptor>());

            // Anything else registered as IInterceptor joins the pipeline. Nothing does in production
            // today; it is EF Core's documented extension point and lets a test count commands or
            // capture SQL without a second composition root.
            builder.AddInterceptors(serviceProvider.GetServices<IInterceptor>());
        });

        services.AddScoped<IDepartmentCatalogReader, DepartmentCatalogReader>();

        // `ProductCatalogReader` serves two ports: the storefront catalogue (PLP, PDP, SRCH) and the
        // wishlist's "cards for these slugs" (WISH-01). It is registered as itself and both ports resolve
        // to it, so one scope gets one instance however many ports it is asked for — rather than
        // `AddScoped<IWishlistCatalogReader, ProductCatalogReader>()`, which would build a second reader
        // (and a second `CatalogOptions` snapshot) for a request that used both.
        services.AddScoped<ProductCatalogReader>();
        services.AddScoped<IProductCatalogReader>(provider => provider.GetRequiredService<ProductCatalogReader>());
        services.AddScoped<IWishlistCatalogReader>(provider => provider.GetRequiredService<ProductCatalogReader>());

        services.AddScoped<ISitemapCatalogReader, SitemapCatalogReader>();
        services.AddScoped<IAuditLogReader, AuditLogReader>();

        // Admin catalogue structure (ADM-11, W1-12).
        services.AddScoped<IAdminCatalogReader, AdminCatalogReader>();
        services.AddScoped<ICatalogStructureStore, CatalogStructureStore>();

        // Admin products (ADM-10, W1-03). Same split as above and for the same reason: the read side has
        // to be able to see an unpublished product, which is the only kind there is before it is finished.
        services.AddScoped<IAdminProductReader, AdminProductReader>();
        services.AddScoped<IProductWriteStore, ProductWriteStore>();

        // Users & Roles management (W1-08, issue #8).
        services.AddScoped<IStaffUserDirectoryReader, StaffUserDirectoryReader>();
        services.AddScoped<IRoleDirectoryReader, RoleDirectoryReader>();
        services.AddScoped<IStaffUserRoleManagementStore, StaffUserRoleManagementStore>();

        // Content and newsletter (W2-01, HOME-01..10, GLB-03).
        services.AddScoped<IHomeContentReader, HomeContentReader>();

        // Content pages and the contact enquiry (W2-11, GLB-10).
        //
        // The sanitiser is a singleton: it holds the allow-list and nothing else, and builds a fresh
        // HtmlSanitizer per call so its per-call removal handlers cannot race (see HtmlSanitiser).
        services.AddSingleton<IHtmlSanitiser, HtmlSanitiser>();
        services.AddScoped<IContentPageReader, ContentPageReader>();
        services.AddScoped<IAdminContentReader, AdminContentReader>();
        services.AddScoped<IContentPageStore, ContentPageStore>();
        services.AddScoped<IContactEnquiryStore, ContactEnquiryStore>();
        services.AddScoped<INewsletterSubscriptions, NewsletterSubscriptions>();

        // Doc 08 §3.7's homepage cache. A singleton, because a cache scoped to one request caches
        // nothing at all.
        services.AddSingleton<IHomePageCache, HomePageCache>();
        services.AddSingleton<IContentPageLinksCache, ContentPageLinksCache>();

        // The bag (W2-09, CART-01/02). The store writes with doc 08 §3.5's item-level upserts and the
        // reader reprices every line from the catalogue on each read (doc 08 §6).
        services.AddScoped<ICartStore, CartStore>();
        services.AddScoped<ICartReader, CartReader>();

        // Settings (ADM-22, GLB-02). The write store and the storefront reader are separate ports on
        // purpose: the public one has no method that could reach a secret, a permission or an unseeded key.
        services.AddScoped<ISettingsStore, SettingsStore>();
        services.AddScoped<ISiteSettingsReader, SiteSettingsReader>();
        services.AddScoped<ISettingSecretProtector, SettingSecretProtector>();

        // Doc 08 §3.8's sixty-second cache. A singleton, because a cache scoped to one request caches
        // nothing — and this payload is read during the server render of every page on the site.
        services.AddSingleton<ISiteSettingsCache, SiteSettingsCache>();

        // Checkout (W3-02, CHK-01..04). `NumberSeriesAllocator` implements doc 08 §3.5's
        // UPDATE … OUTPUT deleted.NextValue exactly; `OrderPlacementStore` is doc 08 §5.4's one
        // transaction; `CheckoutReader` is the single repricing read where an order's prices are frozen.
        services.AddScoped<ICheckoutReader, CheckoutReader>();
        services.AddScoped<INumberSeriesAllocator, NumberSeriesAllocator>();
        services.AddScoped<IOrderPlacementStore, OrderPlacementStore>();

        // Payments (W3-03, PAY-01/PAY-03). **Registering a provider is the whole act of adding one:** the
        // registry is built from whatever IPaymentProvider implementations are here, and checkout asks it
        // by code. PayPalProvider (W4-01) is one more AddScoped line on this list and nothing else.
        services.AddScoped<IPaymentProvider, BankTransferProvider>();
        services.AddScoped<IPaymentProviderRegistry, PaymentProviderRegistry>();

        // Singleton, because "once" means once for the process: the provider itself is per request, and a
        // warning that fires per request is noise rather than a signal (see BankDetailsWarningOnce).
        services.AddSingleton<BankDetailsWarningOnce>();
        services.AddScoped<IBankTransferSettingsReader, BankTransferSettingsReader>();

        // Doc 11 P7 steps 3, 6 and 8, and doc 08 §4.1/§4.3: the customer's reference and slip, and the
        // one transaction behind approve, reject and accept-late.
        services.AddScoped<IBankTransferReader, BankTransferReader>();

        // The same class behind a provider-neutral port, so the order page can ask "has the
        // customer sent us anything about paying" without naming a gateway (PAY-03). Resolved
        // through the registered IBankTransferReader rather than registered twice, so one request
        // gets one instance and one DbContext.
        services.AddScoped<IOrderPaymentProofReader>(provider =>
            (BankTransferReader)provider.GetRequiredService<IBankTransferReader>());
        services.AddScoped<IBankTransferReviewStore, BankTransferReviewStore>();
        services.AddScoped<IOrderExpiryStore, OrderExpiryStore>();
        services.AddScoped<IBankTransferProofStore, BankTransferProofStore>();

        services.AddMediaLibrary();

        services.AddOptions<SeedOptions>().BindConfiguration(SeedOptions.SectionName);

        // Written by the seeder, read by the readiness check. A singleton because it is this host's
        // answer to "did I come up able to take an order", which outlives any request scope.
        services.AddSingleton<ReferenceDataState>();

        services.AddScoped<CatalogReferenceDataSeeder>();
        services.AddScoped<SalesReferenceDataSeeder>();
        services.AddScoped<HomeContentSeeder>();
        services.AddScoped<SettingsSeeder>();
        services.AddScoped<DemoCatalogueSeeder>();
        services.AddScoped<CatalogSeedRunner>();
        services.AddHostedService<CatalogSeedHostedService>();

        // Doc 08 §4.1/§5.6 and doc 11 P10 flow B: every five minutes, release the stock and the promo use
        // of orders nobody paid for. Issue #107's real mitigation.
        services.AddHostedService<ExpireUnpaidOrdersService>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(
                name: "sqlserver",
                failureStatus: null,
                tags: [HealthCheckTags.Ready],
                timeout: DatabaseHealthCheckTimeout)

            // **`CanConnectAsync()` is not enough to call a host ready, and that was a real defect.** A
            // database that is connectable but has no migrations applied answered `/health/ready` with
            // `200 Healthy` while every `/api/checkout` returned 500, so an orchestrator routed live
            // traffic to a shop that could not take an order. This check reports what the seeder actually
            // managed; see `ReferenceDataHealthCheck`. No timeout, because it never does any I/O.
            .AddCheck<ReferenceDataHealthCheck>(
                name: "referencedata",
                failureStatus: null,
                tags: [HealthCheckTags.Ready]);

        services.AddAdminIdentity();

        return services;
    }
}
