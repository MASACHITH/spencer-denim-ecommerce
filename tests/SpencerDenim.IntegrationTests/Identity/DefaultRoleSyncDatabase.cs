using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Identity;

/// <summary>
/// A database of its own for <see cref="DefaultRolePermissionSyncTests"/> (issue #122), migrated and nothing
/// else, with a service provider over it and a drop at the end.
/// <para>
/// <b>Why not the assembly's shared database.</b> These tests have to put the default roles into states the
/// shipped code never produces — the roles as an older build seeded them, a permission a Super Admin removed, a
/// version recorded as 99 — and <c>DefaultRoleSeedingTests</c> asserts the exact permission set of the same
/// three roles in the shared one. A test that restores what it changed is a test that leaks when it fails
/// half-way; a database that is dropped cannot.
/// </para>
/// </summary>
public sealed class DefaultRoleSyncDatabase(SqlServerDatabaseFixture server) : IAsyncLifetime
{
    private string _databaseName = string.Empty;
    private string _serverConnectionString = string.Empty;
    private ServiceProvider? _services;

    /// <summary>Every line logged through this database's service provider.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    public string ConnectionString { get; private set; } = string.Empty;

    public IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("The database is not available; call SkipIfUnavailable() first.");

    public void SkipIfUnavailable() => server.SkipIfUnavailable();

    public async ValueTask InitializeAsync()
    {
        if (!server.IsAvailable)
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;

        _serverConnectionString = new SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        _databaseName = "SpencerDenim_RoleSync_" + Guid.NewGuid().ToString("N");
        ConnectionString = new SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = _databaseName }.ConnectionString;

        // Doc 08 §1.1: the collation is fixed at CREATE DATABASE and the migrations cannot set it. It matters
        // here: role names compare case-insensitively because of it.
        await ExecuteOnServerAsync(
            string.Create(CultureInfo.InvariantCulture, $"CREATE DATABASE [{_databaseName}] COLLATE {SqlServerDatabaseFixture.Collation};"),
            cancellationToken);

        await TestDatabaseMigrator.MigrateAsync(ConnectionString, cancellationToken);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = ConnectionString,
                ["Media:PublicRoot"] = Path.Combine(Path.GetTempPath(), "sd-rolesync-unused", "public"),
                ["Media:PrivateRoot"] = Path.Combine(Path.GetTempPath(), "sd-rolesync-unused", "private"),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Information).AddProvider(Logs));
        services.AddInfrastructure();

        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (!server.IsAvailable)
        {
            return;
        }

        if (_services is not null)
        {
            await _services.DisposeAsync();
            _services = null;
        }

        // The provider's pooled connections would keep the database in use.
        SqlConnection.ClearAllPools();

        await ExecuteOnServerAsync(
            $"""
            IF DB_ID(N'{_databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_databaseName}];
            END
            """,
            CancellationToken.None);
    }

    /// <summary>
    /// Empties everything a test in this class can have written: users, roles, grants and the recorded seed
    /// version. <c>AuditLogs</c> is append-only and is not touched; tests read the rows of the role ids they
    /// created, which are never reused.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM [identity].UserRoles;
            DELETE FROM [identity].RoleClaims;
            DELETE FROM [identity].Roles;
            DELETE FROM [identity].Users;
            DELETE FROM ops.Settings WHERE [Key] = N'Seed.Version';
            """,
            TestContext.Current.CancellationToken);

        Logs.Clear();
    }

    /// <summary>Creates a role the way the admin seed does, with the permissions given as its <c>perm</c> claims.</summary>
    public async Task<int> CreateRoleAsync(
        string name,
        IEnumerable<string> permissions,
        bool isSystem = true,
        bool grantsAll = false)
    {
        await using var scope = Services.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var role = new AppRole
        {
            Name = name,
            Description = "Test role.",
            IsSystem = isSystem,
            GrantsAllPermissions = grantsAll,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var created = await roleManager.CreateAsync(role);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));

        foreach (var permission in permissions)
        {
            dbContext.RoleClaims.Add(new IdentityRoleClaim<int>
            {
                RoleId = role.Id,
                ClaimType = RoleClaimConfiguration.PermissionClaimType,
                ClaimValue = permission,
            });
        }

        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return role.Id;
    }

    /// <summary>Creates a staff user (no password: nothing here signs in) holding the named role.</summary>
    public async Task<int> CreateUserInRoleAsync(string email, string roleName)
    {
        await using var scope = Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = "Test",
            LastName = "Staff",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var created = await userManager.CreateAsync(user);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));

        var added = await userManager.AddToRoleAsync(user, roleName);
        Assert.True(added.Succeeded, string.Join("; ", added.Errors.Select(error => error.Description)));

        return user.Id;
    }

    /// <summary>The <c>perm</c> claim values a role holds right now.</summary>
    public async Task<HashSet<string>> HeldAsync(string roleName)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var values = await dbContext.RoleClaims.AsNoTracking()
            .Where(claim => claim.ClaimType == RoleClaimConfiguration.PermissionClaimType
                && dbContext.Roles.Any(role => role.Id == claim.RoleId && role.Name == roleName))
            .Select(claim => claim.ClaimValue!)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The unique index would refuse a duplicate anyway; a list that differs from its set says it loudly.
        var set = values.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(values.Count, set.Count);
        return set;
    }

    /// <summary>The claim rows of a role as (id, value), to prove a second start inserted and deleted nothing.</summary>
    public async Task<List<(int Id, string Value)>> ClaimRowsAsync(string roleName)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await dbContext.RoleClaims.AsNoTracking()
            .Where(claim => dbContext.Roles.Any(role => role.Id == claim.RoleId && role.Name == roleName))
            .OrderBy(claim => claim.Id)
            .Select(claim => new { claim.Id, claim.ClaimValue })
            .ToListAsync(TestContext.Current.CancellationToken);

        return rows.ConvertAll(row => (row.Id, row.ClaimValue!));
    }

    /// <summary>The value of <c>ops.Settings</c> <c>Seed.Version</c>, or null when there is no such row.</summary>
    public async Task<string?> RecordedVersionAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Settings.AsNoTracking()
            .Where(setting => setting.Key == "Seed.Version")
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Plants a <c>Seed.Version</c> row, as a database that has been through this step before would hold.</summary>
    public async Task RecordVersionAsync(string value)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        dbContext.Settings.Add(new SpencerDenim.Domain.Settings.Setting
        {
            Key = "Seed.Version",
            GroupKey = "Seed",
            ValueType = SpencerDenim.Domain.Settings.SettingValueType.Int,
            Value = value,
            RequiredPermission = "Settings.Security",
            Description = "Planted by a test.",
        });

        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task ExecuteOnServerAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_serverConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The only dynamic part is a database name generated from a GUID in this class.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
