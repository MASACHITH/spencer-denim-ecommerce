using Microsoft.EntityFrameworkCore;

namespace SpencerDenim.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for the platform (doc 08 §10.1).
/// No entities yet: entity configurations are added per feature and picked up from this assembly.
/// </summary>
/// <remarks>
/// Mapping rules for future entity configurations (doc 08 §1.4 and §10.1):
/// <list type="bullet">
/// <item><description>
/// JSON columns are always mapped explicitly with <c>HasColumnType("nvarchar(max)")</c>, including <c>ToJson()</c>
/// mappings, plus an <c>ISJSON(col) = 1</c> check constraint. EF Core must never pick the native <c>json</c> type,
/// which SQL Server 2025 and Azure SQL offer but LocalDB 2019 and bacpac tooling do not handle the same way.
/// </description></item>
/// <item><description>
/// The database collation <c>Latin1_General_100_CI_AS_SC</c> is set when the database is created (provisioning
/// script), never with <c>UseCollation</c>, so migrations do not try to alter it.
/// </description></item>
/// </list>
/// </remarks>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Schema that holds the EF migrations history table (doc 08 §10.1).</summary>
    public const string MigrationsHistorySchema = "ops";

    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
