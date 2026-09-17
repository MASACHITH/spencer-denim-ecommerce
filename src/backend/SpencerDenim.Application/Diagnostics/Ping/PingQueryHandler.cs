using System.Reflection;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;

namespace SpencerDenim.Application.Diagnostics.Ping;

/// <summary>Answers the ping query. It does not touch the database (readiness is covered by health checks).</summary>
public sealed class PingQueryHandler(IClock clock) : IQueryHandler<PingQuery, PingResponse>
{
    public const string ServiceName = "SpencerDenim.Api";
    public const string OkStatus = "ok";

    private static readonly string BuildVersion = ResolveVersion();

    public Task<PingResponse> HandleAsync(PingQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var response = new PingResponse(OkStatus, ServiceName, BuildVersion, clock.UtcNow.ToUniversalTime());
        return Task.FromResult(response);
    }

    private static string ResolveVersion()
    {
        var assembly = typeof(PingQueryHandler).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Strip the "+<commit sha>" suffix to keep the value short.
        return informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
