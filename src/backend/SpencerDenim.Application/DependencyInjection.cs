using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Admin.Authentication;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Catalog.Departments;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Catalog.Search;
using SpencerDenim.Application.Content.Announcements;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Application.Content.Home;
using SpencerDenim.Application.Diagnostics.Ping;
using SpencerDenim.Application.Marketing.Newsletter;

namespace SpencerDenim.Application;

public static class DependencyInjection
{
    /// <summary>Registers application use-case handlers.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IQueryHandler<PingQuery, PingResponse>, PingQueryHandler>();
        services.AddScoped<ICommandHandler<AdminLoginCommand, AdminLoginResult>, AdminLoginCommandHandler>();
        services.AddScoped<IQueryHandler<WhoAmIQuery, WhoAmIResponse?>, WhoAmIQueryHandler>();

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
        // because a product row is the head of the listing it links to.
        services.AddScoped<IQueryHandler<GetHomePageQuery, HomePageDto>, GetHomePageQueryHandler>();
        services.AddScoped<
            IQueryHandler<GetAnnouncementsQuery, IReadOnlyList<AnnouncementDto>>,
            GetAnnouncementsQueryHandler>();
        services.AddScoped<
            ICommandHandler<SubscribeToNewsletterCommand, NewsletterSubscriptionOutcome>,
            SubscribeToNewsletterCommandHandler>();

        return services;
    }
}
