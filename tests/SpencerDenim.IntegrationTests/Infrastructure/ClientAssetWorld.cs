using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Media.ClientAssets;
using SpencerDenim.Infrastructure;
using SpencerDenim.Infrastructure.Media.ClientAssets;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// A database and a media volume of its own, for the client-asset import (W1-13).
/// <para>
/// <b>Not the assembly fixture's database.</b> The import rewrites card images, switches homepage
/// sections on and attaches media to departments — exactly the state a dozen catalogue and content
/// tests assert against. Running it on the shared database would make those tests pass or fail
/// depending on which class ran first, which is the kind of coupling that takes a day to find. This
/// costs one database per test and buys an import that is observed from a known starting point every
/// time.
/// </para>
/// <para>
/// The names are this class's own and the directory is created per instance, per doc 14: a shared path
/// is not yours just because it looks like it.
/// </para>
/// </summary>
public sealed class ClientAssetWorld : IAsyncDisposable
{
    private readonly string _serverConnectionString;
    private readonly ServiceProvider _services;

    private ClientAssetWorld(
        string serverConnectionString,
        string databaseName,
        string connectionString,
        string root,
        ServiceProvider services)
    {
        _serverConnectionString = serverConnectionString;
        _services = services;
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        Root = root;
    }

    /// <summary>The throwaway database.</summary>
    public string DatabaseName { get; }

    public string ConnectionString { get; }

    /// <summary>The directory holding both volumes and the fake asset drop.</summary>
    public string Root { get; }

    /// <summary>The public uploads volume.</summary>
    public string PublicRoot => Path.Combine(Root, "public-volume");

    /// <summary>Where <see cref="Drop"/> writes the files an import then reads.</summary>
    public string DropRoot => Path.Combine(Root, "drop");

    /// <summary>What the seeders inserted.</summary>
    public SeedCounts SeedCounts { get; private set; } = new();

    /// <summary>The held-back list this world runs with, or null for none.</summary>
    public ClientAssetHolds? Holds { get; private init; }

    /// <summary>No picture is held back, which is the behaviour every test but one wants.</summary>
    public static ClientAssetHolds NoHolds { get; } = new(new Dictionary<string, HeldBackArtwork>(StringComparer.Ordinal));

    /// <summary>
    /// Builds the world: a database, both volumes, the migrations and the seeded demo catalogue.
    /// </summary>
    /// <param name="withPlaceholderFiles">
    /// Whether to draw the demo placeholder images. True is what a developer's database looks like; it
    /// costs a few hundred encodes, so a test that does not look at a placeholder's pixels passes false.
    /// </param>
    /// <param name="holds">
    /// The held-back-artwork list to run with. Defaults to none, so a test gets the plain behaviour and
    /// the one test that is about holds supplies a checksum it controls — the shipped list is keyed on
    /// the bytes of two real client files, which no test can conjure.
    /// </param>
    public static async Task<ClientAssetWorld> CreateAsync(
        string fixtureConnectionString,
        bool withPlaceholderFiles,
        CancellationToken cancellationToken,
        ClientAssetHolds? holds = null)
    {
        var server = new SqlConnectionStringBuilder(fixtureConnectionString) { InitialCatalog = "master" }
            .ConnectionString;

        var databaseName = "SpencerDenim_W113_" + Guid.NewGuid().ToString("N");
        var connectionString = new SqlConnectionStringBuilder(fixtureConnectionString)
        {
            InitialCatalog = databaseName,
        }.ConnectionString;

        await ExecuteAsync(
            server,
            string.Create(
                CultureInfo.InvariantCulture,
                $"CREATE DATABASE [{databaseName}] COLLATE {SqlServerDatabaseFixture.Collation};"),
            cancellationToken);

        var root = Path.Combine(Path.GetTempPath(), "sd-client-assets-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "public-volume"));
        Directory.CreateDirectory(Path.Combine(root, "private-volume"));
        Directory.CreateDirectory(Path.Combine(root, "drop"));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString,

                // The placeholder generator is a no-op without a volume, which is how a test opts out of
                // a few hundred encodes it does not need.
                ["Media:PublicRoot"] = withPlaceholderFiles ? Path.Combine(root, "public-volume") : string.Empty,
                ["Media:PrivateRoot"] = withPlaceholderFiles ? Path.Combine(root, "private-volume") : string.Empty,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddInfrastructure();
        services.AddSingleton(holds ?? NoHolds);

        var provider = services.BuildServiceProvider();
        var world = new ClientAssetWorld(server, databaseName, connectionString, root, provider)
        {
            Holds = holds,
        };

        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync(cancellationToken);
            world.SeedCounts = await scope.ServiceProvider
                .GetRequiredService<CatalogSeedRunner>()
                .RunAsync(includeDemoCatalogue: true, cancellationToken);
        }

        if (!withPlaceholderFiles)
        {
            // The volumes were held back so the placeholders would not be drawn; the import itself needs
            // them, so they are switched on now, on a second provider over the same database.
            await provider.DisposeAsync();
            return await ReopenWithVolumesAsync(
                server, databaseName, connectionString, root, world.SeedCounts, holds);
        }

        return world;
    }

    /// <summary>Writes a file into the drop, creating the folder.</summary>
    public async Task Drop(string folder, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(DropRoot, folder);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, fileName), content, cancellationToken);
    }

    /// <summary>A complete, well-formed drop: two photographs per named product, and all the home media.</summary>
    public async Task DropTheUsualAsync(
        IReadOnlyList<string> productFileNames,
        CancellationToken cancellationToken)
    {
        foreach (var fileName in productFileNames)
        {
            await Drop("products", fileName, TestImages.UniqueJpeg(1024, 1024), cancellationToken);
        }

        foreach (var department in new[] { "women.jpg", "men.jpg", "kids.jpg" })
        {
            await Drop("departments", department, TestImages.UniqueJpeg(896, 1200), cancellationToken);
        }

        await Drop("home", "hero.mp4", TestVideos.UniqueMp4(960, 540, 10), cancellationToken);
        await Drop("home", "hero-poster.jpg", TestImages.UniqueJpeg(1376, 768), cancellationToken);
        await Drop("home", "campaign.jpg", TestImages.UniqueJpeg(1376, 768), cancellationToken);
        await Drop("home", "activewear.jpg", TestImages.UniqueJpeg(896, 1200), cancellationToken);

        for (var index = 1; index <= 4; index++)
        {
            await Drop(
                "home",
                string.Create(CultureInfo.InvariantCulture, $"trending-{index}.jpg.jpg"),
                TestImages.UniqueJpeg(896, 1200),
                cancellationToken);
        }

        for (var index = 1; index <= 6; index++)
        {
            await Drop(
                "social",
                string.Create(CultureInfo.InvariantCulture, $"social-{index}.jpg.jpg"),
                TestImages.UniqueJpeg(1024, 1024),
                cancellationToken);
        }
    }

    /// <summary>Runs the importer the way the command does, in its own scope.</summary>
    public async Task<ClientAssetImportReport> ImportAsync(
        CancellationToken cancellationToken,
        bool dryRun = false,
        bool replace = false)
    {
        await using var scope = _services.CreateAsyncScope();
        var importer = scope.ServiceProvider.GetRequiredService<IClientAssetImporter>();
        return await importer.ImportAsync(
            new ClientAssetImportRequest(DropRoot, dryRun, replace),
            cancellationToken);
    }

    /// <summary>A scope for reading the result back out of the database.</summary>
    public AsyncServiceScope CreateScope() => _services.CreateAsyncScope();

    /// <summary>Every file on the public volume, as storage keys.</summary>
    public IReadOnlyList<string> PublicFiles() =>
        Directory.Exists(PublicRoot)
            ? Directory
                .EnumerateFiles(PublicRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(PublicRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();

        try
        {
            Directory.Delete(Root, recursive: true);

            // And the shared parent, which is this run's only trace once its own directory is gone.
            // Non-recursive on purpose: it throws when a concurrent run still has a directory in
            // there, which is precisely when it must be left alone. Same rule as SqlServerDatabaseFixture.
            Directory.Delete(Path.GetDirectoryName(Root)!);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        SqlConnection.ClearAllPools();

        await ExecuteAsync(
            _serverConnectionString,
            $"""
            IF DB_ID(N'{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END
            """,
            CancellationToken.None);
    }

    private static Task<ClientAssetWorld> ReopenWithVolumesAsync(
        string server,
        string databaseName,
        string connectionString,
        string root,
        SeedCounts counts,
        ClientAssetHolds? holds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString,
                ["Media:PublicRoot"] = Path.Combine(root, "public-volume"),
                ["Media:PrivateRoot"] = Path.Combine(root, "private-volume"),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddInfrastructure();
        services.AddSingleton(holds ?? NoHolds);

        return Task.FromResult(new ClientAssetWorld(
            server,
            databaseName,
            connectionString,
            root,
            services.BuildServiceProvider())
        {
            SeedCounts = counts,
            Holds = holds,
        });
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
