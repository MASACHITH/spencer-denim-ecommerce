using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Infrastructure.Catalog;
using SpencerDenim.Infrastructure.Health;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Interceptors;
using SpencerDenim.Infrastructure.Persistence.Seeding;
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
            .ValidateOnStart();

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
        services.AddScoped<IProductCatalogReader, ProductCatalogReader>();

        services.AddOptions<SeedOptions>().BindConfiguration(SeedOptions.SectionName);
        services.AddScoped<CatalogReferenceDataSeeder>();
        services.AddScoped<DemoCatalogueSeeder>();
        services.AddScoped<CatalogSeedRunner>();
        services.AddHostedService<CatalogSeedHostedService>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(
                name: "sqlserver",
                failureStatus: null,
                tags: [HealthCheckTags.Ready],
                timeout: DatabaseHealthCheckTimeout);

        services.AddAdminIdentity();

        return services;
    }
}
