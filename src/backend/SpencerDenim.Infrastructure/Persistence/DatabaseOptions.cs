namespace SpencerDenim.Infrastructure.Persistence;

/// <summary>
/// Database settings. They come from environment variables or user-secrets: <c>ConnectionStrings__Database</c> and
/// optional <c>Database__MaxRetryCount</c> / <c>Database__CommandTimeoutSeconds</c> / <c>Database__CompatibilityLevel</c>.
/// Never put a connection string with credentials in appsettings files.
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>Name of the connection string entry (<c>ConnectionStrings:Database</c>).</summary>
    public const string ConnectionStringName = "Database";

    /// <summary>Configuration section for the non-secret settings.</summary>
    public const string SectionName = "Database";

    /// <summary>SQL Server 2019 (LocalDB 15.x), the lowest engine the team develops against.</summary>
    public const int DefaultCompatibilityLevel = 150;

    /// <summary>SQL Server 2008: the lowest level EF Core accepts.</summary>
    public const int MinimumCompatibilityLevel = 100;

    /// <summary>SQL Server 2025 / Azure SQL Database: the production target (doc 08 §10.5).</summary>
    public const int MaximumCompatibilityLevel = 170;

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>EF Core retry count for transient SQL errors (doc 08 §10.3).</summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>Command timeout in seconds.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// SQL Server compatibility level EF Core generates SQL for (<c>Database__CompatibilityLevel</c>).
    /// Default 150 for LocalDB 2019; set 170 for SQL Server 2025 Express and Azure SQL Database.
    /// It must not be higher than the level of the database the API connects to.
    /// </summary>
    public int CompatibilityLevel { get; set; } = DefaultCompatibilityLevel;
}
