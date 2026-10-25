using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.SqlServer.Infrastructure.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Infrastructure;
using SpencerDenim.Infrastructure.Identity;
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
    public void A_mistyped_protected_staff_address_stops_the_server_because_it_would_leave_the_account_unprotected()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
            ["AdminSecurity:ProtectedStaffEmails"] = "support@example.com, typo-without-an-at-sign",
        });

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AdminSecurityOptions>>().Value);

        Assert.Contains("ProtectedStaffEmails", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_protected_staff_list_is_read_from_the_AdminSecurity_setting_the_server_environment_file_fills()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
            ["AdminSecurity:ProtectedStaffEmails"] = "Support@Example.com",
        });

        var accounts = provider.GetRequiredService<IProtectedStaffAccounts>();

        Assert.True(accounts.IsProtected("support@example.com"));
        Assert.False(accounts.IsProtected("someone-else@example.com"));
    }

    [Fact]
    public void A_mistyped_guarded_staff_address_stops_the_server_because_it_would_leave_the_account_unguarded()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
            ["AdminSecurity:ProtectedStaffEmails"] = "owner@example.com",
            ["AdminSecurity:GuardedStaffEmails"] = "client-without-an-at-sign",
        });

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AdminSecurityOptions>>().Value);

        Assert.Contains("GuardedStaffEmails", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_guarded_account_with_no_protected_account_stops_the_server_because_nobody_could_ever_change_it()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
            ["AdminSecurity:GuardedStaffEmails"] = "client@example.com",
        });

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AdminSecurityOptions>>().Value);

        Assert.Contains("ProtectedStaffEmails", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The check reads the addresses, not the raw text: separators alone name nobody (tester, #255).</summary>
    [Theory]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData(" , ; ")]
    public void A_protected_list_of_only_separators_names_nobody_so_a_guarded_account_still_stops_the_server(string separatorsOnly)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
            ["AdminSecurity:ProtectedStaffEmails"] = separatorsOnly,
            ["AdminSecurity:GuardedStaffEmails"] = "client@example.com",
        });

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AdminSecurityOptions>>().Value);

        Assert.Contains("ProtectedStaffEmails", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_guarded_staff_list_is_read_from_the_AdminSecurity_setting_the_server_environment_file_fills()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
            ["AdminSecurity:ProtectedStaffEmails"] = "owner@example.com",
            ["AdminSecurity:GuardedStaffEmails"] = "Client@Example.com",
        });

        var accounts = provider.GetRequiredService<IProtectedStaffAccounts>();

        Assert.True(accounts.IsGuarded("client@example.com"));
        Assert.False(accounts.IsGuarded("owner@example.com"));
    }

    [Fact]
    public void Nobody_is_protected_when_the_server_names_no_account()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
        });

        Assert.False(provider.GetRequiredService<IProtectedStaffAccounts>().IsProtected("support@example.com"));
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
