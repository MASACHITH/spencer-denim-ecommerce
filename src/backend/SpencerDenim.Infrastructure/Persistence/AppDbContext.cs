using Microsoft.EntityFrameworkCore;

namespace SpencerDenim.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for the platform (doc 08 §10.1).
/// No entities yet: entity configurations are added per feature and picked up from this assembly.
/// </summary>
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
