namespace SpencerDenim.Infrastructure.Persistence;

/// <summary>
/// Database settings. They come from environment variables: <c>ConnectionStrings__Database</c> and
/// optional <c>Database__MaxRetryCount</c> / <c>Database__CommandTimeoutSeconds</c>.
/// Never put the connection string in appsettings files.
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>Name of the connection string entry (<c>ConnectionStrings:Database</c>).</summary>
    public const string ConnectionStringName = "Database";

    /// <summary>Configuration section for the non-secret settings.</summary>
    public const string SectionName = "Database";

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>EF Core retry count for transient SQL errors (doc 08 §10.3).</summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>Command timeout in seconds.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;
}
