using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Admin.Account;
using SpencerDenim.Application.Admin.Authentication;
using SpencerDenim.Application.Admin.Auditing;
using SpencerDenim.Application.Admin.Auditing.Contracts;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Catalog.Admin;
using SpencerDenim.Application.Catalog.Admin.Contracts;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Catalog.Departments;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Catalog.Search;
using SpencerDenim.Application.Catalog.Sitemap;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Announcements;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Application.Content.Home;
using SpencerDenim.Application.Diagnostics.Ping;
using SpencerDenim.Application.Marketing.Newsletter;
using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Application.Sales.Wishlist;
using SpencerDenim.Application.Sales.Wishlist.Contracts;
using SpencerDenim.Application.Sales.Checkout;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Application.Settings.Site;

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

        // Storefront content (W2-01). The homepage handler also depends on the catalogue read port,
        // because a product row is the head of the listing it links to, and it is wrapped in doc 08
        // §3.7's five-minute cache — registered by hand rather than with a decoration helper,
        // because one decorator does not justify another dependency.
        services.AddKeyedScoped<IQueryHandler<GetHomePageQuery, HomePageDto>, GetHomePageQueryHandler>(
            UncachedHomePage);
        services.AddScoped<IQueryHandler<GetHomePageQuery, HomePageDto>>(provider =>
            new CachedHomePageQueryHandler(
                provider.GetRequiredKeyedService<IQueryHandler<GetHomePageQuery, HomePageDto>>(UncachedHomePage),
                provider.GetRequiredService<IHomePageCache>()));
        services.AddScoped<
            IQueryHandler<GetAnnouncementsQuery, IReadOnlyList<AnnouncementDto>>,
            GetAnnouncementsQueryHandler>();
        services.AddScoped<
            ICommandHandler<SubscribeToNewsletterCommand, NewsletterSubscriptionOutcome>,
            SubscribeToNewsletterCommandHandler>();

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
        services.AddKeyedScoped<IQueryHandler<GetSiteSettingsQuery, SiteSettingsDto>, GetSiteSettingsQueryHandler>(
            UncachedSiteSettings);
        services.AddScoped<IQueryHandler<GetSiteSettingsQuery, SiteSettingsDto>>(provider =>
            new CachedSiteSettingsQueryHandler(
                provider.GetRequiredKeyedService<IQueryHandler<GetSiteSettingsQuery, SiteSettingsDto>>(
                    UncachedSiteSettings),
                provider.GetRequiredService<ISiteSettingsCache>()));

        // Checkout (W3-02, CHK-01..04). The reads, the number series and the one placement transaction are
        // ports Infrastructure registers; the arithmetic is the Domain's `OrderPricing`.
        services.AddScoped<IQueryHandler<GetCheckoutQuery, CheckoutView>, GetCheckoutQueryHandler>();
        services.AddScoped<IQueryHandler<GetOrderQuery, OrderConfirmationDto?>, GetOrderQueryHandler>();
        services.AddScoped<ICommandHandler<PlaceOrderCommand, PlaceOrderResult>, PlaceOrderCommandHandler>();

        return services;
    }
}
