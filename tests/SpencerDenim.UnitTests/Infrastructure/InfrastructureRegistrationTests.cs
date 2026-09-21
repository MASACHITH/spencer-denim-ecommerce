using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.SqlServer.Infrastructure.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Infrastructure;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.UnitTests.Infrastructure;

public sealed class InfrastructureRegistrationTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();

        // The real host (Api's Program.cs, via WebApplication.CreateBuilder) always has endpoint routing
        // available; AddInfrastructure's authorization policy cache needs an EndpointDataSource to validate,
        // so this bare ServiceCollection needs the same routing registration to build/validate correctly.
        services.AddRouting();

        services.AddInfrastructure();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>
    /// EF Core only exposes the configured level through its SQL Server options extension (an internal API).
    /// Reading it here is acceptable in a test: a breaking EF change fails this test, not production code.
    /// </summary>
    private static int? ReadConfiguredCompatibilityLevel(AppDbContext dbContext)
    {
#pragma warning disable EF1001 // Internal EF Core API usage.
        return dbContext.GetService<IDbContextOptions>()
            .FindExtension<SqlServerOptionsExtension>()?
            .SqlServerCompatibilityLevel;
#pragma warning restore EF1001
    }

    [Fact]
    public void Missing_connection_string_fails_validation_with_a_clear_message()
    {
        using var provider = BuildProvider([]);

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<DatabaseOptions>>().Value);

        Assert.Contains("ConnectionStrings__Database", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_string_and_database_settings_are_read_from_configuration()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
            ["Database:MaxRetryCount"] = "5",
        });

        var options = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        Assert.Equal("Server=example;Database=SpencerDenim;", options.ConnectionString);
        Assert.Equal(5, options.MaxRetryCount);
        Assert.Equal(30, options.CommandTimeoutSeconds);
    }

    [Fact]
    public void AppDbContext_uses_sql_server_with_retry_on_failure_and_ops_history_schema()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
        });
        using var scope = provider.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", dbContext.Database.ProviderName);
        Assert.True(dbContext.Database.CreateExecutionStrategy().RetriesOnFailure);
        Assert.NotEmpty(dbContext.Model.GetEntityTypes());
        Assert.Equal("ops", AppDbContext.MigrationsHistorySchema);
    }

    [Fact]
    public void Compatibility_level_defaults_to_150_for_localdb_2019()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
        });
        using var scope = provider.CreateScope();

        var options = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(150, options.CompatibilityLevel);
        Assert.Equal(150, ReadConfiguredCompatibilityLevel(dbContext));
    }

    [Fact]
    public void Compatibility_level_is_read_from_configuration_and_applied_to_ef_core()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
            ["Database:CompatibilityLevel"] = "170",
        });
        using var scope = provider.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(170, ReadConfiguredCompatibilityLevel(dbContext));
    }

    [Theory]
    [InlineData("90")]
    [InlineData("180")]
    public void Unsupported_compatibility_level_fails_validation(string level)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
            ["Database:CompatibilityLevel"] = level,
        });

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<DatabaseOptions>>().Value);

        Assert.Contains("Database:CompatibilityLevel", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clock_is_registered_and_returns_utc()
    {
        using var provider = BuildProvider([]);

        var clock = provider.GetRequiredService<IClock>();

        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
    }
}
