using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Admin.Account;
using SpencerDenim.Application.Admin.Authentication;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Catalog.Departments;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Catalog.Search;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Announcements;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Application.Content.Home;
using SpencerDenim.Application.Diagnostics.Ping;
using SpencerDenim.Application.Marketing.Newsletter;

namespace SpencerDenim.Application;

public static class DependencyInjection
{
    /// <summary>
    /// The key the uncached homepage handler is registered under, so the cache can decorate it
    /// without the two registrations resolving each other in a circle.
    /// </summary>
    private const string UncachedHomePage = "home-page:uncached";

    /// <summary>Registers application use-case handlers.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IQueryHandler<PingQuery, PingResponse>, PingQueryHandler>();
        services.AddScoped<ICommandHandler<AdminLoginCommand, AdminLoginResult>, AdminLoginCommandHandler>();
        services.AddScoped<IQueryHandler<WhoAmIQuery, WhoAmIResponse?>, WhoAmIQueryHandler>();
        services.AddScoped<
            ICommandHandler<ChangeAdminPasswordCommand, ChangeAdminPasswordResult>,
            ChangeAdminPasswordCommandHandler>();

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

        return services;
    }
}
