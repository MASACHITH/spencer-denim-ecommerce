using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Accounts;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Application.Admin.Account;
using SpencerDenim.Application.Admin.Authentication;
using SpencerDenim.Application.Admin.Auditing;
using SpencerDenim.Application.Admin.Auditing.Contracts;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Customers;
using SpencerDenim.Application.Admin.Customers.Contracts;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Application.Admin.Reporting.Contracts;
using SpencerDenim.Application.Analytics.Reporting;
using SpencerDenim.Application.Catalog.Admin;
using SpencerDenim.Application.Catalog.Admin.Contracts;
using SpencerDenim.Application.Catalog.Admin.Products;
using SpencerDenim.Application.Catalog.Admin.Products.Contracts;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Catalog.Departments;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Catalog.Reviews;
using SpencerDenim.Application.Catalog.Search;
using SpencerDenim.Application.Catalog.Sitemap;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Announcements;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Content.Admin.Announcements;
using SpencerDenim.Application.Content.Admin.Home;
using SpencerDenim.Application.Content.Admin.MegaMenu;
using SpencerDenim.Application.Content.Admin.SizeGuides;
using SpencerDenim.Application.Marketing.Promotions;
using SpencerDenim.Application.Content.Admin.Contracts;
using SpencerDenim.Application.Content.Contact;
using SpencerDenim.Application.Content.DeliveryReturns;
using SpencerDenim.Application.Content.Home;
using SpencerDenim.Application.Content.Pages;
using SpencerDenim.Application.Diagnostics.Ping;
using SpencerDenim.Application.Inventory;
using SpencerDenim.Application.Inventory.Contracts;
using SpencerDenim.Application.Marketing.Newsletter;
using SpencerDenim.Application.Marketing.SaleBanner;
using SpencerDenim.Application.Marketing.Newsletter.Admin;
using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Application.Sales.Orders.Admin;
using SpencerDenim.Application.Sales.Orders.Contracts;
using SpencerDenim.Application.Sales.Orders.Customer;
using SpencerDenim.Application.Sales.Payments.Admin;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Application.Sales.Payments.Customer;
using SpencerDenim.Application.Sales.Payments.PayPal.Admin;
using SpencerDenim.Application.Sales.Wishlist;
using SpencerDenim.Application.Sales.Wishlist.Contracts;
using SpencerDenim.Application.Sales.Checkout;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Delivery;
using SpencerDenim.Application.Sales.Delivery.Admin;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Application.Settings.Site;
using SpencerDenim.Application.Ops.Alerts;
using SpencerDenim.Application.Ops.Alerts.Contracts;

namespace SpencerDenim.Application;

public static class DependencyInjection
{
    /// <summary>
    /// The key the uncached homepage handler is registered under, so the cache can decorate it
    /// without the two registrations resolving each other in a circle.
    /// </summary>
    private const string UncachedHomePage = "home-page:uncached";

    /// <inheritdoc cref="UncachedHomePage"/>
    private const string UncachedSiteSettings = "site-settings:uncached";

    /// <inheritdoc cref="UncachedHomePage"/>
    private const string UncachedContentPageLinks = "content-page-links:uncached";

    /// <summary>Registers application use-case handlers.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IQueryHandler<PingQuery, PingResponse>, PingQueryHandler>();
        services.AddScoped<ICommandHandler<AdminLoginCommand, AdminLoginResult>, AdminLoginCommandHandler>();
        services.AddScoped<IQueryHandler<WhoAmIQuery, WhoAmIResponse?>, WhoAmIQueryHandler>();
        services.AddScoped<
            IQueryHandler<GetAuditLogsQuery, PagedResult<AuditLogEntryDto>>,
            GetAuditLogsQueryHandler>();
        services.AddScoped<
            ICommandHandler<ChangeAdminPasswordCommand, ChangeAdminPasswordResult>,
            ChangeAdminPasswordCommandHandler>();

        // Admin alerts (doc 08 §3.8 N2, doc 11 §7 G-37, W4-02a): the admin shell's banner and its
        // acknowledge. Both compute the caller's R10 visibility from their effective permissions, so both
        // depend on IEffectivePermissionsProvider, which Infrastructure registers.
        services.AddScoped<
            IQueryHandler<GetOpenAdminAlertsQuery, AdminAlertBannerDto>,
            GetOpenAdminAlertsQueryHandler>();
        services.AddScoped<
            ICommandHandler<AcknowledgeAdminAlertCommand, AcknowledgeAdminAlertResult>,
            AcknowledgeAdminAlertCommandHandler>();

        // Staff two-factor authentication (W1-10): the second sign-in step and the user's own enrolment.
        services.AddScoped<
            ICommandHandler<CompleteAdminTwoFactorSignInCommand, AdminLoginResult>,
            CompleteAdminTwoFactorSignInCommandHandler>();
        services.AddScoped<
            IQueryHandler<GetAdminTwoFactorStatusQuery, AdminTwoFactorStatusResponse?>,
            GetAdminTwoFactorStatusQueryHandler>();
        services.AddScoped<ICommandHandler<BeginAdminTwoFactorSetupCommand, AdminTwoFactorResult>, BeginAdminTwoFactorSetupCommandHandler>();
        services.AddScoped<ICommandHandler<EnableAdminTwoFactorCommand, AdminTwoFactorResult>, EnableAdminTwoFactorCommandHandler>();
        services.AddScoped<
            ICommandHandler<RegenerateAdminRecoveryCodesCommand, AdminTwoFactorResult>,
            RegenerateAdminRecoveryCodesCommandHandler>();
        services.AddScoped<ICommandHandler<DisableAdminTwoFactorCommand, AdminTwoFactorResult>, DisableAdminTwoFactorCommandHandler>();
        // Users & Roles management (W1-08, issue #8). The ports these depend on are registered by
        // Infrastructure, exactly like the catalogue read side below.
        services.AddScoped<
            IQueryHandler<GetStaffUsersQuery, PagedResult<StaffUserSummaryDto>>,
            GetStaffUsersQueryHandler>();
        services.AddScoped<IQueryHandler<GetStaffUserByIdQuery, StaffUserDetailDto?>, GetStaffUserByIdQueryHandler>();
        services.AddScoped<IQueryHandler<GetRolesQuery, IReadOnlyList<RoleSummaryDto>>, GetRolesQueryHandler>();
        services.AddScoped<IQueryHandler<GetRoleByIdQuery, RoleDetailDto?>, GetRoleByIdQueryHandler>();
        services.AddScoped<ICommandHandler<CreateStaffUserCommand, CreateStaffUserResult>, CreateStaffUserCommandHandler>();
        services.AddScoped<ICommandHandler<AssignUserRolesCommand, AssignUserRolesResult>, AssignUserRolesCommandHandler>();
        services.AddScoped<ICommandHandler<SetStaffUserActiveCommand, SetStaffUserActiveResult>, SetStaffUserActiveCommandHandler>();
        services.AddScoped<
            ICommandHandler<UpdateStaffUserProfileCommand, UpdateStaffUserProfileResult>,
            UpdateStaffUserProfileCommandHandler>();

        // Catalogue read side (W1-02). The ports they depend on are registered by Infrastructure.
        services.AddScoped<
            IQueryHandler<GetDepartmentsQuery, IReadOnlyList<DepartmentDto>>,
            GetDepartmentsQueryHandler>();
        services.AddScoped<
            IQueryHandler<ProductListQuery, Result<PagedResult<ProductCardDto>>>,
            GetProductsQueryHandler>();
        services.AddScoped<
            IQueryHandler<GetProductBySlugQuery, ProductDetailDto?>,
            GetProductBySlugQueryHandler>();
        services.AddScoped<
            IQueryHandler<ProductSearchQuery, Result<PagedResult<ProductCardDto>>>,
            SearchProductsQueryHandler>();
        services.AddScoped<
            IQueryHandler<GetCatalogSitemapQuery, CatalogSitemapDto>,
            GetCatalogSitemapQueryHandler>();

        // Admin catalogue structure (ADM-11, W1-12): departments and the category tree.
        // The read side is its own port because the storefront's drops every deactivated row, which is
        // exactly what these screens exist to show and switch back on (see IAdminCatalogReader).
        services.AddScoped<
            IQueryHandler<GetAdminDepartmentsQuery, IReadOnlyList<AdminDepartmentDto>>,
            GetAdminDepartmentsQueryHandler>();
        services.AddScoped<
            IQueryHandler<GetAdminCategoryTreeQuery, AdminCategoryTreeDto?>,
            GetAdminCategoryTreeQueryHandler>();
        services.AddScoped<
            IQueryHandler<GetCategoryDeactivationImpactQuery, CategoryDeactivationImpactDto?>,
            GetCategoryDeactivationImpactQueryHandler>();
        services.AddScoped<
            IQueryHandler<GetCategoryFormOptionsQuery, CategoryFormOptionsDto>,
            GetCategoryFormOptionsQueryHandler>();
        services.AddScoped<
            ICommandHandler<UpdateDepartmentCommand, CatalogWriteResult>,
            UpdateDepartmentCommandHandler>();
        services.AddScoped<
            ICommandHandler<SetDepartmentActiveCommand, CatalogWriteResult>,
            SetDepartmentActiveCommandHandler>();
        services.AddScoped<
            ICommandHandler<ReorderDepartmentsCommand, CatalogWriteResult>,
            ReorderDepartmentsCommandHandler>();
        services.AddScoped<
            ICommandHandler<CreateCategoryCommand, CatalogWriteResult>,
            CreateCategoryCommandHandler>();
        services.AddScoped<
            ICommandHandler<UpdateCategoryCommand, CatalogWriteResult>,
            UpdateCategoryCommandHandler>();
        services.AddScoped<
            ICommandHandler<ReorderCategoriesCommand, CatalogWriteResult>,
            ReorderCategoriesCommandHandler>();
        services.AddScoped<
            ICommandHandler<SetCategoryActiveCommand, CatalogWriteResult>,
            SetCategoryActiveCommandHandler>();
        services.AddScoped<
            ICommandHandler<RemoveCategoryCommand, CatalogWriteResult>,
            RemoveCategoryCommandHandler>();

        // Admin products (ADM-10, W1-03). The read side is again its own port, because the storefront's
        // reader drops every unpublished row and those are exactly the products this screen exists to
        // finish and publish (see IAdminProductReader).
        services.AddScoped<
            IQueryHandler<GetAdminProductsQuery, PagedResult<AdminProductListItemDto>>,
            GetAdminProductsQueryHandler>();
        services.AddScoped<
            IQueryHandler<GetAdminProductQuery, AdminProductDetailDto?>,
            GetAdminProductQueryHandler>();
        services.AddScoped<
            IQueryHandler<GetProductFormOptionsQuery, ProductFormOptionsDto>,
            GetProductFormOptionsQueryHandler>();
        services.AddScoped<
            IQueryHandler<SearchProductsForRelatedQuery, IReadOnlyList<AdminRelatedProductDto>>,
            SearchProductsForRelatedQueryHandler>();
        services.AddScoped<
            ICommandHandler<CreateProductCommand, CatalogWriteResult>,
            CreateProductCommandHandler>();
        services.AddScoped<
            ICommandHandler<UpdateProductCommand, CatalogWriteResult>,
            UpdateProductCommandHandler>();
        services.AddScoped<
            ICommandHandler<SetProductPublishedCommand, CatalogWriteResult>,
            SetProductPublishedCommandHandler>();
        services.AddScoped<
            ICommandHandler<RemoveProductCommand, CatalogWriteResult>,
            RemoveProductCommandHandler>();

        // Storefront content (W2-01). The homepage handler also depends on the catalogue read port,
        // because a product row is the head of the listing it links to, and it is wrapped in doc 08
        // §3.7's five-minute cache — registered by hand rather than with a decoration helper,
        // because one decorator does not justify another dependency.
        services.AddKeyedScoped<IQueryHandler<GetHomePageQuery, HomePageDto>, GetHomePageQueryHandler>(
            UncachedHomePage);
        services.AddScoped<IQueryHandler<GetHomePageQuery, HomePageDto>>(provider =>
            new CachedHomePageQueryHandler(
                provider.GetRequiredKeyedService<IQueryHandler<GetHomePageQuery, HomePageDto>>(UncachedHomePage),
                provider.GetRequiredService<IHomePageCache>(),
                provider.GetRequiredService<IHomeContentReader>(),
                provider.GetRequiredService<IClock>()));
        services.AddScoped<
            IQueryHandler<GetAnnouncementsQuery, IReadOnlyList<AnnouncementDto>>,
            GetAnnouncementsQueryHandler>();

        // The Sale page's campaign banner (ADM-14, #191). Read live, not cached: see the handler.
        services.AddScoped<IQueryHandler<GetSaleBannerQuery, SaleBannerDto>, GetSaleBannerQueryHandler>();
        services.AddScoped<
            ICommandHandler<SubscribeToNewsletterCommand, NewsletterSubscriptionOutcome>,
            SubscribeToNewsletterCommandHandler>();

        // Content pages and the contact enquiry (W2-11, GLB-10).
        //
        // The page *body* read is not cached: one page's body is read only when somebody opens that page,
        // and a client who has just corrected a typo in their returns policy should see it corrected. The
        // published-page *list* is, because it is read during the server render of every page on the site
        // — same decoration-by-hand as the homepage and the site settings above.
        services.AddScoped<IQueryHandler<GetContentPageQuery, ContentPageDto?>, GetContentPageQueryHandler>();
        services.AddKeyedScoped<
            IQueryHandler<GetContentPageLinksQuery, ContentPageLinksDto>,
            GetContentPageLinksQueryHandler>(UncachedContentPageLinks);
        services.AddScoped<IQueryHandler<GetContentPageLinksQuery, ContentPageLinksDto>>(provider =>
            new CachedContentPageLinksQueryHandler(
                provider.GetRequiredKeyedService<IQueryHandler<GetContentPageLinksQuery, ContentPageLinksDto>>(
                    UncachedContentPageLinks),
                provider.GetRequiredService<IContentPageLinksCache>()));
        services.AddScoped<
            ICommandHandler<SubmitContactEnquiryCommand, ContactEnquiryResult>,
            SubmitContactEnquiryCommandHandler>();

        // The admin content screens (ADM-17, GLB-10). The four page writes are one class, because they
        // share the validation, the sanitising and the audit shape; it is registered once per interface so
        // each endpoint still asks for the one command it sends.
        services.AddScoped<ContentPageWriteHandlers>();
        services.AddScoped<ICommandHandler<CreateContentPageCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<ContentPageWriteHandlers>());
        services.AddScoped<ICommandHandler<UpdateContentPageCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<ContentPageWriteHandlers>());
        services.AddScoped<ICommandHandler<SetContentPagePublishedCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<ContentPageWriteHandlers>());
        services.AddScoped<ICommandHandler<DeleteContentPageCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<ContentPageWriteHandlers>());

        // The rest of the ADM-17 content screens (#192): the homepage blocks, the announcement strip and the
        // size guides. Each group is one class registered once per interface, like the page writes above.
        services.AddScoped<HomeContentWriteHandlers>();
        services.AddScoped<ICommandHandler<UpdateHomeSectionCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<HomeContentWriteHandlers>());
        services.AddScoped<ICommandHandler<CreateHomeSectionItemCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<HomeContentWriteHandlers>());
        services.AddScoped<ICommandHandler<UpdateHomeSectionItemCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<HomeContentWriteHandlers>());
        services.AddScoped<ICommandHandler<DeleteHomeSectionItemCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<HomeContentWriteHandlers>());

        services.AddScoped<AnnouncementWriteHandlers>();
        services.AddScoped<ICommandHandler<CreateAnnouncementCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<AnnouncementWriteHandlers>());
        services.AddScoped<ICommandHandler<UpdateAnnouncementCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<AnnouncementWriteHandlers>());
        services.AddScoped<ICommandHandler<DeleteAnnouncementCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<AnnouncementWriteHandlers>());

        services.AddScoped<SizeGuideWriteHandlers>();
        services.AddScoped<ICommandHandler<CreateSizeGuideCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<SizeGuideWriteHandlers>());
        services.AddScoped<ICommandHandler<UpdateSizeGuideCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<SizeGuideWriteHandlers>());
        services.AddScoped<ICommandHandler<DeleteSizeGuideCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<SizeGuideWriteHandlers>());

        // Mega-menu cards (ADM-17, GLB-05; #215).
        services.AddScoped<MegaMenuCardWriteHandlers>();
        services.AddScoped<ICommandHandler<CreateMegaMenuCardCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<MegaMenuCardWriteHandlers>());
        services.AddScoped<ICommandHandler<UpdateMegaMenuCardCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<MegaMenuCardWriteHandlers>());
        services.AddScoped<ICommandHandler<DeleteMegaMenuCardCommand, ContentWriteResult>>(
            provider => provider.GetRequiredService<MegaMenuCardWriteHandlers>());

        // The admin promo-code screen (ADM-13, W3-05). Three writes in one class because they share every
        // rule; registered once per interface so each endpoint asks for the one command it sends.
        services.AddScoped<PromoCodeWriteHandlers>();
        services.AddScoped<ICommandHandler<CreatePromoCodeCommand, PromoWriteResult>>(
            provider => provider.GetRequiredService<PromoCodeWriteHandlers>());
        services.AddScoped<ICommandHandler<UpdatePromoCodeCommand, PromoWriteResult>>(
            provider => provider.GetRequiredService<PromoCodeWriteHandlers>());
        services.AddScoped<ICommandHandler<DeletePromoCodeCommand, PromoWriteResult>>(
            provider => provider.GetRequiredService<PromoCodeWriteHandlers>());

        // ADM-14 (W3-05b). One class per interface, so each endpoint asks for the one command it sends;
        // the three writes share every rule, including doc 11 P3 step 3's recompute of the affected
        // products' stored price summary.
        services.AddScoped<SaleCampaignWriteHandlers>();
        services.AddScoped<ICommandHandler<CreateSaleCampaignCommand, SaleCampaignWriteResult>>(
            provider => provider.GetRequiredService<SaleCampaignWriteHandlers>());
        services.AddScoped<ICommandHandler<UpdateSaleCampaignCommand, SaleCampaignWriteResult>>(
            provider => provider.GetRequiredService<SaleCampaignWriteHandlers>());
        services.AddScoped<ICommandHandler<DeleteSaleCampaignCommand, SaleCampaignWriteResult>>(
            provider => provider.GetRequiredService<SaleCampaignWriteHandlers>());

        services.AddScoped<ContactEnquiryWriteHandlers>();
        services.AddScoped<ICommandHandler<OpenContactSubmissionCommand, ContactSubmissionWriteOutcome>>(
            provider => provider.GetRequiredService<ContactEnquiryWriteHandlers>());
        services.AddScoped<
            ICommandHandler<SetContactSubmissionStatusCommand, ContactSubmissionWriteOutcome>>(
            provider => provider.GetRequiredService<ContactEnquiryWriteHandlers>());
        services.AddScoped<ICommandHandler<DeleteContactSubmissionCommand, ContactSubmissionWriteOutcome>>(
            provider => provider.GetRequiredService<ContactEnquiryWriteHandlers>());
        services.AddScoped<
            IQueryHandler<GetContactSubmissionsQuery, AdminContactSubmissionsDto>,
            GetContactSubmissionsQueryHandler>();

        // The bag and the mini bag (W2-09, CART-01/02). The reads and the writes go through the same two
        // ports, which Infrastructure registers; the merge handler has no caller until ACC-01 gives the
        // API a customer principal (see MergeGuestCartCommandHandler).
        services.AddScoped<IQueryHandler<GetCartQuery, CartView>, GetCartQueryHandler>();
        services.AddScoped<ICommandHandler<AddToCartCommand, CartWriteResult>, AddToCartCommandHandler>();
        services.AddScoped<
            ICommandHandler<SetCartItemQuantityCommand, CartWriteResult>,
            SetCartItemQuantityCommandHandler>();
        services.AddScoped<
            ICommandHandler<RemoveCartItemCommand, CartWriteResult>,
            RemoveCartItemCommandHandler>();
        services.AddScoped<ICommandHandler<MergeGuestCartCommand, CartView>, MergeGuestCartCommandHandler>();

        // The wishlist (W2-08, WISH-01). One read and no writes: the shopper's list is in their browser
        // (doc 02's WISH-01 line, doc 08 §1.7), so this query only turns their slugs into live cards. The
        // merge into an account belongs to W3-01, which is the half of WISH-01 that cannot exist before
        // there is an account to merge into — see `WishlistItem`.
        services.AddScoped<IQueryHandler<GetWishlistQuery, WishlistDto>, GetWishlistQueryHandler>();

        // Settings (ADM-22, GLB-02). The storefront read is wrapped in doc 08 §3.8's sixty-second cache the
        // same way the homepage is, and for a stronger reason: this payload is read during the server render
        // of every page rather than of one.
        services.AddScoped<IQueryHandler<GetAdminSettingsQuery, AdminSettingsDto>, GetAdminSettingsQueryHandler>();
        services.AddScoped<
            ICommandHandler<SetStoreLogoCommand, SettingsWriteResult>,
            SetStoreLogoCommandHandler>();
        services.AddScoped<
            ICommandHandler<UpdateContactAndSocialCommand, SettingsWriteResult>,
            UpdateContactAndSocialCommandHandler>();

        // Issue #193: the Traffic form — how long the nightly purge keeps raw page views (Settings.Edit).
        services.AddScoped<
            ICommandHandler<UpdateTrafficSettingsCommand, SettingsWriteResult>,
            UpdateTrafficSettingsCommandHandler>();

        // Issue #189: the review approval switch (Settings.Edit).
        services.AddScoped<
            ICommandHandler<UpdateReviewSettingsCommand, SettingsWriteResult>,
            UpdateReviewSettingsCommandHandler>();

        // Issue #33: the product page's delivery and returns texts (Settings.Edit), and the panel that shows them
        // beside the delivery rates. The panel is read live, not cached: see the handler.
        services.AddScoped<
            ICommandHandler<UpdateProductPageInfoCommand, SettingsWriteResult>,
            UpdateProductPageInfoCommandHandler>();
        services.AddScoped<IQueryHandler<GetDeliveryReturnsQuery, DeliveryReturnsDto>, GetDeliveryReturnsQueryHandler>();
        services.AddKeyedScoped<IQueryHandler<GetSiteSettingsQuery, SiteSettingsDto>, GetSiteSettingsQueryHandler>(
            UncachedSiteSettings);
        services.AddScoped<IQueryHandler<GetSiteSettingsQuery, SiteSettingsDto>>(provider =>
            new CachedSiteSettingsQueryHandler(
                provider.GetRequiredKeyedService<IQueryHandler<GetSiteSettingsQuery, SiteSettingsDto>>(
                    UncachedSiteSettings),
                provider.GetRequiredService<ISiteSettingsCache>()));

        // Customer accounts (W3-01, ACC-01/02; doc 11 P11). Thin orchestration: validation, the port
        // call, and the one audit row a customer sign-in produces. Everything that touches the identity
        // store is a port Infrastructure registers.
        services.AddScoped<
            ICommandHandler<RegisterCustomerCommand, Result<RegisterCustomerResult>>,
            RegisterCustomerCommandHandler>();

        services.AddScoped<
            ICommandHandler<CustomerSignInCommand, Result<CustomerSignInResult>>,
            CustomerSignInCommandHandler>();

        services.AddScoped<
            ICommandHandler<RequestCustomerPasswordResetCommand, Result<bool>>,
            RequestCustomerPasswordResetCommandHandler>();

        services.AddScoped<
            ICommandHandler<ResetCustomerPasswordCommand, Result<CustomerPasswordResetResult>>,
            ResetCustomerPasswordCommandHandler>();

        services.AddScoped<
            ICommandHandler<ChangeCustomerPasswordCommand, Result<CustomerPasswordChangeResult>>,
            ChangeCustomerPasswordCommandHandler>();

        services.AddScoped<
            ICommandHandler<ConfirmCustomerEmailCommand, CustomerEmailConfirmationResult>,
            ConfirmCustomerEmailCommandHandler>();

        services.AddScoped<
            ICommandHandler<ResendCustomerConfirmationCommand, bool>,
            ResendCustomerConfirmationCommandHandler>();

        services.AddScoped<
            IQueryHandler<GetCustomerProfileQuery, CustomerProfile?>,
            GetCustomerProfileQueryHandler>();

        services.AddScoped<
            ICommandHandler<SaveCustomerProfileCommand, Result<CustomerProfile?>>,
            SaveCustomerProfileCommandHandler>();

        services.AddScoped<
            IQueryHandler<GetCustomerAddressesQuery, IReadOnlyList<CustomerAddressDto>>,
            GetCustomerAddressesQueryHandler>();

        services.AddScoped<
            ICommandHandler<SaveCustomerAddressCommand, Result<CustomerAddressResult>>,
            SaveCustomerAddressCommandHandler>();

        services.AddScoped<
            ICommandHandler<DeleteCustomerAddressCommand, CustomerAddressResult>,
            DeleteCustomerAddressCommandHandler>();

        services.AddScoped<
            ICommandHandler<SetDefaultCustomerAddressCommand, CustomerAddressResult>,
            SetDefaultCustomerAddressCommandHandler>();

        services.AddScoped<
            IQueryHandler<GetCustomerOrdersQuery, CustomerOrderPageDto>,
            GetCustomerOrdersQueryHandler>();

        // Checkout (W3-02, CHK-01..04). The reads, the number series and the one placement transaction are
        // ports Infrastructure registers; the arithmetic is the Domain's `OrderPricing`.
        services.AddScoped<IQueryHandler<GetCheckoutQuery, CheckoutView>, GetCheckoutQueryHandler>();
        services.AddScoped<IQueryHandler<GetOrderQuery, OrderConfirmationDto?>, GetOrderQueryHandler>();
        services.AddScoped<ICommandHandler<PlaceOrderCommand, PlaceOrderResult>, PlaceOrderCommandHandler>();

        // Owner decision 2026-10-21: the country list the storefront's selectors read.
        services.AddScoped<
            IQueryHandler<GetDeliveryCountriesQuery, IReadOnlyList<DeliveryCountryDto>>,
            GetDeliveryCountriesQueryHandler>();

        // Payments (W3-03, PAY-01 / ADM-09). The staff queue of doc 11 P7 step 4 and the one handler
        // behind its three decisions.
        services.AddScoped<
            IQueryHandler<GetBankTransferQueueQuery, BankTransferQueuePage>,
            GetBankTransferQueueQueryHandler>();

        services.AddScoped<
            ICommandHandler<ReviewBankTransferCommand, ReviewBankTransferResult>,
            ReviewBankTransferCommandHandler>();

        services.AddScoped<
            ICommandHandler<SubmitBankTransferReferenceCommand, SubmitBankTransferReferenceResult>,
            SubmitBankTransferReferenceCommandHandler>();

        services.AddScoped<
            ICommandHandler<SubmitBankTransferSlipCommand, SubmitBankTransferSlipResult>,
            SubmitBankTransferSlipCommandHandler>();

        // The admin orders screen (W3-04, ADM-09, doc 11 P9).
        services.AddScoped<
            IQueryHandler<GetAdminOrderListQuery, AdminOrderListPage>,
            GetAdminOrderListQueryHandler>();

        services.AddScoped<
            IQueryHandler<GetAdminOrderQuery, AdminOrderDetailDto?>,
            GetAdminOrderQueryHandler>();

        services.AddScoped<
            IQueryHandler<GetOrderInvoiceQuery, OrderInvoiceDto?>,
            GetOrderInvoiceQueryHandler>();

        services.AddScoped<
            IQueryHandler<ListCouriersQuery, IReadOnlyList<AdminCourierDto>>,
            ListCouriersQueryHandler>();

        // The admin customers screen (W3-07, ADM-15, doc 11 P11 step 16): read only.
        services.AddScoped<
            IQueryHandler<GetAdminCustomersQuery, AdminCustomerListPage>,
            GetAdminCustomersQueryHandler>();

        services.AddScoped<
            IQueryHandler<GetAdminCustomerQuery, AdminCustomerDetailDto?>,
            GetAdminCustomerQueryHandler>();

        services.AddScoped<
            IQueryHandler<GetAdminCustomerOrdersQuery, AdminCustomerOrderPage?>,
            GetAdminCustomerOrdersQueryHandler>();

        // The newsletter subscriber list and its audited export (W3-07, ADM-21, doc 11 P15 step 4).
        services.AddScoped<
            IQueryHandler<GetNewsletterSubscribersQuery, NewsletterSubscriberPage>,
            GetNewsletterSubscribersQueryHandler>();

        services.AddScoped<
            ICommandHandler<ExportNewsletterSubscribersCommand, NewsletterExportFile>,
            ExportNewsletterSubscribersCommandHandler>();

        // The dashboard and the reports (W4-02: ADM-04, ADM-05, ADM-07, ADM-08, ADM-19; doc 11 P16). Read only,
        // bar the three exports, which write an Export audit row each.
        services.AddScoped<IQueryHandler<GetAdminDashboardQuery, AdminDashboardDto>, GetAdminDashboardQueryHandler>();
        services.AddScoped<IQueryHandler<GetSalesTrendQuery, SalesTrendDto>, GetSalesTrendQueryHandler>();
        services.AddScoped<IQueryHandler<GetLowStockQuery, LowStockDto>, GetLowStockQueryHandler>();

        // Traffic (W4-03, ADM-06, ADM-20).
        services.AddScoped<IQueryHandler<GetTrafficReportQuery, TrafficReportDto>, GetTrafficReportQueryHandler>();
        services.AddScoped<IQueryHandler<GetTrafficSourcesQuery, TrafficSourcesDto>, GetTrafficSourcesQueryHandler>();
        services.AddScoped<IQueryHandler<GetSalesReportQuery, SalesReportDto>, GetSalesReportQueryHandler>();
        services.AddScoped<IQueryHandler<GetInventoryReportQuery, InventoryReportPage>, GetInventoryReportQueryHandler>();
        services.AddScoped<IQueryHandler<GetCustomerReportQuery, CustomerReportPage>, GetCustomerReportQueryHandler>();

        // The inventory screen (ADM-12, W1-05, #190): reads on Inventory.View, writes on Inventory.Edit.
        services.AddScoped<InventoryQueryHandlers>();
        services.AddScoped<IQueryHandler<GetInventoryListQuery, InventoryListPage>>(
            provider => provider.GetRequiredService<InventoryQueryHandlers>());
        services.AddScoped<IQueryHandler<GetInventoryFilterOptionsQuery, InventoryFilterOptions>>(
            provider => provider.GetRequiredService<InventoryQueryHandlers>());
        services.AddScoped<IQueryHandler<GetInventoryHistoryQuery, InventoryHistoryPage?>>(
            provider => provider.GetRequiredService<InventoryQueryHandlers>());
        services.AddScoped<InventoryWriteHandlers>();
        services.AddScoped<ICommandHandler<ApplyStockChangesCommand, InventoryWriteResult>>(
            provider => provider.GetRequiredService<InventoryWriteHandlers>());
        services.AddScoped<ICommandHandler<SetLowStockThresholdCommand, InventoryWriteResult>>(
            provider => provider.GetRequiredService<InventoryWriteHandlers>());

        // Reviews and ratings (PDP-08) and their moderation (ADM-16), issue #189.
        services.AddScoped<ReviewQueryHandlers>();
        services.AddScoped<IQueryHandler<GetProductReviewsQuery, ProductReviewPageDto?>>(
            provider => provider.GetRequiredService<ReviewQueryHandlers>());
        services.AddScoped<IQueryHandler<GetMyProductReviewQuery, MyProductReviewDto?>>(
            provider => provider.GetRequiredService<ReviewQueryHandlers>());
        services.AddScoped<IQueryHandler<GetAdminReviewsQuery, AdminReviewListPage>>(
            provider => provider.GetRequiredService<ReviewQueryHandlers>());
        services.AddScoped<ReviewWriteHandlers>();
        services.AddScoped<ICommandHandler<CreateReviewCommand, ReviewCommandResult>>(
            provider => provider.GetRequiredService<ReviewWriteHandlers>());
        services.AddScoped<ICommandHandler<EditReviewCommand, ReviewCommandResult>>(
            provider => provider.GetRequiredService<ReviewWriteHandlers>());
        services.AddScoped<ICommandHandler<ModerateReviewCommand, ModerationResult>, ModerateReviewHandler>();

        services.AddScoped<IQueryHandler<GetCustomerBuyersQuery, CustomerBuyersDto>, GetCustomerBuyersQueryHandler>();
        services.AddScoped<ICommandHandler<ExportSalesReportCommand, ReportExportFile>, ExportSalesReportCommandHandler>();
        services.AddScoped<
            ICommandHandler<ExportInventoryReportCommand, ReportExportFile>,
            ExportInventoryReportCommandHandler>();
        services.AddScoped<
            ICommandHandler<ExportCustomerReportCommand, ReportExportFile>,
            ExportCustomerReportCommandHandler>();

        services.AddScoped<
            ICommandHandler<TransitionOrderCommand, TransitionOrderResult>,
            TransitionOrderCommandHandler>();

        // W3-04b / doc 11 P10 flow A: the shopper's own Cancel order, from the guest order link. Its own
        // handler rather than a kind on the admin command, because it carries no staff actor, no reason
        // and no permission, and answers one opaque refusal.
        services.AddScoped<
            ICommandHandler<CancelOwnOrderCommand, CancelOwnOrderResult>,
            CancelOwnOrderCommandHandler>();

        services.AddScoped<
            ICommandHandler<SaveCourierCommand, SaveCourierResult>,
            SaveCourierCommandHandler>();

        // ADM-09's own settings form: the four Store.* legal keys an invoice prints and the auto-deliver
        // window. It lands with ADM-09 rather than ADM-22 because an invoice that cannot be completed
        // short of SQL is not a deliverable.
        services.AddScoped<
            ICommandHandler<UpdateInvoiceAndFulfilmentSettingsCommand, SettingsWriteResult>,
            UpdateInvoiceAndFulfilmentSettingsCommandHandler>();

        // Settings (ADM-22 + W3-03): the bank details a customer transfers to, under Settings.Payments.
        services.AddScoped<
            ICommandHandler<UpdateBankDetailsCommand, SettingsWriteResult>,
            UpdateBankDetailsCommandHandler>();

        // PayPal's credentials, rate and timings (W4-01, PAY-02), under Settings.Payments.
        services.AddScoped<
            ICommandHandler<UpdatePayPalSettingsCommand, SettingsWriteResult>,
            UpdatePayPalSettingsCommandHandler>();

        // The mail account (doc 08 §3.8 Email.Smtp, Settings.Security), its test send, and doc 08 §3.8 DB2/N2's
        // banner-plus-email for a sensitive change (2026-10-21).
        services.AddScoped<ISensitiveSettingsChangeNotifier, SensitiveSettingsChangeNotifier>();
        services.AddScoped<
            ICommandHandler<UpdateSmtpSettingsCommand, SettingsWriteResult>,
            UpdateSmtpSettingsCommandHandler>();
        services.AddScoped<
            ICommandHandler<SendSmtpTestEmailCommand, SmtpTestResult>,
            SendSmtpTestEmailCommandHandler>();

        // Owner decision 2026-10-21: the Payment methods switch (Settings.Payments) and the Overseas delivery
        // screen (Settings.View / Settings.Edit).
        services.AddScoped<PaymentProviderSwitchHandler>();
        services.AddScoped<IQueryHandler<GetPaymentProvidersAdminQuery, PaymentProvidersAdminDto>>(
            provider => provider.GetRequiredService<PaymentProviderSwitchHandler>());
        services.AddScoped<ICommandHandler<SetPaymentProviderEnabledCommand, PaymentProviderWriteResult>>(
            provider => provider.GetRequiredService<PaymentProviderSwitchHandler>());

        services.AddScoped<
            IQueryHandler<GetOverseasDeliveryAdminQuery, OverseasDeliveryAdminDto>,
            GetOverseasDeliveryAdminQueryHandler>();
        services.AddScoped<OverseasDeliveryCommandHandler>();
        services.AddScoped<ICommandHandler<UpdateDeliveryRegionCommand, OverseasDeliveryWriteResult>>(
            provider => provider.GetRequiredService<OverseasDeliveryCommandHandler>());
        services.AddScoped<ICommandHandler<UpdateCountryDeliveryCommand, OverseasDeliveryWriteResult>>(
            provider => provider.GetRequiredService<OverseasDeliveryCommandHandler>());
        services.AddScoped<ICommandHandler<SetInternationalDeliveryCommand, OverseasDeliveryWriteResult>>(
            provider => provider.GetRequiredService<OverseasDeliveryCommandHandler>());

        return services;
    }
}
