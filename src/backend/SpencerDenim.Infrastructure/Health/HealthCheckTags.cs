namespace SpencerDenim.Infrastructure.Health;

/// <summary>Tags that split health checks into liveness and readiness probes.</summary>
public static class HealthCheckTags
{
    /// <summary>The process is up and can answer HTTP (<c>/health</c>).</summary>
    public const string Live = "live";

    /// <summary>Dependencies such as SQL Server are reachable (<c>/health/ready</c>).</summary>
    public const string Ready = "ready";
}
