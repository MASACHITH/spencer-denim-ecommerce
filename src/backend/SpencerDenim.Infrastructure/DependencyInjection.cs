using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Infrastructure.Health;
using SpencerDenim.Infrastructure.Persistence;
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

            // Scaffold only: there are no entity configurations yet, so ApplyConfigurationsFromAssembly warns on
            // every model build. Logged at Debug instead of Warning. REMOVE this line with the first entity
            // configuration (W1 catalogue work), so a genuinely empty scan is noticed. No other warning is touched.
            builder.ConfigureWarnings(warnings => warnings.Log((CoreEventId.NoEntityTypeConfigurationsWarning, LogLevel.Debug)));
        });

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(
                name: "sqlserver",
                failureStatus: null,
                tags: [HealthCheckTags.Ready],
                timeout: DatabaseHealthCheckTimeout);

        return services;
    }
}
