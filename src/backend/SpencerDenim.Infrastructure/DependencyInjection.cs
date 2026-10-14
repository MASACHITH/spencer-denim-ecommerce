using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Auditing;
using SpencerDenim.Application.Admin.Customers;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Application.Catalog.Admin.Products.Abstractions;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Application.Marketing.Newsletter;
using SpencerDenim.Application.Marketing.Newsletter.Admin;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Wishlist.Abstractions;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Infrastructure.Admin.Auditing;
using SpencerDenim.Infrastructure.Admin.Customers;
using SpencerDenim.Infrastructure.Admin.Users;
using SpencerDenim.Infrastructure.Catalog;
using SpencerDenim.Infrastructure.Content;
using SpencerDenim.Infrastructure.Marketing;
using SpencerDenim.Infrastructure.Media;
using SpencerDenim.Infrastructure.Ops.Email;
using SpencerDenim.Infrastructure.Health;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Identity.Customers;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Interceptors;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.Infrastructure.Sales;
using SpencerDenim.Infrastructure.Sales.Orders;
using SpencerDenim.Infrastructure.Sales.Payments;
using SpencerDenim.Infrastructure.Settings;
using SpencerDenim.Infrastructure.Time;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Infrastructure.Ops.Alerts;

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

        // Email (W3-06, ACC-03). Doc 08 §3.8 files the SMTP keys under `Settings.Security`, which needs
        // the sensitive-change alert, ops.AdminAlerts and the GrantsAll-only permission — none of which
        // exists — so they are configuration for now, under doc 08's own key names (see EmailOptions).
        services.AddOptions<EmailOptions>()
            .BindConfiguration(EmailOptions.SectionName)
            .Validate(
                options => options.SweepSeconds > 0,
                "Email:SweepSeconds must be greater than zero: a PeriodicTimer with a non-positive period "
                + "throws, which would fault the host on startup.")
            .Validate(
                options => options.BatchSize is > 0 and <= 1000,
                "Email:BatchSize must be between 1 and 1000. It is written into the claim's TOP (n), and "
                + "a batch large enough to hold one transaction open across a thousand SMTP conversations "
                + "is not a batch.")
            .Validate(
                options => options.LeaseMinutes > 0,
                "Email:LeaseMinutes must be greater than zero: a lease that has already expired when it is "
                + "taken would let a second sender send the same message.")
            .Validate(
                options => options.UnconfiguredWarningMinutes > 0,
                "Email:UnconfiguredWarningMinutes must be greater than zero.")
            .Validate(
                options => options.Smtp.Port is > 0 and <= 65535,
                "Email:Smtp:Port must be a valid TCP port.")
            .Validate(
                // An absolute http(s) origin or nothing at all. The point is to refuse the two mistakes
                // that would put a broken link into every email the shop sends — a bare host with no
                // scheme, and a relative path — at startup rather than in a customer's inbox. Unset is
                // valid: the emails then carry no links (see EmailOptions.StorefrontBaseUrl).
                options => IsAbsoluteHttpUrl(options.StorefrontBaseUrl),
                "Email:StorefrontBaseUrl must be an absolute http or https URL, e.g. "
                + "https://example.com, or be left unset.")
            .ValidateOnStart();

        services.AddSingleton(provider => provider.GetRequiredService<IOptions<EmailOptions>>().Value);

        // Pure and stateless over the bound options, so one instance for the process.
        services.AddSingleton<OrderEmailComposer>();

        // The four account emails of doc 11 P11 (W3-01, ACC-01). Pure over the same bound options.
        services.AddSingleton<AccountEmailComposer>();

        // The SMTP boundary. A singleton over the options; it builds a fresh SmtpClient per message,
        // because SmtpClient is not thread-safe (see SmtpEmailTransport).
        services.AddSingleton<IEmailTransport, SmtpEmailTransport>();

        // Scoped, because both write through the request's own AppDbContext: that is what makes the
        // outbox row commit in the same transaction as the order it is about (doc 08 §3.8).
        services.AddScoped<IEmailOutboxWriter, EmailOutboxWriter>();
        services.AddScoped<IOrderEmailOutbox, OrderEmailOutbox>();
        services.AddScoped<IAccountEmailOutbox, AccountEmailOutbox>();
        services.AddScoped<IEmailOutboxStore, EmailOutboxStore>();

        // ops.AdminAlerts (doc 08 §3.8 N2, W4-02a). Scoped for the same reason: the acknowledgement writes
        // its audit row through the request's own AppDbContext so the two commit together, and the
        // dead-email raiser runs inside the sweep's own scope.
        services.AddScoped<IAdminAlertWriter, AdminAlertWriter>();
        services.AddScoped<IAdminAlertReader, AdminAlertReader>();
        services.AddScoped<IAdminAlertAcknowledger, AdminAlertAcknowledger>();
        services.AddScoped<IDeadEmailAlerts, DeadEmailAlerts>();

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

        // The admin promo-code screen (ADM-13, W3-05). The reader converts the two UTC columns to Colombo
        // wall-clock at this edge; the store keeps one unit of work so a change and its audit row commit
        // together.
        services.AddScoped<IPromoCodeAdminReader, PromoCodeAdminReader>();
        services.AddScoped<IPromoCodeAdminStore, PromoCodeAdminStore>();
        services.AddScoped<INewsletterSubscriptions, NewsletterSubscriptions>();

        // Sale campaigns (ADM-14, W3-05b). `ICampaignOfferReader` is the **live** side of doc 08 §6
        // step 3, which the bag, the checkout quote and the placement all read — doc 08 §7.4's "checkout
        // always prices live", as against the stored summary on ProductStats that listings read and that
        // may lag five minutes at a boundary.
        services.AddScoped<ICampaignOfferReader, CampaignOfferReader>();

        // The lagging side of the same decision: doc 08 §7.4's batched recompute of the stored price
        // summary, which the admin save path and the RefreshCampaignPricing job both drive.
        services.AddScoped<ICampaignPricingRefresher, CampaignPricingRefresher>();

        // The admin screen (ADM-14). The reader converts the two UTC columns to Colombo wall-clock at
        // this edge and resolves each target to the name the list prints; the store keeps one unit of
        // work so a change and its audit row commit together.
        services.AddScoped<ISaleCampaignAdminReader, SaleCampaignAdminReader>();
        services.AddScoped<ISaleCampaignAdminStore, SaleCampaignAdminStore>();

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

        // The groundwork a provider that is confirmed by the provider itself (PayPal, W4-01; a second
        // gateway, W4-01b) is built on, so that adding one stays "a class and a registration line" with no
        // change to checkout (PAY-03):
        //  - OrderSettlementEffects: the Paid effects, written once, called by the bank review above and by
        //    the settlement below. A concrete class and not a port: it must run inside its caller's
        //    transaction, and a port would invite a transaction of its own.
        //  - IOrderSettlementStore: "this payment is complete" -> pay the order, or put the money on record
        //    as a payment exception (doc 08 §4.2 step 3).
        //  - IPaymentEventStore: the provider event log, once per idempotency key (doc 08 DB13).
        //  - IPaymentAttemptReader: which payment the order page is describing (PaymentInstructionsRequest).
        services.AddScoped<OrderSettlementEffects>();
        services.AddScoped<IOrderSettlementStore, OrderSettlementStore>();
        services.AddScoped<IPaymentEventStore, PaymentEventStore>();
        services.AddScoped<IPaymentAttemptReader, PaymentAttemptReader>();

        // The admin orders screen (W3-04, ADM-09, doc 11 P9): the list and the detail, the four fulfilment
        // transitions, the couriers the client fills in, and the two settings groups an invoice and the
        // auto-deliver job read.
        services.AddScoped<IAdminOrderReader, AdminOrderReader>();
        services.AddScoped<IOrderFulfilmentStore, OrderFulfilmentStore>();

        // W3-07: the admin customers screen (ADM-15) and the newsletter subscriber list (ADM-21). Reads,
        // plus the export's own audit row, which goes through IAuditLogWriter like every other.
        services.AddScoped<IAdminCustomerReader, AdminCustomerReader>();
        services.AddScoped<INewsletterSubscriberReader, NewsletterSubscriberReader>();

        // W3-04b / doc 11 P10 flow A: cancelling an unpaid order, for staff and for the shopper's own
        // order link. Its own store rather than a member of IOrderFulfilmentStore, which touches no stock.
        services.AddScoped<IOrderCancellationStore, OrderCancellationStore>();
        services.AddScoped<ICourierAdminStore, CourierAdminStore>();
        services.AddScoped<IOrderSettingsReader, OrderSettingsReader>();
        services.AddScoped<IAutoDeliverStore, AutoDeliverStore>();

        services.AddMediaLibrary();

        services.AddOptions<SeedOptions>().BindConfiguration(SeedOptions.SectionName);

        // Written by the seeder, read by the readiness check. A singleton because it is this host's
        // answer to "did I come up able to take an order", which outlives any request scope.
        services.AddSingleton<ReferenceDataState>();

        services.AddScoped<CatalogReferenceDataSeeder>();
        services.AddScoped<SalesReferenceDataSeeder>();
        services.AddScoped<HomeContentSeeder>();
        services.AddScoped<SettingsSeeder>();

        // Doc 08 §9's versioned default-role permission step (issue #122). A step of the reference-data
        // phase rather than a hosted service of its own, so it runs under that phase's applock and retry
        // rather than growing a second copy of them — and so it is not behind `Seed:Admin:Enabled`, which
        // is turned off after go-live.
        services.AddScoped<Identity.Seeding.DefaultRoleSeedUpgrader>();
        services.AddScoped<DemoCatalogueSeeder>();
        services.AddScoped<CatalogSeedRunner>();
        services.AddHostedService<CatalogSeedHostedService>();

        // Doc 08 §4.1/§5.6 and doc 11 P10 flow B: every five minutes, release the stock and the promo use
        // of orders nobody paid for. Issue #107's real mitigation.
        services.AddHostedService<ExpireUnpaidOrdersService>();

        // Doc 11 P9 step 8 and doc 08 §4.1 D4: once a day, close orders that shipped more than
        // Orders.AutoDeliverAfterDays ago. **Never a store pickup order** (G-18).
        services.AddHostedService<AutoDeliverShippedService>();

        // Doc 08 §7.4 and doc 11 P3 step 3 (ADM-14): every five minutes, bring the stored sale price of
        // the products of campaigns that have just started or ended up to date, so listings and the Sale
        // page catch up with a boundary. Never on the money path — the bag and checkout price live.
        services.AddHostedService<RefreshCampaignPricingService>();

        // Doc 11 §1.4's `SendEmails` and doc 08 §3.8: hand the queued messages to the mail server with
        // the 1/5/15/60/240-minute ladder. With no SMTP configured it claims nothing and says so with a
        // count — see SendEmailsService, which is the behaviour that matters while the client's mail
        // credentials are outstanding.
        services.AddHostedService<SendEmailsService>();

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

        // ACC-01 / ACC-02 (W3-01): the second cookie scheme doc 08 §3.1 DB10 asks for, and the customer
        // account use cases behind it. **After** AddAdminIdentity, because that call establishes the
        // Identity core services both share (one store, doc 08 D7) and names the default scheme.
        services.AddCustomerIdentity();

        return services;
    }

    /// <summary>
    /// Whether a configured base URL is absent or is an absolute http/https origin.
    /// </summary>
    /// <remarks>
    /// <c>Uri.TryCreate</c> with <see cref="UriKind.Absolute"/> accepts <c>mailto:</c> and
    /// <c>file:</c> too, so the scheme is checked as well — a <c>file:</c> origin in an email would be a
    /// link every recipient's client silently refused.
    /// </remarks>
    private static bool IsAbsoluteHttpUrl(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
}
