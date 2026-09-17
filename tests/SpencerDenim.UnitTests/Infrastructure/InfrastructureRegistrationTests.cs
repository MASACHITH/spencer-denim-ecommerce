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
        services.AddInfrastructure();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
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
        Assert.Empty(dbContext.Model.GetEntityTypes());
        Assert.Equal("ops", AppDbContext.MigrationsHistorySchema);
    }

    [Fact]
    public void Clock_is_registered_and_returns_utc()
    {
        using var provider = BuildProvider([]);

        var clock = provider.GetRequiredService<IClock>();

        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
    }
}
